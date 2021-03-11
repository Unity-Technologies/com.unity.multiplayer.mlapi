using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using UnityEngine;
using System.Linq;
using MLAPI.Logging;
using UnityEngine.SceneManagement;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Internal;
using MLAPI.Profiling;
using MLAPI.Serialization;
using MLAPI.Transports;
using MLAPI.Connection;
using MLAPI.Messaging;
using MLAPI.SceneManagement;
using MLAPI.Serialization.Pooled;
using MLAPI.Spawning;
using MLAPI.Exceptions;
using MLAPI.Transports.Tasks;
using MLAPI.Messaging.Buffering;
using Unity.Profiling;

namespace MLAPI
{
    /// <summary>
    /// The main component of the library
    /// </summary>
    [AddComponentMenu("MLAPI/NetworkManager", -100)]
    public class NetworkManager : MonoBehaviour, INetworkUpdateSystem
    {
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `public`
        internal static readonly Dictionary<uint, Action<NetworkBehaviour, NetworkSerializer, __RpcParams>> __ntable = new Dictionary<uint, Action<NetworkBehaviour, NetworkSerializer, __RpcParams>>();
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public static readonly Dictionary<uint, Action<NetworkBehaviour, NetworkSerializer, __RpcParams>> __ntable = new Dictionary<uint, Action<NetworkBehaviour, NetworkSerializer, __RpcParams>>();
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private static ProfilerMarker s_EventTick = new ProfilerMarker($"{nameof(NetworkManager)}.EventTick");
        private static ProfilerMarker s_ReceiveTick = new ProfilerMarker($"{nameof(NetworkManager)}.ReceiveTick");
        private static ProfilerMarker s_SyncTime = new ProfilerMarker($"{nameof(NetworkManager)}.SyncTime");
        private static ProfilerMarker s_TransportConnect = new ProfilerMarker($"{nameof(NetworkManager)}.TransportConnect");
        private static ProfilerMarker s_HandleIncomingData = new ProfilerMarker($"{nameof(NetworkManager)}.{nameof(HandleIncomingData)}");
        private static ProfilerMarker s_TransportDisconnect = new ProfilerMarker($"{nameof(NetworkManager)}.TransportDisconnect");

        private static ProfilerMarker s_InvokeRpc = new ProfilerMarker($"{nameof(NetworkManager)}.{nameof(InvokeRpc)}");
#endif

        internal RpcQueueContainer RpcQueueContainer { get; private set; }
        internal NetworkTickSystem NetworkTickSystem { get; private set; }

        public delegate void PerformanceDataEventHandler(PerformanceTickData profilerData);

        public static event PerformanceDataEventHandler OnPerformanceDataEvent;

        /// <summary>
        /// A synchronized time, represents the time in seconds since the server application started. Is replicated across all clients
        /// </summary>
        public float NetworkTime => Time.unscaledTime + m_CurrentNetworkTimeOffset;

        private float m_NetworkTimeOffset;
        private float m_CurrentNetworkTimeOffset;

        /// <summary>
        /// Gets or sets if the NetworkManager should be marked as DontDestroyOnLoad
        /// </summary>
        [HideInInspector]
        public bool DontDestroy = true;

        /// <summary>
        /// Gets or sets if the application should be set to run in background
        /// </summary>
        [HideInInspector]
        public bool RunInBackground = true;

        /// <summary>
        /// The log level to use
        /// </summary>
        [HideInInspector]
        public LogLevel LogLevel = LogLevel.Normal;

        /// <summary>
        /// The singleton instance of the NetworkManager
        /// </summary>
        public static NetworkManager Singleton { get; private set; }

        /// <summary>
        /// Gets the networkId of the server
        /// </summary>
        public ulong ServerClientId => NetworkConfig.NetworkTransport?.ServerClientId ?? throw new NullReferenceException($"The transport in the active {nameof(NetworkConfig)} is null");

        /// <summary>
        /// The clientId the server calls the local client by, only valid for clients
        /// </summary>
        public ulong LocalClientId
        {
            get => IsServer ? NetworkConfig.NetworkTransport.ServerClientId : m_LocalClientId;
            internal set => m_LocalClientId = value;
        }

        private ulong m_LocalClientId;

        /// <summary>
        /// Gets a dictionary of connected clients and their clientId keys. This is only populated on the server.
        /// </summary>
        public readonly Dictionary<ulong, NetworkClient> ConnectedClients = new Dictionary<ulong, NetworkClient>();

        /// <summary>
        /// Gets a list of connected clients. This is only populated on the server.
        /// </summary>
        public readonly List<NetworkClient> ConnectedClientsList = new List<NetworkClient>();

        /// <summary>
        /// Gets a dictionary of the clients that have been accepted by the transport but are still pending by the MLAPI. This is only populated on the server.
        /// </summary>
        public readonly Dictionary<ulong, PendingClient> PendingClients = new Dictionary<ulong, PendingClient>();

        /// <summary>
        /// Gets Whether or not a server is running
        /// </summary>
        public bool IsServer { get; internal set; }

        /// <summary>
        /// Gets Whether or not a client is running
        /// </summary>
        public bool IsClient { get; internal set; }

        /// <summary>
        /// Gets if we are running as host
        /// </summary>
        public bool IsHost => IsServer && IsClient;

        /// <summary>
        /// Gets Whether or not we are listening for connections
        /// </summary>
        public bool IsListening { get; internal set; }

        /// <summary>
        /// Gets if we are connected as a client
        /// </summary>
        public bool IsConnectedClient { get; internal set; }

        /// <summary>
        /// The callback to invoke once a client connects. This callback is only ran on the server and on the local client that connects.
        /// </summary>
        public event Action<ulong> OnClientConnectedCallback = null;

        internal void InvokeOnClientConnectedCallback(ulong clientId) => OnClientConnectedCallback?.Invoke(clientId);

        /// <summary>
        /// The callback to invoke when a client disconnects. This callback is only ran on the server and on the local client that disconnects.
        /// </summary>
        public event Action<ulong> OnClientDisconnectCallback = null;

        internal void InvokeOnClientDisconnectCallback(ulong clientId) => OnClientDisconnectCallback?.Invoke(clientId);

        /// <summary>
        /// The callback to invoke once the server is ready
        /// </summary>
        public event Action OnServerStarted = null;

        /// <summary>
        /// Delegate type called when connection has been approved. This only has to be set on the server.
        /// </summary>
        /// <param name="createPlayerObject">If true, a player object will be created. Otherwise the client will have no object.</param>
        /// <param name="playerPrefabHash">The prefabHash to use for the client. If createPlayerObject is false, this is ignored. If playerPrefabHash is null, the default player prefab is used.</param>
        /// <param name="approved">Whether or not the client was approved</param>
        /// <param name="position">The position to spawn the client at. If null, the prefab position is used.</param>
        /// <param name="rotation">The rotation to spawn the client with. If null, the prefab position is used.</param>
        public delegate void ConnectionApprovedDelegate(bool createPlayerObject, ulong? playerPrefabHash, bool approved, Vector3? position, Quaternion? rotation);

        /// <summary>
        /// The callback to invoke during connection approval
        /// </summary>
        public event Action<byte[], ulong, ConnectionApprovedDelegate> ConnectionApprovalCallback = null;

        internal void InvokeConnectionApproval(byte[] payload, ulong clientId, ConnectionApprovedDelegate action) => ConnectionApprovalCallback?.Invoke(payload, clientId, action);

        /// <summary>
        /// The current NetworkConfig
        /// </summary>
        [HideInInspector]
        public NetworkConfig NetworkConfig;

        /// <summary>
        /// The current hostname we are connected to, used to validate certificate
        /// </summary>
        public string ConnectedHostname { get; private set; }

        internal static event Action OnSingletonReady;

        private void OnValidate()
        {
            if (NetworkConfig == null) return; //May occur when the component is added

            if (GetComponentInChildren<NetworkObject>() != null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning($"{nameof(NetworkManager)} cannot be a {nameof(NetworkObject)}.");
                }
            }

            if (!NetworkConfig.RegisteredScenes.Contains(SceneManager.GetActiveScene().name))
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Active scene is not registered as a network scene. The MLAPI has added it");
                NetworkConfig.RegisteredScenes.Add(SceneManager.GetActiveScene().name);
            }

            for (int i = 0; i < NetworkConfig.NetworkPrefabs.Count; i++)
            {
                if (NetworkConfig.NetworkPrefabs[i] != null && NetworkConfig.NetworkPrefabs[i].Prefab != null)
                {
                    if (NetworkConfig.NetworkPrefabs[i].Prefab.GetComponent<NetworkObject>() == null)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                        {
                            NetworkLog.LogWarning($"{nameof(NetworkPrefab)} [{i}] does not have a {nameof(NetworkObject)} component");
                        }
                    }
                    else
                    {
                        NetworkConfig.NetworkPrefabs[i].Prefab.GetComponent<NetworkObject>().ValidateHash();
                    }
                }
            }

            // TODO: Show which two prefab generators that collide
            var hashes = new HashSet<ulong>();

            for (int i = 0; i < NetworkConfig.NetworkPrefabs.Count; i++)
            {
                if (hashes.Contains(NetworkConfig.NetworkPrefabs[i].Hash))
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        var prefabHashGenerator = NetworkConfig.NetworkPrefabs[i].Prefab.GetComponent<NetworkObject>().PrefabHashGenerator;
                        NetworkLog.LogError($"PrefabHash collision! You have two prefabs with the same hash ({nameof(NetworkObject.PrefabHashGenerator)} = {prefabHashGenerator}). This is not supported");
                    }
                }

                hashes.Add(NetworkConfig.NetworkPrefabs[i].Hash);
            }

            int playerPrefabCount = NetworkConfig.NetworkPrefabs.Count(x => x.PlayerPrefab);

            if (playerPrefabCount == 0 && !NetworkConfig.ConnectionApproval && NetworkConfig.CreatePlayerPrefab)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning($"There is no {nameof(NetworkPrefab)} marked as a {nameof(NetworkPrefab.PlayerPrefab)}");
                }
            }
            else if (playerPrefabCount > 1)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning($"Only one {nameof(NetworkPrefab)} can be marked as a {nameof(NetworkPrefab.PlayerPrefab)}");
                }
            }

            var networkPrefab = NetworkConfig.NetworkPrefabs.FirstOrDefault(x => x.PlayerPrefab);

            if (networkPrefab == null)
            {
                NetworkConfig.PlayerPrefabHash = null;
            }
            else
            {
                if (NetworkConfig.PlayerPrefabHash == null)
                {
                    NetworkConfig.PlayerPrefabHash = new NullableBoolSerializable();
                }

                NetworkConfig.PlayerPrefabHash.Value = networkPrefab.Hash;
            }
        }

        private void Init(bool server)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(Init));

            LocalClientId = 0;
            m_NetworkTimeOffset = 0f;
            m_CurrentNetworkTimeOffset = 0f;
            m_LastReceiveTickTime = 0f;
            m_LastReceiveTickTime = 0f;
            PendingClients.Clear();
            ConnectedClients.Clear();
            ConnectedClientsList.Clear();

            NetworkSpawnManager.SpawnedObjects.Clear();
            NetworkSpawnManager.SpawnedObjectsList.Clear();
            NetworkSpawnManager.ReleasedNetworkObjectIds.Clear();
            NetworkSpawnManager.PendingSoftSyncObjects.Clear();
            NetworkSceneManager.RegisteredSceneNames.Clear();
            NetworkSceneManager.SceneIndexToString.Clear();
            NetworkSceneManager.SceneNameToIndex.Clear();
            NetworkSceneManager.SceneSwitchProgresses.Clear();

            if (NetworkConfig.NetworkTransport == null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("No transport has been selected!");
                return;
            }

            //This 'if' should never enter
            if (NetworkTickSystem != null)
            {
                NetworkTickSystem.Dispose();
                NetworkTickSystem = null;
            }

            NetworkTickSystem = new NetworkTickSystem(NetworkConfig.NetworkTickIntervalSec);

            //This should never happen, but in the event that it does there should be (at a minimum) a unity error logged.
            if (RpcQueueContainer != null)
            {
                UnityEngine.Debug.LogError("Init was invoked, but rpcQueueContainer was already initialized! (destroying previous instance)");
                RpcQueueContainer.Shutdown();
                RpcQueueContainer = null;
            }

            //The RpcQueueContainer must be initialized within the Init method ONLY
            //It should ONLY be shutdown and destroyed in the Shutdown method (other than just above)
            RpcQueueContainer = new RpcQueueContainer(false);

            //Note: Since frame history is not being used, this is set to 0
            //To test frame history, increase the number to (n) where n > 0
            RpcQueueContainer.Initialize(0);

            // Register INetworkUpdateSystem (always register this after rpcQueueContainer has been instantiated)
            this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
            this.RegisterNetworkUpdate(NetworkUpdateStage.PreUpdate);

            if (NetworkConfig.EnableSceneManagement)
            {
                NetworkConfig.RegisteredScenes.Sort(StringComparer.Ordinal);

                for (int i = 0; i < NetworkConfig.RegisteredScenes.Count; i++)
                {
                    NetworkSceneManager.RegisteredSceneNames.Add(NetworkConfig.RegisteredScenes[i]);
                    NetworkSceneManager.SceneIndexToString.Add((uint)i, NetworkConfig.RegisteredScenes[i]);
                    NetworkSceneManager.SceneNameToIndex.Add(NetworkConfig.RegisteredScenes[i], (uint)i);
                }

                NetworkSceneManager.SetCurrentSceneIndex();
            }

            for (int i = 0; i < NetworkConfig.NetworkPrefabs.Count; i++)
            {
                if (NetworkConfig.NetworkPrefabs[i] == null || NetworkConfig.NetworkPrefabs[i].Prefab == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogError($"{nameof(NetworkPrefab)} cannot be null ({nameof(NetworkPrefab)} at index: {i})");
                    }
                }
                else if (NetworkConfig.NetworkPrefabs[i].Prefab.GetComponent<NetworkObject>() == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogError($"{nameof(NetworkPrefab)} (\"{NetworkConfig.NetworkPrefabs[i].Prefab.name}\") is missing a {nameof(NetworkObject)} component");
                    }
                }
                else
                {
                    NetworkConfig.NetworkPrefabs[i].Prefab.GetComponent<NetworkObject>().ValidateHash();
                }
            }

            NetworkConfig.NetworkTransport.OnTransportEvent += HandleRawTransportPoll;

            NetworkConfig.NetworkTransport.ResetChannelCache();

            NetworkConfig.NetworkTransport.Init();
        }

        /// <summary>
        /// Starts a server
        /// </summary>
        public SocketTasks StartServer()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StartServer()");
            if (IsServer || IsClient)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Cannot start server while an instance is already running");
                return SocketTask.Fault.AsTasks();
            }

            if (NetworkConfig.ConnectionApproval)
            {
                if (ConnectionApprovalCallback == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("No ConnectionApproval callback defined. Connection approval will timeout");
                }
            }

            Init(true);

            var socketTasks = NetworkConfig.NetworkTransport.StartServer();

            IsServer = true;
            IsClient = false;
            IsListening = true;

            NetworkSpawnManager.ServerSpawnSceneObjectsOnStartSweep();

            OnServerStarted?.Invoke();

            return socketTasks;
        }

        /// <summary>
        /// Starts a client
        /// </summary>
        public SocketTasks StartClient()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(StartClient));

            if (IsServer || IsClient)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Cannot start client while an instance is already running");
                return SocketTask.Fault.AsTasks();
            }

            Init(false);

            var socketTasks = NetworkConfig.NetworkTransport.StartClient();

            IsServer = false;
            IsClient = true;
            IsListening = true;

            return socketTasks;
        }

        /// <summary>
        /// Stops the running server
        /// </summary>
        public void StopServer()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(StopServer));
            var disconnectedIds = new HashSet<ulong>();
            //Don't know if I have to disconnect the clients. I'm assuming the NetworkTransport does all the cleaning on shtudown. But this way the clients get a disconnect message from server (so long it does't get lost)

            foreach (KeyValuePair<ulong, NetworkClient> pair in ConnectedClients)
            {
                if (!disconnectedIds.Contains(pair.Key))
                {
                    disconnectedIds.Add(pair.Key);

                    if (pair.Key == NetworkConfig.NetworkTransport.ServerClientId)
                        continue;

                    NetworkConfig.NetworkTransport.DisconnectRemoteClient(pair.Key);
                }
            }

            foreach (KeyValuePair<ulong, PendingClient> pair in PendingClients)
            {
                if (!disconnectedIds.Contains(pair.Key))
                {
                    disconnectedIds.Add(pair.Key);
                    if (pair.Key == NetworkConfig.NetworkTransport.ServerClientId) continue;

                    NetworkConfig.NetworkTransport.DisconnectRemoteClient(pair.Key);
                }
            }

            IsServer = false;
            Shutdown();
        }

        /// <summary>
        /// Stops the running host
        /// </summary>
        public void StopHost()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(StopHost));
            IsServer = false;
            IsClient = false;
            StopServer();
            //We don't stop client since we dont actually have a transport connection to our own host. We just handle host messages directly in the MLAPI
        }

        /// <summary>
        /// Stops the running client
        /// </summary>
        public void StopClient()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(StopClient));
            IsClient = false;
            NetworkConfig.NetworkTransport.DisconnectLocalClient();
            IsConnectedClient = false;
            Shutdown();
        }

        /// <summary>
        /// Starts a Host
        /// </summary>
        public SocketTasks StartHost(Vector3? position = null, Quaternion? rotation = null, bool? createPlayerObject = null, ulong? prefabHash = null, Stream payloadStream = null)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(StartHost));

            if (IsServer || IsClient)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Cannot start host while an instance is already running");
                return SocketTask.Fault.AsTasks();
            }

            if (NetworkConfig.ConnectionApproval)
            {
                if (ConnectionApprovalCallback == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("No ConnectionApproval callback defined. Connection approval will timeout");
                }
            }

            Init(true);

            var socketTasks = NetworkConfig.NetworkTransport.StartServer();

            IsServer = true;
            IsClient = true;
            IsListening = true;

            ulong hostClientId = NetworkConfig.NetworkTransport.ServerClientId;

            ConnectedClients.Add(hostClientId, new NetworkClient()
            {
                ClientId = hostClientId
            });

            ConnectedClientsList.Add(ConnectedClients[hostClientId]);

            if ((createPlayerObject == null && NetworkConfig.CreatePlayerPrefab) || (createPlayerObject != null && createPlayerObject.Value))
            {
                var networkObject = NetworkSpawnManager.CreateLocalNetworkObject(false, 0, prefabHash ?? NetworkConfig.PlayerPrefabHash.Value, null, position, rotation);
                NetworkSpawnManager.SpawnNetworkObjectLocally(networkObject, NetworkSpawnManager.GetNetworkObjectId(), false, true, hostClientId, payloadStream, payloadStream != null, payloadStream == null ? 0 : (int)payloadStream.Length, false, false);

                if (networkObject.CheckObjectVisibility == null || networkObject.CheckObjectVisibility(hostClientId))
                {
                    networkObject.m_Observers.Add(hostClientId);
                }
            }

            NetworkSpawnManager.ServerSpawnSceneObjectsOnStartSweep();

            OnServerStarted?.Invoke();

            return socketTasks;
        }

        public void SetSingleton()
        {
            Singleton = this;

            OnSingletonReady?.Invoke();
        }

        private void OnEnable()
        {
            if (Singleton != null && Singleton != this)
            {
                Destroy(gameObject);
                return;
            }

            SetSingleton();

            if (DontDestroy) DontDestroyOnLoad(gameObject);
            if (RunInBackground) Application.runInBackground = true;
        }

        private void OnDestroy()
        {
            if (Singleton != null && Singleton == this)
            {
                Shutdown();
                Singleton = null;
            }
        }

        public void Shutdown()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo(nameof(Shutdown));

            // Unregister INetworkUpdateSystem before shutting down the RpcQueueContainer
            this.UnregisterAllNetworkUpdates();

            //If an instance of the RpcQueueContainer is still around, then shut it down and remove the reference
            if (RpcQueueContainer != null)
            {
                RpcQueueContainer.Shutdown();
                RpcQueueContainer = null;
            }

            if (NetworkTickSystem != null)
            {
                NetworkTickSystem.Dispose();
                NetworkTickSystem = null;
            }

#if !UNITY_2020_2_OR_LATER
            NetworkProfiler.Stop();
#endif
            IsListening = false;
            IsServer = false;
            IsClient = false;
            NetworkConfig.NetworkTransport.OnTransportEvent -= HandleRawTransportPoll;
            NetworkSpawnManager.DestroyNonSceneObjects();
            NetworkSpawnManager.ServerResetShudownStateForSceneObjects();

            //The Transport is set during Init time, thus it is possible for the Transport to be null
            NetworkConfig?.NetworkTransport?.Shutdown();
        }

        // INetworkUpdateSystem
        public void NetworkUpdate(NetworkUpdateStage updateStage)
        {
            switch (updateStage)
            {
                case NetworkUpdateStage.EarlyUpdate:
                    OnNetworkEarlyUpdate();
                    break;
                case NetworkUpdateStage.PreUpdate:
                    OnNetworkPreUpdate();
                    break;
            }
        }

        private float m_LastReceiveTickTime;
        private float m_LastEventTickTime;
        private float m_LastTimeSyncTime;

        private void OnNetworkEarlyUpdate()
        {
            ProfilerBeginTick();

            if (IsListening)
            {
                // Process received data
                if ((NetworkTime - m_LastReceiveTickTime >= (1f / NetworkConfig.ReceiveTickrate)) || NetworkConfig.ReceiveTickrate <= 0)
                {
                    PerformanceDataManager.Increment(ProfilerConstants.ReceiveTickRate);
                    ProfilerStatManager.RcvTickRate.Record();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_ReceiveTick.Begin();
#endif
                    var isLoopBack = false;

#if !UNITY_2020_2_OR_LATER
                    NetworkProfiler.StartTick(TickType.Receive);
#endif

                    //If we are in loopback mode, we don't need to touch the transport
                    if (!isLoopBack)
                    {
                        NetworkEvent networkEvent;
                        int processedEvents = 0;
                        do
                        {
                            processedEvents++;
                            networkEvent = NetworkConfig.NetworkTransport.PollEvent(out ulong clientId, out NetworkChannel networkChannel, out ArraySegment<byte> payload, out float receiveTime);
                            HandleRawTransportPoll(networkEvent, clientId, networkChannel, payload, receiveTime);

                            // Only do another iteration if: there are no more messages AND (there is no limit to max events or we have processed less than the maximum)
                        } while (IsListening && (networkEvent != NetworkEvent.Nothing) && (NetworkConfig.MaxReceiveEventsPerTickRate <= 0 || processedEvents < NetworkConfig.MaxReceiveEventsPerTickRate));
                    }

                    m_LastReceiveTickTime = NetworkTime;

#if !UNITY_2020_2_OR_LATER
                    NetworkProfiler.EndTick();
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_ReceiveTick.End();
#endif
                }
            }
        }

        private void OnNetworkPreUpdate()
        {
            if (IsListening)
            {
                if (((NetworkTime - m_LastEventTickTime >= (1f / NetworkConfig.EventTickrate))))
                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_EventTick.Begin();
#endif
#if UNITY_EDITOR && !UNITY_2020_2_OR_LATER
                    NetworkProfiler.StartTick(TickType.Event);
#endif

                    if (NetworkConfig.EnableNetworkVariable)
                    {
                        // Do NetworkVariable updates
                        NetworkBehaviour.NetworkBehaviourUpdate();
                    }

                    if (!IsServer && NetworkConfig.EnableMessageBuffering)
                    {
                        BufferManager.CleanBuffer();
                    }

                    if (IsServer)
                    {
                        m_LastEventTickTime = NetworkTime;
                    }
#if UNITY_EDITOR && !UNITY_2020_2_OR_LATER
                    NetworkProfiler.EndTick();
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_EventTick.End();
#endif
                }

                if (IsServer && NetworkConfig.EnableTimeResync && NetworkTime - m_LastTimeSyncTime >= NetworkConfig.TimeResyncInterval)
                {
#if UNITY_EDITOR && !UNITY_2020_2_OR_LATER
                    NetworkProfiler.StartTick(TickType.Event);
#endif
                    SyncTime();
                    m_LastTimeSyncTime = NetworkTime;
#if UNITY_EDITOR && !UNITY_2020_2_OR_LATER
                    NetworkProfiler.EndTick();
#endif
                }

                if (!Mathf.Approximately(m_NetworkTimeOffset, m_CurrentNetworkTimeOffset))
                {
                    // Smear network time adjustments by no more than 200ms per second.  This should help code deal with
                    // changes more gracefully, since the network time will always flow forward at a reasonable pace.
                    float maxDelta = Mathf.Max(0.001f, 0.2f * Time.unscaledDeltaTime);
                    m_CurrentNetworkTimeOffset += Mathf.Clamp(m_NetworkTimeOffset - m_CurrentNetworkTimeOffset, -maxDelta, maxDelta);
                }
            }

            NotifyProfilerListeners();
        }

        internal void UpdateNetworkTime(ulong clientId, float netTime, float receiveTime, bool warp = false)
        {
            float rtt = NetworkConfig.NetworkTransport.GetCurrentRtt(clientId) / 1000f;
            m_NetworkTimeOffset = netTime - receiveTime + rtt / 2f;

            if (warp)
            {
                m_CurrentNetworkTimeOffset = m_NetworkTimeOffset;
            }

            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo($"Received network time {netTime}, RTT to server is {rtt}, {(warp ? "setting" : "smearing")} offset to {m_NetworkTimeOffset} (delta {m_NetworkTimeOffset - m_CurrentNetworkTimeOffset})");
            }
        }

        private void SendConnectionRequest()
        {
            using (var buffer = PooledNetworkBuffer.Get())
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteUInt64Packed(NetworkConfig.GetConfig());

                if (NetworkConfig.ConnectionApproval)
                {
                    writer.WriteByteArray(NetworkConfig.ConnectionData);
                }

                InternalMessageSender.Send(ServerClientId, NetworkConstants.CONNECTION_REQUEST, NetworkChannel.Internal, buffer);
            }
        }

        private IEnumerator ApprovalTimeout(ulong clientId)
        {
            float timeStarted = NetworkTime;

            //We yield every frame incase a pending client disconnects and someone else gets its connection id
            while (NetworkTime - timeStarted < NetworkConfig.ClientConnectionBufferTimeout && PendingClients.ContainsKey(clientId))
            {
                yield return null;
            }

            if (PendingClients.ContainsKey(clientId) && !ConnectedClients.ContainsKey(clientId))
            {
                // Timeout
                if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                {
                    NetworkLog.LogInfo($"Client {clientId} Handshake Timed Out");
                }

                DisconnectClient(clientId);
            }
        }

        internal IEnumerator TimeOutSwitchSceneProgress(SceneSwitchProgress switchSceneProgress)
        {
            yield return new WaitForSecondsRealtime(NetworkConfig.LoadSceneTimeOut);
            switchSceneProgress.SetTimedOut();
        }

        private void HandleRawTransportPoll(NetworkEvent networkEvent, ulong clientId, NetworkChannel networkChannel, ArraySegment<byte> payload, float receiveTime)
        {
            PerformanceDataManager.Increment(ProfilerConstants.NumberBytesReceived, payload.Count);
            ProfilerStatManager.BytesRcvd.Record(payload.Count);

            switch (networkEvent)
            {
                case NetworkEvent.Connect:
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportConnect.Begin();
#endif
#if !UNITY_2020_2_OR_LATER
                    NetworkProfiler.StartEvent(TickType.Receive, (uint)payload.Count, networkChannel, "TRANSPORT_CONNECT");
#endif
                    if (IsServer)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                        {
                            NetworkLog.LogInfo("Client Connected");
                        }

                        PendingClients.Add(clientId, new PendingClient()
                        {
                            ClientId = clientId,
                            ConnectionState = PendingClient.State.PendingConnection
                        });

                        StartCoroutine(ApprovalTimeout(clientId));
                    }
                    else
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                        {
                            NetworkLog.LogInfo("Connected");
                        }

                        SendConnectionRequest();
                        StartCoroutine(ApprovalTimeout(clientId));
                    }

#if !UNITY_2020_2_OR_LATER
                    NetworkProfiler.EndEvent();
#endif
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportConnect.End();
#endif
                    break;
                case NetworkEvent.Data:
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                    {
                        NetworkLog.LogInfo($"Incoming Data From {clientId}: {payload.Count} bytes");
                    }

                    HandleIncomingData(clientId, networkChannel, payload, receiveTime, true);
                    break;
                }
                case NetworkEvent.Disconnect:
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportDisconnect.Begin();
#endif
#if !UNITY_2020_2_OR_LATER
                    NetworkProfiler.StartEvent(TickType.Receive, 0, NetworkChannel.Internal, "TRANSPORT_DISCONNECT");
#endif

                    if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                    {
                        NetworkLog.LogInfo($"Disconnect Event From {clientId}");
                    }

                    if (IsServer) OnClientDisconnectFromServer(clientId);
                    else
                    {
                        IsConnectedClient = false;
                        StopClient();
                    }

                    OnClientDisconnectCallback?.Invoke(clientId);

#if !UNITY_2020_2_OR_LATER
                    NetworkProfiler.EndEvent();
#endif
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportDisconnect.End();
#endif
                    break;
            }
        }

        private readonly NetworkBuffer m_InputBufferWrapper = new NetworkBuffer(new byte[0]);
        private readonly RpcBatcher m_RpcBatcher = new RpcBatcher();

        internal void HandleIncomingData(ulong clientId, NetworkChannel networkChannel, ArraySegment<byte> data, float receiveTime, bool allowBuffer)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleIncomingData.Begin();
#endif
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo("Unwrapping Data Header");
            }

            m_InputBufferWrapper.SetTarget(data.Array);
            m_InputBufferWrapper.SetLength(data.Count + data.Offset);
            m_InputBufferWrapper.Position = data.Offset;

            using (var messageStream = MessagePacker.UnwrapMessage(m_InputBufferWrapper, out byte messageType))
            {
                if (messageStream == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogError("Message unwrap could not be completed. Was the header corrupt?");
                    }

                    return;
                }

                if (messageType == NetworkConstants.INVALID)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogError($"Message unwrap read an invalid {nameof(messageType)}");
                    }

                    return;
                }

                uint headerByteSize = (uint)Arithmetic.VarIntSize(messageType);

#if !UNITY_2020_2_OR_LATER
                NetworkProfiler.StartEvent(TickType.Receive, (uint)(data.Count - headerByteSize), networkChannel, messageType);
#endif

                if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                {
                    NetworkLog.LogInfo($"Data Header: {nameof(messageType)}={messageType}");
                }

                // Client tried to send a network message that was not the connection request before he was accepted.
                if (PendingClients.ContainsKey(clientId) && PendingClients[clientId].ConnectionState == PendingClient.State.PendingConnection && messageType != NetworkConstants.CONNECTION_REQUEST)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        NetworkLog.LogWarning($"Message received from {nameof(clientId)}={clientId} before it has been accepted");
                    }

                    return;
                }

                #region INTERNAL MESSAGE

                switch (messageType)
                {
                    case NetworkConstants.CONNECTION_REQUEST:
                        if (IsServer) InternalMessageHandler.HandleConnectionRequest(clientId, messageStream);
                        break;
                    case NetworkConstants.CONNECTION_APPROVED:
                        if (IsClient) InternalMessageHandler.HandleConnectionApproved(clientId, messageStream, receiveTime);
                        break;
                    case NetworkConstants.ADD_OBJECT:
                        if (IsClient) InternalMessageHandler.HandleAddObject(clientId, messageStream);
                        break;
                    case NetworkConstants.DESTROY_OBJECT:
                        if (IsClient) InternalMessageHandler.HandleDestroyObject(clientId, messageStream);
                        break;
                    case NetworkConstants.SWITCH_SCENE:
                        if (IsClient) InternalMessageHandler.HandleSwitchScene(clientId, messageStream);
                        break;
                    case NetworkConstants.CHANGE_OWNER:
                        if (IsClient) InternalMessageHandler.HandleChangeOwner(clientId, messageStream);
                        break;
                    case NetworkConstants.ADD_OBJECTS:
                        if (IsClient) InternalMessageHandler.HandleAddObjects(clientId, messageStream);
                        break;
                    case NetworkConstants.DESTROY_OBJECTS:
                        if (IsClient) InternalMessageHandler.HandleDestroyObjects(clientId, messageStream);
                        break;
                    case NetworkConstants.TIME_SYNC:
                        if (IsClient) InternalMessageHandler.HandleTimeSync(clientId, messageStream, receiveTime);
                        break;
                    case NetworkConstants.NETWORK_VARIABLE_DELTA:
                        InternalMessageHandler.HandleNetworkVariableDelta(clientId, messageStream, BufferCallback, new PreBufferPreset()
                        {
                            AllowBuffer = allowBuffer,
                            NetworkChannel = networkChannel,
                            ClientId = clientId,
                            Data = data,
                            MessageType = messageType,
                            ReceiveTime = receiveTime
                        });
                        break;
                    case NetworkConstants.NETWORK_VARIABLE_UPDATE:
                        InternalMessageHandler.HandleNetworkVariableUpdate(clientId, messageStream, BufferCallback, new PreBufferPreset()
                        {
                            AllowBuffer = allowBuffer,
                            NetworkChannel = networkChannel,
                            ClientId = clientId,
                            Data = data,
                            MessageType = messageType,
                            ReceiveTime = receiveTime
                        });
                        break;
                    case NetworkConstants.UNNAMED_MESSAGE:
                        InternalMessageHandler.HandleUnnamedMessage(clientId, messageStream);
                        break;
                    case NetworkConstants.NAMED_MESSAGE:
                        InternalMessageHandler.HandleNamedMessage(clientId, messageStream);
                        break;
                    case NetworkConstants.CLIENT_SWITCH_SCENE_COMPLETED:
                        if (IsServer && NetworkConfig.EnableSceneManagement) InternalMessageHandler.HandleClientSwitchSceneCompleted(clientId, messageStream);
                        break;
                    case NetworkConstants.SERVER_LOG:
                        if (IsServer && NetworkConfig.EnableNetworkLogs) InternalMessageHandler.HandleNetworkLog(clientId, messageStream);
                        break;
                    case NetworkConstants.SERVER_RPC:
                    {
                        if (IsServer)
                        {
                            if (RpcQueueContainer.IsUsingBatching())
                            {
                                m_RpcBatcher.ReceiveItems(messageStream, ReceiveCallback, RpcQueueContainer.QueueItemType.ServerRpc, clientId, receiveTime);
                                ProfilerStatManager.RpcBatchesRcvd.Record();
                                PerformanceDataManager.Increment(ProfilerConstants.NumberOfRPCBatchesReceived);
                            }
                            else
                            {
                                InternalMessageHandler.RpcReceiveQueueItem(clientId, messageStream, receiveTime, RpcQueueContainer.QueueItemType.ServerRpc);
                            }
                        }

                        break;
                    }
                    case NetworkConstants.CLIENT_RPC:
                    {
                        if (IsClient)
                        {
                            if (RpcQueueContainer.IsUsingBatching())
                            {
                                m_RpcBatcher.ReceiveItems(messageStream, ReceiveCallback, RpcQueueContainer.QueueItemType.ClientRpc, clientId, receiveTime);
                                ProfilerStatManager.RpcBatchesRcvd.Record();
                                PerformanceDataManager.Increment(ProfilerConstants.NumberOfRPCBatchesReceived);
                            }
                            else
                            {
                                InternalMessageHandler.RpcReceiveQueueItem(clientId, messageStream, receiveTime, RpcQueueContainer.QueueItemType.ClientRpc);
                            }
                        }

                        break;
                    }
                    default:
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                        {
                            NetworkLog.LogError($"Read unrecognized {nameof(messageType)}={messageType}");
                        }

                        break;
                }

                #endregion

#if !UNITY_2020_2_OR_LATER
                NetworkProfiler.EndEvent();
#endif
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleIncomingData.End();
#endif
        }

        private static void ReceiveCallback(NetworkBuffer messageBuffer, RpcQueueContainer.QueueItemType messageType, ulong clientId, float receiveTime)
        {
            InternalMessageHandler.RpcReceiveQueueItem(clientId, messageBuffer, receiveTime, messageType);
        }

        /// <summary>
        /// InvokeRPC
        /// Called when an inbound queued RPC is invoked
        /// </summary>
        /// <param name="queueItem">frame queue item to invoke</param>
#pragma warning disable 618
        internal static void InvokeRpc(RpcFrameQueueItem queueItem)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_InvokeRpc.Begin();
#endif
            var networkObjectId = queueItem.NetworkReader.ReadUInt64Packed();
            var networkBehaviourId = queueItem.NetworkReader.ReadUInt16Packed();
            var networkUpdateStage = queueItem.NetworkReader.ReadByteDirect();
            var networkMethodId = queueItem.NetworkReader.ReadUInt32Packed();

            if (__ntable.ContainsKey(networkMethodId))
            {
                if (!NetworkSpawnManager.SpawnedObjects.ContainsKey(networkObjectId)) return;
                var networkObject = NetworkSpawnManager.SpawnedObjects[networkObjectId];

                var networkBehaviour = networkObject.GetNetworkBehaviourAtOrderIndex(networkBehaviourId);
                if (networkBehaviour == null) return;

                var rpcParams = new __RpcParams();
                switch (queueItem.QueueItemType)
                {
                    case RpcQueueContainer.QueueItemType.ServerRpc:
                        rpcParams.Server = new ServerRpcParams
                        {
                            Receive = new ServerRpcReceiveParams
                            {
                                UpdateStage = (NetworkUpdateStage)networkUpdateStage,
                                SenderClientId = queueItem.NetworkId
                            }
                        };
                        break;
                    case RpcQueueContainer.QueueItemType.ClientRpc:
                        rpcParams.Client = new ClientRpcParams
                        {
                            Receive = new ClientRpcReceiveParams
                            {
                                UpdateStage = (NetworkUpdateStage)networkUpdateStage
                            }
                        };
                        break;
                }

                __ntable[networkMethodId](networkBehaviour, new NetworkSerializer(queueItem.NetworkReader), rpcParams);
            }
#pragma warning restore 618

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_InvokeRpc.End();
#endif
        }

        private void BufferCallback(ulong networkId, PreBufferPreset preset)
        {
            if (!preset.AllowBuffer)
            {
                // This is to prevent recursive buffering
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                {
                    NetworkLog.LogError($"A message of type {NetworkConstants.MESSAGE_NAMES[preset.MessageType]} was recursivley buffered. It has been dropped.");
                }

                return;
            }

            if (!NetworkConfig.EnableMessageBuffering)
            {
                throw new InvalidOperationException("Cannot buffer with buffering disabled.");
            }

            if (IsServer)
            {
                throw new InvalidOperationException("Cannot buffer on server.");
            }

            BufferManager.BufferMessageForNetworkId(networkId, preset.ClientId, preset.NetworkChannel, preset.ReceiveTime, preset.Data);
        }

        /// <summary>
        /// Disconnects the remote client.
        /// </summary>
        /// <param name="clientId">The ClientId to disconnect</param>
        public void DisconnectClient(ulong clientId)
        {
            if (!IsServer)
            {
                throw new NotServerException("Only server can disconnect remote clients. Use StopClient instead.");
            }

            if (ConnectedClients.ContainsKey(clientId)) ConnectedClients.Remove(clientId);
            if (PendingClients.ContainsKey(clientId)) PendingClients.Remove(clientId);

            for (int i = ConnectedClientsList.Count - 1; i > -1; i--)
            {
                if (ConnectedClientsList[i].ClientId == clientId)
                {
                    ConnectedClientsList.RemoveAt(i);
                    PerformanceDataManager.Increment(ProfilerConstants.NumberOfConnections, -1);
                    ProfilerStatManager.Connections.Record(-1);
                }
            }

            NetworkConfig.NetworkTransport.DisconnectRemoteClient(clientId);
        }

        internal void OnClientDisconnectFromServer(ulong clientId)
        {
            if (PendingClients.ContainsKey(clientId)) PendingClients.Remove(clientId);

            if (ConnectedClients.ContainsKey(clientId))
            {
                if (IsServer)
                {
                    if (ConnectedClients[clientId].PlayerObject != null)
                    {
                        if (NetworkSpawnManager.CustomDestroyHandlers.ContainsKey(ConnectedClients[clientId].PlayerObject.PrefabHash))
                        {
                            NetworkSpawnManager.CustomDestroyHandlers[ConnectedClients[clientId].PlayerObject.PrefabHash](ConnectedClients[clientId].PlayerObject);
                            NetworkSpawnManager.OnDestroyObject(ConnectedClients[clientId].PlayerObject.NetworkObjectId, false);
                        }
                        else
                        {
                            Destroy(ConnectedClients[clientId].PlayerObject.gameObject);
                        }
                    }

                    for (int i = 0; i < ConnectedClients[clientId].OwnedObjects.Count; i++)
                    {
                        if (ConnectedClients[clientId].OwnedObjects[i] != null)
                        {
                            if (!ConnectedClients[clientId].OwnedObjects[i].DontDestroyWithOwner)
                            {
                                if (NetworkSpawnManager.CustomDestroyHandlers.ContainsKey(ConnectedClients[clientId].OwnedObjects[i].PrefabHash))
                                {
                                    NetworkSpawnManager.CustomDestroyHandlers[ConnectedClients[clientId].OwnedObjects[i].PrefabHash](ConnectedClients[clientId].OwnedObjects[i]);
                                    NetworkSpawnManager.OnDestroyObject(ConnectedClients[clientId].OwnedObjects[i].NetworkObjectId, false);
                                }
                                else
                                {
                                    Destroy(ConnectedClients[clientId].OwnedObjects[i].gameObject);
                                }
                            }
                            else
                            {
                                ConnectedClients[clientId].OwnedObjects[i].RemoveOwnership();
                            }
                        }
                    }

                    // TODO: Could(should?) be replaced with more memory per client, by storing the visiblity

                    foreach (var sobj in NetworkSpawnManager.SpawnedObjectsList)
                    {
                        sobj.m_Observers.Remove(clientId);
                    }
                }

                for (int i = 0; i < ConnectedClientsList.Count; i++)
                {
                    if (ConnectedClientsList[i].ClientId == clientId)
                    {
                        ConnectedClientsList.RemoveAt(i);
                        PerformanceDataManager.Increment(ProfilerConstants.NumberOfConnections, -1);
                        ProfilerStatManager.Connections.Record(-1);
                        break;
                    }
                }

                ConnectedClients.Remove(clientId);
            }
        }

        private void SyncTime()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_SyncTime.Begin();
#endif
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo("Syncing Time To Clients");
            }

            using (var buffer = PooledNetworkBuffer.Get())
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteSinglePacked(Time.realtimeSinceStartup);
                InternalMessageSender.Send(NetworkConstants.TIME_SYNC, NetworkChannel.SyncChannel, buffer);
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_SyncTime.End();
#endif
        }

        private readonly List<NetworkObject> m_ObservedObjects = new List<NetworkObject>();

        internal void HandleApproval(ulong clientId, bool createPlayerObject, ulong? playerPrefabHash, bool approved, Vector3? position, Quaternion? rotation)
        {
            if (approved)
            {
                // Inform new client it got approved
                if (PendingClients.ContainsKey(clientId)) PendingClients.Remove(clientId);

                var client = new NetworkClient { ClientId = clientId, };
                ConnectedClients.Add(clientId, client);
                ConnectedClientsList.Add(client);

                PerformanceDataManager.Increment(ProfilerConstants.NumberOfConnections);
                ProfilerStatManager.Connections.Record();

                // This packet is unreliable, but if it gets through it should provide a much better sync than the potentially huge approval message.
                SyncTime();


                if (createPlayerObject)
                {
                    var networkObject = NetworkSpawnManager.CreateLocalNetworkObject(false, 0, playerPrefabHash ?? NetworkConfig.PlayerPrefabHash.Value, null, position, rotation);
                    NetworkSpawnManager.SpawnNetworkObjectLocally(networkObject, NetworkSpawnManager.GetNetworkObjectId(), false, true, clientId, null, false, 0, false, false);

                    ConnectedClients[clientId].PlayerObject = networkObject;
                }

                m_ObservedObjects.Clear();

                foreach (var sobj in NetworkSpawnManager.SpawnedObjectsList)
                {
                    if (clientId == ServerClientId || sobj.CheckObjectVisibility == null || sobj.CheckObjectVisibility(clientId))
                    {
                        m_ObservedObjects.Add(sobj);
                        sobj.m_Observers.Add(clientId);
                    }
                }

                using (var buffer = PooledNetworkBuffer.Get())
                using (var writer = PooledNetworkWriter.Get(buffer))
                {
                    writer.WriteUInt64Packed(clientId);

                    if (NetworkConfig.EnableSceneManagement)
                    {
                        writer.WriteUInt32Packed(NetworkSceneManager.CurrentSceneIndex);
                        writer.WriteByteArray(NetworkSceneManager.CurrentSceneSwitchProgressGuid.ToByteArray());
                    }

                    writer.WriteSinglePacked(Time.realtimeSinceStartup);
                    writer.WriteUInt32Packed((uint)m_ObservedObjects.Count);

                    for (int i = 0; i < m_ObservedObjects.Count; i++)
                    {
                        var observedObject = m_ObservedObjects[i];
                        writer.WriteBool(observedObject.IsPlayerObject);
                        writer.WriteUInt64Packed(observedObject.NetworkObjectId);
                        writer.WriteUInt64Packed(observedObject.OwnerClientId);

                        NetworkObject parent = null;

                        if (!observedObject.AlwaysReplicateAsRoot && observedObject.transform.parent != null)
                        {
                            parent = observedObject.transform.parent.GetComponent<NetworkObject>();
                        }

                        if (parent == null)
                        {
                            writer.WriteBool(false);
                        }
                        else
                        {
                            writer.WriteBool(true);
                            writer.WriteUInt64Packed(parent.NetworkObjectId);
                        }

                        if (!NetworkConfig.EnableSceneManagement || NetworkConfig.UsePrefabSync)
                        {
                            writer.WriteUInt64Packed(observedObject.PrefabHash);
                        }
                        else
                        {
                            // Is this a scene object that we will soft map
                            writer.WriteBool(observedObject.IsSceneObject ?? true);

                            if (observedObject.IsSceneObject == null || observedObject.IsSceneObject.Value)
                            {
                                writer.WriteUInt64Packed(observedObject.NetworkInstanceId);
                            }
                            else
                            {
                                writer.WriteUInt64Packed(observedObject.PrefabHash);
                            }
                        }

                        if (observedObject.IncludeTransformWhenSpawning == null || observedObject.IncludeTransformWhenSpawning(clientId))
                        {
                            writer.WriteBool(true);
                            writer.WriteSinglePacked(observedObject.transform.position.x);
                            writer.WriteSinglePacked(observedObject.transform.position.y);
                            writer.WriteSinglePacked(observedObject.transform.position.z);

                            writer.WriteSinglePacked(observedObject.transform.rotation.eulerAngles.x);
                            writer.WriteSinglePacked(observedObject.transform.rotation.eulerAngles.y);
                            writer.WriteSinglePacked(observedObject.transform.rotation.eulerAngles.z);
                        }
                        else
                        {
                            writer.WriteBool(false);
                        }

                        if (NetworkConfig.EnableNetworkVariable)
                        {
                            observedObject.WriteNetworkVariableData(buffer, clientId);
                        }
                    }

                    InternalMessageSender.Send(clientId, NetworkConstants.CONNECTION_APPROVED, NetworkChannel.Internal, buffer);

                    OnClientConnectedCallback?.Invoke(clientId);
                }

                if (!createPlayerObject || (playerPrefabHash == null && NetworkConfig.PlayerPrefabHash == null)) return;

                //Inform old clients of the new player

                foreach (KeyValuePair<ulong, NetworkClient> clientPair in ConnectedClients)
                {
                    if (clientPair.Key == clientId ||
                        ConnectedClients[clientId].PlayerObject == null ||
                        !ConnectedClients[clientId].PlayerObject.m_Observers.Contains(clientPair.Key))
                        continue; //The new client.

                    using (var buffer = PooledNetworkBuffer.Get())
                    using (var writer = PooledNetworkWriter.Get(buffer))
                    {
                        writer.WriteBool(true);
                        writer.WriteUInt64Packed(ConnectedClients[clientId].PlayerObject.NetworkObjectId);
                        writer.WriteUInt64Packed(clientId);

                        //Does not have a parent
                        writer.WriteBool(false);

                        if (!NetworkConfig.EnableSceneManagement || NetworkConfig.UsePrefabSync)
                        {
                            writer.WriteUInt64Packed(playerPrefabHash ?? NetworkConfig.PlayerPrefabHash.Value);
                        }
                        else
                        {
                            // Not a softmap aka scene object
                            writer.WriteBool(false);
                            writer.WriteUInt64Packed(playerPrefabHash ?? NetworkConfig.PlayerPrefabHash.Value);
                        }

                        if (ConnectedClients[clientId].PlayerObject.IncludeTransformWhenSpawning == null || ConnectedClients[clientId].PlayerObject.IncludeTransformWhenSpawning(clientId))
                        {
                            writer.WriteBool(true);
                            writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.position.x);
                            writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.position.y);
                            writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.position.z);

                            writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.rotation.eulerAngles.x);
                            writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.rotation.eulerAngles.y);
                            writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.rotation.eulerAngles.z);
                        }
                        else
                        {
                            writer.WriteBool(false);
                        }

                        writer.WriteBool(false); //No payload data

                        if (NetworkConfig.EnableNetworkVariable)
                        {
                            ConnectedClients[clientId].PlayerObject.WriteNetworkVariableData(buffer, clientPair.Key);
                        }

                        InternalMessageSender.Send(clientPair.Key, NetworkConstants.ADD_OBJECT, NetworkChannel.Internal, buffer);
                    }
                }
            }
            else
            {
                if (PendingClients.ContainsKey(clientId))
                {
                    PendingClients.Remove(clientId);
                }

                NetworkConfig.NetworkTransport.DisconnectRemoteClient(clientId);
            }
        }

        private void ProfilerBeginTick()
        {
            PerformanceDataManager.BeginNewTick();
            if (NetworkConfig.NetworkTransport is ITransportProfilerData profileTransport)
            {
                profileTransport.BeginNewTick();
            }
        }

        private void NotifyProfilerListeners()
        {
            var data = PerformanceDataManager.GetData();
            var eventHandler = OnPerformanceDataEvent;
            if (eventHandler != null && data != null)
            {
                if (NetworkConfig.NetworkTransport is ITransportProfilerData profileTransport)
                {
                    var transportProfilerData = profileTransport.GetTransportProfilerData();
                    PerformanceDataManager.AddTransportData(transportProfilerData);
                }

                eventHandler.Invoke(data);
            }
            else if (data == null)
            {
                NetworkLog.LogWarning("No data available. Did you forget to call PerformanceDataManager.BeginNewTick() first?");
            }
        }
    }
}
