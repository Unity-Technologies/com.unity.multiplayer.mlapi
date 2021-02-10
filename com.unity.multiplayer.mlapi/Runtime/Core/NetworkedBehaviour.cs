using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using UnityEngine;
using System.Reflection;
using System.Linq;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Logging;
using MLAPI.Messaging;
using MLAPI.NetworkedVar;
using MLAPI.Profiling;
using MLAPI.Reflection;
using MLAPI.Security;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Spawning;
using MLAPI.Transports;
using BitStream = MLAPI.Serialization.BitStream;
using Unity.Profiling;

namespace MLAPI
{
    /// <summary>
    /// The base class to override to write networked code. Inherits MonoBehaviour
    /// </summary>
    public abstract class NetworkedBehaviour : MonoBehaviour
    {
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `protected`
        internal enum __NExec
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public enum __NExec
#endif
        {
            None = 0,
            Server = 1,
            Client = 2
        }

#pragma warning disable 414
        [NonSerialized]
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `protected`
        internal __NExec __nexec = __NExec.None;
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public __NExec __nexec = __NExec.None;
#endif
#pragma warning restore 414

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `protected`
        internal BitSerializer __beginSendServerRpc(ServerRpcParams serverRpcParams, RpcDelivery rpcDelivery)
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public BitSerializer __beginSendServerRpc(ServerRpcParams serverRpcParams, RpcDelivery rpcDelivery)
#endif
        {
            PooledBitWriter writer;

            var rpcQueueContainer = NetworkingManager.Singleton.rpcQueueContainer;
            var isUsingBatching = rpcQueueContainer.IsUsingBatching();
            var transportChannel = rpcDelivery == RpcDelivery.Reliable ? Channel.ReliableRPC : Channel.UnreliableRPC;

            if (IsHost)
            {
                writer = rpcQueueContainer.BeginAddQueueItemToFrame(RpcQueueContainer.QueueItemType.ServerRpc, Time.realtimeSinceStartup, transportChannel, 0,
                    NetworkingManager.Singleton.ServerClientId, null, QueueHistoryFrame.QueueFrameType.Inbound, serverRpcParams.Send.UpdateStage);

                if (!isUsingBatching)
                {
                    writer.WriteBit(false); // Encrypted
                    writer.WriteBit(false); // Authenticated
                    writer.WriteBits(MLAPIConstants.MLAPI_SERVER_RPC, 6); // MessageType
                }
            }
            else
            {
                writer = rpcQueueContainer.BeginAddQueueItemToFrame(RpcQueueContainer.QueueItemType.ServerRpc, Time.realtimeSinceStartup, transportChannel, 0,
                    NetworkingManager.Singleton.ServerClientId, null, QueueHistoryFrame.QueueFrameType.Outbound, NetworkUpdateStage.PostLateUpdate);
                if (!isUsingBatching)
                {
                    writer.WriteBit(false); // Encrypted
                    writer.WriteBit(false); // Authenticated
                    writer.WriteBits(MLAPIConstants.MLAPI_SERVER_RPC, 6); // MessageType
                }
            }

            writer.WriteUInt64Packed(NetworkId); // NetworkObjectId
            writer.WriteUInt16Packed(GetBehaviourId()); // NetworkBehaviourId
            writer.WriteByte((byte)serverRpcParams.Send.UpdateStage); // NetworkUpdateStage

            return writer.Serializer;
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `protected`
        internal void __endSendServerRpc(BitSerializer serializer, ServerRpcParams serverRpcParams, RpcDelivery rpcDelivery)
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public void __endSendServerRpc(BitSerializer serializer, ServerRpcParams serverRpcParams, RpcDelivery rpcDelivery)
#endif
        {
            if (serializer == null) return;

            var rpcQueueContainer = NetworkingManager.Singleton.rpcQueueContainer;
            if (IsHost)
            {
                rpcQueueContainer.EndAddQueueItemToFrame(serializer.Writer, QueueHistoryFrame.QueueFrameType.Inbound, serverRpcParams.Send.UpdateStage);
            }
            else
            {
                rpcQueueContainer.EndAddQueueItemToFrame(serializer.Writer, QueueHistoryFrame.QueueFrameType.Outbound, NetworkUpdateStage.PostLateUpdate);
            }
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `protected`
        internal BitSerializer __beginSendClientRpc(ClientRpcParams clientRpcParams, RpcDelivery rpcDelivery)
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public BitSerializer __beginSendClientRpc(ClientRpcParams clientRpcParams, RpcDelivery rpcDelivery)
#endif
        {
            PooledBitWriter writer;

            // This will start a new queue item entry and will then return the writer to the current frame's stream
            var rpcQueueContainer = NetworkingManager.Singleton.rpcQueueContainer;
            var isUsingBatching = rpcQueueContainer.IsUsingBatching();
            var transportChannel = rpcDelivery == RpcDelivery.Reliable ? Channel.ReliableRPC : Channel.UnreliableRPC;

            ulong[] ClientIds = clientRpcParams.Send.TargetClientIds ?? NetworkingManager.Singleton.ConnectedClientsList.Select(c => c.ClientId).ToArray();
            if (clientRpcParams.Send.TargetClientIds != null && clientRpcParams.Send.TargetClientIds.Length == 0)
            {
                ClientIds = NetworkingManager.Singleton.ConnectedClientsList.Select(c => c.ClientId).ToArray();
            }

            //NOTES ON BELOW CHANGES:
            //The following checks for IsHost and whether the host client id is part of the clients to recieve the RPC
            //Is part of a patch-fix to handle looping back RPCs into the next frame's inbound queue.
            //!!! This code is temporary and will change (soon) when bitserializer can be configured for mutliple BitWriters!!!
            var ContainsServerClientId = ClientIds.Contains(NetworkingManager.Singleton.ServerClientId);
            if (IsHost && ContainsServerClientId)
            {
                //Always write to the next frame's inbound queue
                writer = rpcQueueContainer.BeginAddQueueItemToFrame(RpcQueueContainer.QueueItemType.ClientRpc, Time.realtimeSinceStartup, transportChannel, 0,
                    NetworkingManager.Singleton.ServerClientId, null, QueueHistoryFrame.QueueFrameType.Inbound, clientRpcParams.Send.UpdateStage);

                //Handle sending to the other clients, if so the above notes explain why this code is here (a temporary patch-fix)
                if (ClientIds.Length > 1)
                {
                    //Set the loopback frame
                    rpcQueueContainer.SetLoopBackFrameItem(clientRpcParams.Send.UpdateStage);

                    //Switch to the outbound queue
                    writer = rpcQueueContainer.BeginAddQueueItemToFrame(RpcQueueContainer.QueueItemType.ClientRpc, Time.realtimeSinceStartup, Channel.ReliableRPC, 0, NetworkId,
                        ClientIds, QueueHistoryFrame.QueueFrameType.Outbound, NetworkUpdateStage.PostLateUpdate);

                    if (!isUsingBatching)
                    {
                        writer.WriteBit(false); // Encrypted
                        writer.WriteBit(false); // Authenticated
                        writer.WriteBits(MLAPIConstants.MLAPI_CLIENT_RPC, 6); // MessageType
                    }
                }
                else
                {
                    if (!isUsingBatching)
                    {
                        writer.WriteBit(false); // Encrypted
                        writer.WriteBit(false); // Authenticated
                        writer.WriteBits(MLAPIConstants.MLAPI_CLIENT_RPC, 6); // MessageType
                    }
                }
            }
            else
            {
                writer = rpcQueueContainer.BeginAddQueueItemToFrame(RpcQueueContainer.QueueItemType.ClientRpc, Time.realtimeSinceStartup, transportChannel, 0, NetworkId,
                    ClientIds, QueueHistoryFrame.QueueFrameType.Outbound, NetworkUpdateStage.PostLateUpdate);

                if (!isUsingBatching)
                {
                    writer.WriteBit(false); // Encrypted
                    writer.WriteBit(false); // Authenticated
                    writer.WriteBits(MLAPIConstants.MLAPI_CLIENT_RPC, 6); // MessageType
                }
            }

            writer.WriteUInt64Packed(NetworkId); // NetworkObjectId
            writer.WriteUInt16Packed(GetBehaviourId()); // NetworkBehaviourId
            writer.WriteByte((byte)clientRpcParams.Send.UpdateStage); // NetworkUpdateStage

            return writer.Serializer;
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `protected`
        internal void __endSendClientRpc(BitSerializer serializer, ClientRpcParams clientRpcParams, RpcDelivery rpcDelivery)
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public void __endSendClientRpc(BitSerializer serializer, ClientRpcParams clientRpcParams, RpcDelivery rpcDelivery)
#endif
        {
            if (serializer == null) return;

            var rpcQueueContainer = NetworkingManager.Singleton.rpcQueueContainer;

            if (IsHost)
            {
                ulong[] ClientIds = clientRpcParams.Send.TargetClientIds ?? NetworkingManager.Singleton.ConnectedClientsList.Select(c => c.ClientId).ToArray();
                if (clientRpcParams.Send.TargetClientIds != null && clientRpcParams.Send.TargetClientIds.Length == 0)
                {
                    ClientIds = NetworkingManager.Singleton.ConnectedClientsList.Select(c => c.ClientId).ToArray();
                }

                var ContainsServerClientId = ClientIds.Contains(NetworkingManager.Singleton.ServerClientId);
                if (ContainsServerClientId && ClientIds.Length == 1)
                {
                    rpcQueueContainer.EndAddQueueItemToFrame(serializer.Writer, QueueHistoryFrame.QueueFrameType.Inbound, clientRpcParams.Send.UpdateStage);
                    return;
                }
            }

            rpcQueueContainer.EndAddQueueItemToFrame(serializer.Writer, QueueHistoryFrame.QueueFrameType.Outbound, NetworkUpdateStage.PostLateUpdate);
        }

        /// <summary>
        /// Gets the NetworkingManager that owns this NetworkedBehaviour instance
        /// </summary>
        public NetworkingManager NetworkManager => NetworkedObject.NetworkManager;
        /// <summary>
        /// Gets if the object is the the personal clients player object
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsLocalPlayer instead", false)]
        public bool isLocalPlayer => IsLocalPlayer;
        /// <summary>
        /// Gets if the object is the the personal clients player object
        /// </summary>
        public bool IsLocalPlayer => NetworkedObject.IsLocalPlayer;
        /// <summary>
        /// Gets if the object is owned by the local player or if the object is the local player object
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsOwner instead", false)]
        public bool isOwner => IsOwner;
        /// <summary>
        /// Gets if the object is owned by the local player or if the object is the local player object
        /// </summary>
        public bool IsOwner => NetworkedObject.IsOwner;
        /// <summary>
        /// Gets if we are executing as server
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsServer instead", false)]
        protected bool isServer => IsServer;
        /// <summary>
        /// Gets if we are executing as server
        /// </summary>
        protected static bool IsServer => IsRunning && NetworkingManager.Singleton.IsServer;
        /// <summary>
        /// Gets if we are executing as client
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsClient instead")]
        protected bool isClient => IsClient;
        /// <summary>
        /// Gets if we are executing as client
        /// </summary>
        protected bool IsClient => IsRunning && NetworkingManager.Singleton.IsClient;
        /// <summary>
        /// Gets if we are executing as Host, I.E Server and Client
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsHost instead", false)]
        protected bool isHost => IsHost;
        /// <summary>
        /// Gets if we are executing as Host, I.E Server and Client
        /// </summary>
        protected bool IsHost => IsRunning && NetworkingManager.Singleton.IsHost;
        private static bool IsRunning => NetworkingManager.Singleton != null && NetworkingManager.Singleton.IsListening;
        /// <summary>
        /// Gets Whether or not the object has a owner
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsOwnedByServer instead", false)]
		public bool isOwnedByServer => IsOwnedByServer;
        /// <summary>
        /// Gets Whether or not the object has a owner
        /// </summary>
        public bool IsOwnedByServer => NetworkedObject.IsOwnedByServer;
        /// <summary>
        /// Gets the NetworkedObject that owns this NetworkedBehaviour instance
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use NetworkedObject instead", false)]
        public NetworkedObject networkedObject => NetworkedObject;
        /// <summary>
        /// Gets the NetworkedObject that owns this NetworkedBehaviour instance
        /// </summary>
        public NetworkedObject NetworkedObject
        {
            get
            {
                if (_networkedObject == null)
                {
                    _networkedObject = GetComponentInParent<NetworkedObject>();
                }

                if (_networkedObject == null)
                {
                    throw new NullReferenceException("Could not get NetworkedObject for the NetworkedBehaviour. Are you missing a NetworkedObject component?");
                }

                return _networkedObject;
            }
        }
        /// <summary>
        /// Gets whether or not this NetworkedBehaviour instance has a NetworkedObject owner.
        /// </summary>
        public bool HasNetworkedObject
        {
            get
            {
                if (_networkedObject == null)
                {
                    _networkedObject = GetComponentInParent<NetworkedObject>();
                }

                return _networkedObject != null;
            }
        }

        private NetworkedObject _networkedObject = null;
        /// <summary>
        /// Gets the NetworkId of the NetworkedObject that owns the NetworkedBehaviour instance
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use NetworkId instead", false)]
        public ulong networkId => NetworkId;
        /// <summary>
        /// Gets the NetworkId of the NetworkedObject that owns the NetworkedBehaviour instance
        /// </summary>
        public ulong NetworkId => NetworkedObject.NetworkId;
        /// <summary>
        /// Gets the clientId that owns the NetworkedObject
        /// </summary>
        public ulong OwnerClientId => NetworkedObject.OwnerClientId;

        internal bool networkedStartInvoked = false;
        internal bool internalNetworkedStartInvoked = false;
        /// <summary>
        /// Stores the network tick at the NetworkedBehaviourUpdate time
        /// This allows sending NetworkedVars not more often than once per network tick, regardless of the update rate
        /// </summary>
        static ushort currentTick = 0;
        /// <summary>
        /// Gets called when message handlers are ready to be registered and the networking is setup
        /// </summary>
        public virtual void NetworkStart()
        {

        }

        /// <summary>
        /// Gets called when message handlers are ready to be registered and the networking is setup. Provides a Payload if it was provided
        /// </summary>
        /// <param name="stream">The stream containing the spawn payload</param>
        public virtual void NetworkStart(Stream stream)
        {
            NetworkStart();
        }

        internal void InternalNetworkStart()
        {
            InitializeVars();
        }

        /// <summary>
        /// Gets called when the local client gains ownership of this object
        /// </summary>
        public virtual void OnGainedOwnership()
        {

        }
        /// <summary>
        /// Gets called when we loose ownership of this object
        /// </summary>
        public virtual void OnLostOwnership()
        {

        }

        /// <summary>
        /// Gets behaviourId for this NetworkedBehaviour on this NetworkedObject
        /// </summary>
        /// <returns>The behaviourId for the current NetworkedBehaviour</returns>
        public ushort GetBehaviourId()
        {
            return NetworkedObject.GetOrderIndex(this);
        }

        /// <summary>
        /// Returns a the NetworkedBehaviour with a given behaviourId for the current networkedObject
        /// </summary>
        /// <param name="id">The behaviourId to return</param>
        /// <returns>Returns NetworkedBehaviour with given behaviourId</returns>
        protected NetworkedBehaviour GetBehaviour(ushort id)
        {
            return NetworkedObject.GetBehaviourAtOrderIndex(id);
        }

        #region NetworkedVar

        private bool varInit = false;

        private readonly List<HashSet<int>> channelMappedNetworkedVarIndexes = new List<HashSet<int>>();
        private readonly List<Channel> channelsForNetworkedVarGroups = new List<Channel>();
        internal readonly List<INetworkedVar> networkedVarFields = new List<INetworkedVar>();

        private static HashSet<MLAPI.NetworkedObject> touched = new HashSet<MLAPI.NetworkedObject>();
        private static readonly Dictionary<Type, FieldInfo[]> fieldTypes = new Dictionary<Type, FieldInfo[]>();

        private static FieldInfo[] GetFieldInfoForType(Type type)
        {
            if (!fieldTypes.ContainsKey(type))
                fieldTypes.Add(type, GetFieldInfoForTypeRecursive(type));

            return fieldTypes[type];
        }

        private static FieldInfo[] GetFieldInfoForTypeRecursive(Type type, List<FieldInfo> list = null)
        {
            if (list == null)
            {
                list = new List<FieldInfo>();
                list.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            }
            else
            {
                list.AddRange(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance));
            }

            if (type.BaseType != null && type.BaseType != typeof(NetworkedBehaviour))
            {
                return GetFieldInfoForTypeRecursive(type.BaseType, list);
            }
            else
            {
                return list.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            }
        }

        internal void InitializeVars()
        {
            if (varInit)
                return;
            varInit = true;

            FieldInfo[] sortedFields = GetFieldInfoForType(GetType());

            for (int i = 0; i < sortedFields.Length; i++)
            {
                Type fieldType = sortedFields[i].FieldType;

                if (fieldType.HasInterface(typeof(INetworkedVar)))
                {
                    INetworkedVar instance = (INetworkedVar)sortedFields[i].GetValue(this);

                    if (instance == null)
                    {
                        instance = (INetworkedVar)Activator.CreateInstance(fieldType, true);
                        sortedFields[i].SetValue(this, instance);
                    }

                    instance.SetNetworkedBehaviour(this);
                    networkedVarFields.Add(instance);
                }
            }

            {
                // Create index map for channels
                Dictionary<Channel, int> firstLevelIndex = new Dictionary<Channel, int>();
                int secondLevelCounter = 0;

                for (int i = 0; i < networkedVarFields.Count; i++)
                {
                    Channel channel = networkedVarFields[i].GetChannel();

                    if (!firstLevelIndex.ContainsKey(channel))
                    {
                        firstLevelIndex.Add(channel, secondLevelCounter);
                        channelsForNetworkedVarGroups.Add(channel);
                        secondLevelCounter++;
                    }

                    if (firstLevelIndex[channel] >= channelMappedNetworkedVarIndexes.Count)
                    {
                        channelMappedNetworkedVarIndexes.Add(new HashSet<int>());
                    }

                    channelMappedNetworkedVarIndexes[firstLevelIndex[channel]].Add(i);
                }
            }
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public static ProfilerMarker s_NetworkedBehaviourUpdate = new ProfilerMarker("MLAPI.NetworkedObject.NetworkedBehaviourUpdate");
#endif

        internal static void NetworkedBehaviourUpdate()
        {
            // Don't NetworkedBehaviourUpdate more than once per network tick
            ushort tick = GetTick();
            if (tick == currentTick)
            {
                return;
            }
            currentTick = tick;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_NetworkedBehaviourUpdate.Begin();
#endif
            try
            {
                if (IsServer)
                {
                    touched.Clear();
                    for (int i = 0; i < NetworkingManager.Singleton.ConnectedClientsList.Count; i++)
                    {
                        var client = NetworkingManager.Singleton.ConnectedClientsList[i];
                        var spawnedObjs = SpawnManager.SpawnedObjectsList;
                        touched.UnionWith(spawnedObjs);
                        foreach (var sobj in spawnedObjs)
                        {
                            // Sync just the variables for just the objects this client sees
                            for (int k = 0; k < sobj.childNetworkedBehaviours.Count; k++)
                            {
                                sobj.childNetworkedBehaviours[k].VarUpdate(client.ClientId);
                            }
                        }
                    }

                    // Now, reset all the no-longer-dirty variables
                    foreach (var sobj in touched)
                    {
                        for (int k = 0; k < sobj.childNetworkedBehaviours.Count; k++)
                        {
                            sobj.childNetworkedBehaviours[k].PostNetworkVariableWrite();
                        }
                    }
                }
                else
                {

                    // when client updates the sever, it tells it about all its objects
                    foreach (var sobj in SpawnManager.SpawnedObjectsList)
                    {
                        for (int k = 0; k < sobj.childNetworkedBehaviours.Count; k++)
                        {
                           sobj.childNetworkedBehaviours[k].VarUpdate(NetworkingManager.Singleton.ServerClientId);
                        }
                    }

                    // Now, reset all the no-longer-dirty variables
                    foreach (var sobj in SpawnManager.SpawnedObjectsList)
                    {
                        for (int k = 0; k < sobj.childNetworkedBehaviours.Count; k++)
                        {
                            sobj.childNetworkedBehaviours[k].PostNetworkVariableWrite();
                        }
                    }
                }
            }
            finally
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                s_NetworkedBehaviourUpdate.End();
#endif
            }
        }


        internal void PreNetworkVariableWrite()
        {
            // reset our "which variables got written" data
            networkedVarIndexesToReset.Clear();
            networkedVarIndexesToResetSet.Clear();
        }

        internal void PostNetworkVariableWrite()
        {
            // mark any variables we wrote as no longer dirty
            for (int i = 0; i < networkedVarIndexesToReset.Count; i++)
            {
                networkedVarFields[networkedVarIndexesToReset[i]].ResetDirty();
            }
        }

        internal void VarUpdate(ulong clientId)
        {
            if (!varInit)
                InitializeVars();

            PreNetworkVariableWrite();
            NetworkedVarUpdate(clientId);
        }

        private readonly List<int> networkedVarIndexesToReset = new List<int>();
        private readonly HashSet<int> networkedVarIndexesToResetSet = new HashSet<int>();

        // todo: This is temporary, to be replaced by the tick system
        public static ushort GetTick()
        {
            return (ushort)(Time.time / 0.1);
        }

        private void NetworkedVarUpdate(ulong clientId)
        {
            if (!CouldHaveDirtyNetworkedVars())
                return;

            for (int j = 0; j < channelMappedNetworkedVarIndexes.Count; j++)
            {
                using (PooledBitStream stream = PooledBitStream.Get())
                {
                    using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                    {
                        writer.WriteUInt64Packed(NetworkId);
                        writer.WriteUInt16Packed(NetworkedObject.GetOrderIndex(this));

                        // Write the current tick frame
                        // todo: this is currently done per channel, per tick the snapshot system might improve on this
                        writer.WriteUInt16Packed(currentTick);

                        bool writtenAny = false;
                        for (int k = 0; k < networkedVarFields.Count; k++)
                        {
                            if (!channelMappedNetworkedVarIndexes[j].Contains(k))
                            {
                                // This var does not belong to the currently iterating channel group.
                                if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                                {
                                    writer.WriteUInt16Packed(0);
                                }
                                else
                                {
                                    writer.WriteBool(false);
                                }
                                continue;
                            }

                            bool isDirty = networkedVarFields[k].IsDirty(); // cache this here. You never know what operations users will do in the dirty methods

                            //   if I'm dirty AND a client, write (server always has all permissions)
                            //   if I'm dirty AND the server AND the client can read me, send.
                            bool shouldWrite = isDirty && (!IsServer || networkedVarFields[k].CanClientRead(clientId));

                            if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                            {
                                if (!shouldWrite)
                                {
                                    writer.WriteUInt16Packed(0);
                                }
                            }
                            else
                            {
                                writer.WriteBool(shouldWrite);
                            }

                            if (shouldWrite)
                            {
                                writtenAny = true;

                                if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                                {
                                    using (PooledBitStream varStream = PooledBitStream.Get())
                                    {
                                        networkedVarFields[k].WriteDelta(varStream);
                                        varStream.PadStream();

                                        writer.WriteUInt16Packed((ushort)varStream.Length);
                                        stream.CopyFrom(varStream);
                                    }
                                }
                                else
                                {
                                    networkedVarFields[k].WriteDelta(stream);
                                }

                                if (!networkedVarIndexesToResetSet.Contains(k))
                                {
                                    networkedVarIndexesToResetSet.Add(k);
                                    networkedVarIndexesToReset.Add(k);
                                }
                            }
                        }

                        if (writtenAny)
                        {
                            InternalMessageSender.Send(clientId,
                                MLAPIConstants.MLAPI_NETWORKED_VAR_DELTA,
                                channelsForNetworkedVarGroups[j], stream, SecuritySendFlags.None);
                        }
                    }
                }
            }
        }
        private bool CouldHaveDirtyNetworkedVars()
        {
            // TODO: There should be a better way by reading one dirty variable vs. 'n'
            for (int i = 0; i < networkedVarFields.Count; i++)
            {
                if (networkedVarFields[i].IsDirty())
                    return true;
            }

            return false;
        }

        internal static void HandleNetworkedVarDeltas(List<INetworkedVar> networkedVarList, Stream stream, ulong clientId, NetworkedBehaviour logInstance)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                // read the remote network tick at which this variable was written.
                ushort remoteTick = reader.ReadUInt16Packed();

                for (int i = 0; i < networkedVarList.Count; i++)
                {
                    ushort varSize = 0;

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        varSize = reader.ReadUInt16Packed();

                        if (varSize == 0)
                            continue;
                    }
                    else
                    {
                        if (!reader.ReadBool())
                            continue;
                    }

                    if (IsServer && !networkedVarList[i].CanClientWrite(clientId))
                    {
                        if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                            {
                                NetworkLog.LogWarning("Client wrote to NetworkedVar without permission. " + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                                NetworkLog.LogError("[" + networkedVarList[i].GetType().Name + "]");
                            }


                            stream.Position += varSize;
                            continue;
                        }
                        else
                        {
                            //This client wrote somewhere they are not allowed. This is critical
                            //We can't just skip this field. Because we don't actually know how to dummy read
                            //That is, we don't know how many bytes to skip. Because the interface doesn't have a
                            //Read that gives us the value. Only a Read that applies the value straight away
                            //A dummy read COULD be added to the interface for this situation, but it's just being too nice.
                            //This is after all a developer fault. A critical error should be fine.
                            // - TwoTen

                            if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                            {
                                NetworkLog.LogError("Client wrote to NetworkedVar without permission. No more variables can be read. This is critical. " + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                                NetworkLog.LogError("[" + networkedVarList[i].GetType().Name + "]");
                            }
                            return;
                        }
                    }

                    // read the local network tick at which this variable was written.
                    // if this var was updated from our machine, this local tick will be locally valid
                    ushort localTick = reader.ReadUInt16Packed();

                    long readStartPos = stream.Position;

                    networkedVarList[i].ReadDelta(stream, IsServer, localTick, remoteTick);
                    ProfilerStatManager.networkVarsRcvd.Record();

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        (stream as BitStream).SkipPadBits();

                        if (stream.Position > (readStartPos + varSize))
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Var delta read too far. " + (stream.Position - (readStartPos + varSize)) + " bytes." + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                            stream.Position = readStartPos + varSize;
                        }
                        else if (stream.Position < (readStartPos + varSize))
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Var delta read too little. " + ((readStartPos + varSize) - stream.Position) + " bytes." + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                            stream.Position = readStartPos + varSize;
                        }
                    }
                }
            }
        }

        internal static void HandleNetworkedVarUpdate(List<INetworkedVar> networkedVarList, Stream stream, ulong clientId, NetworkedBehaviour logInstance)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                // read the remote network tick at which this variable was written.
                ushort remoteTick = reader.ReadUInt16Packed();

                for (int i = 0; i < networkedVarList.Count; i++)
                {
                    ushort varSize = 0;

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        varSize = reader.ReadUInt16Packed();

                        if (varSize == 0)
                            continue;
                    }
                    else
                    {
                        if (!reader.ReadBool())
                            continue;
                    }

                    if (IsServer && !networkedVarList[i].CanClientWrite(clientId))
                    {
                        if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Client wrote to NetworkedVar without permission. " + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                            stream.Position += varSize;
                            continue;
                        }
                        else
                        {
                            //This client wrote somewhere they are not allowed. This is critical
                            //We can't just skip this field. Because we don't actually know how to dummy read
                            //That is, we don't know how many bytes to skip. Because the interface doesn't have a
                            //Read that gives us the value. Only a Read that applies the value straight away
                            //A dummy read COULD be added to the interface for this situation, but it's just being too nice.
                            //This is after all a developer fault. A critical error should be fine.
                            // - TwoTen
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("Client wrote to NetworkedVar without permission. No more variables can be read. This is critical. " + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                            return;
                        }
                    }

                    // read the local network tick at which this variable was written.
                    // if this var was updated from our machine, this local tick will be locally valid
                    ushort localTick = reader.ReadUInt16Packed();
                    long readStartPos = stream.Position;

                    networkedVarList[i].ReadField(stream, localTick, remoteTick);
                    ProfilerStatManager.networkVarsRcvd.Record();

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        if (stream is BitStream bitStream)
                        {
                            bitStream.SkipPadBits();
                        }

                        if (stream.Position > (readStartPos + varSize))
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Var update read too far. " + (stream.Position - (readStartPos + varSize)) + " bytes." + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                            stream.Position = readStartPos + varSize;
                        }
                        else if (stream.Position < (readStartPos + varSize))
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Var update read too little. " + ((readStartPos + varSize) - stream.Position) + " bytes." + (logInstance != null ? ("NetworkId: " + logInstance.NetworkId + " BehaviourIndex: " + logInstance.NetworkedObject.GetOrderIndex(logInstance) + " VariableIndex: " + i) : string.Empty));
                            stream.Position = readStartPos + varSize;
                        }
                    }
                }
            }
        }


        internal static void WriteNetworkedVarData(List<INetworkedVar> networkedVarList, Stream stream, ulong clientId)
        {
            if (networkedVarList.Count == 0)
                return;

            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                for (int j = 0; j < networkedVarList.Count; j++)
                {
                    bool canClientRead = networkedVarList[j].CanClientRead(clientId);

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        if (!canClientRead)
                        {
                            writer.WriteUInt16Packed(0);
                        }
                    }
                    else
                    {
                        writer.WriteBool(canClientRead);
                    }

                    if (canClientRead)
                    {
                        if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                        {
                            using (PooledBitStream varStream = PooledBitStream.Get())
                            {
                                networkedVarList[j].WriteField(varStream);
                                varStream.PadStream();

                                writer.WriteUInt16Packed((ushort)varStream.Length);
                                varStream.CopyTo(stream);
                            }
                        }
                        else
                        {
                            networkedVarList[j].WriteField(stream);
                        }
                    }
                }
            }
        }

        internal static void SetNetworkedVarData(List<INetworkedVar> networkedVarList, Stream stream)
        {
            if (networkedVarList.Count == 0)
                return;

            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                for (int j = 0; j < networkedVarList.Count; j++)
                {
                    ushort varSize = 0;

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        varSize = reader.ReadUInt16Packed();

                        if (varSize == 0)
                            continue;
                    }
                    else
                    {
                        if (!reader.ReadBool())
                            continue;
                    }

                    long readStartPos = stream.Position;

                    networkedVarList[j].ReadField(stream, 0, 0);

                    if (NetworkingManager.Singleton.NetworkConfig.EnsureNetworkedVarLengthSafety)
                    {
                        if (stream is BitStream bitStream)
                        {
                            bitStream.SkipPadBits();
                        }

                        if (stream.Position > (readStartPos + varSize))
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Var data read too far. " + (stream.Position - (readStartPos + varSize)) + " bytes.");
                            stream.Position = readStartPos + varSize;
                        }
                        else if (stream.Position < (readStartPos + varSize))
                        {
                            if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Var data read too little. " + ((readStartPos + varSize) - stream.Position) + " bytes.");
                            stream.Position = readStartPos + varSize;
                        }
                    }
                }
            }
        }
        #endregion

        /// <summary>
        /// Gets the local instance of a object with a given NetworkId
        /// </summary>
        /// <param name="networkId"></param>
        /// <returns></returns>
        protected NetworkedObject GetNetworkedObject(ulong networkId)
        {
            if(SpawnManager.SpawnedObjects.ContainsKey(networkId))
                return SpawnManager.SpawnedObjects[networkId];
            return null;
        }
    }
}
