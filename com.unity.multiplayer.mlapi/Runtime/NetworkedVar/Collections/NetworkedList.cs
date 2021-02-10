using System.Collections;
using System.Collections.Generic;
using System.IO;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;

namespace MLAPI.NetworkedVar.Collections
{
    /// <summary>
    /// Event based networkedVar container for syncing Lists
    /// </summary>
    /// <typeparam name="T">The type for the list</typeparam>
    public class NetworkedList<T> : IList<T>, INetworkedVar
    {
        private readonly IList<T> list = new List<T>();
        private readonly List<NetworkedListEvent<T>> dirtyEvents = new List<NetworkedListEvent<T>>();
        private NetworkedBehaviour networkedBehaviour;

        /// <summary>
        /// Gets the last time the variable was synced
        /// </summary>
        public float LastSyncedTime { get; internal set; }

        /// <summary>
        /// The settings for this container
        /// </summary>
        public readonly NetworkedVarSettings Settings = new NetworkedVarSettings();

        /// <summary>
        /// Delegate type for list changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnListChangedDelegate(NetworkedListEvent<T> changeEvent);

        /// <summary>
        /// The callback to be invoked when the list gets changed
        /// </summary>
        public event OnListChangedDelegate OnListChanged;

        /// <summary>
        /// Creates a NetworkedList with the default value and settings
        /// </summary>
        public NetworkedList()
        {

        }

        /// <summary>
        /// Creates a NetworkedList with the default value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedList</param>
        public NetworkedList(NetworkedVarSettings settings)
        {
            this.Settings = settings;
        }

        /// <summary>
        /// Creates a NetworkedList with a custom value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedList</param>
        /// <param name="value">The initial value to use for the NetworkedList</param>
        public NetworkedList(NetworkedVarSettings settings, IList<T> value)
        {
            this.Settings = settings;
            this.list = value;
        }

        /// <summary>
        /// Creates a NetworkedList with a custom value and the default settings
        /// </summary>
        /// <param name="value">The initial value to use for the NetworkedList</param>
        public NetworkedList(IList<T> value)
        {
            this.list = value;
        }

        /// <inheritdoc />
        public void ResetDirty()
        {
            dirtyEvents.Clear();
            LastSyncedTime = NetworkingManager.Singleton.NetworkTime;
        }

        /// <inheritdoc />
        public bool IsDirty()
        {
            if (dirtyEvents.Count == 0) return false;
            if (Settings.SendTickrate == 0) return true;
            if (Settings.SendTickrate < 0) return false;
            if (NetworkingManager.Singleton.NetworkTime - LastSyncedTime >= (1f / Settings.SendTickrate)) return true;
            return false;
        }

        /// <inheritdoc />
        public Channel GetChannel()
        {
            return Settings.SendChannel;
        }

        /// <inheritdoc />
        public bool CanClientWrite(ulong clientId)
        {
            switch (Settings.WritePermission)
            {
                case NetworkedVarPermission.Everyone:
                    return true;
                case NetworkedVarPermission.ServerOnly:
                    return false;
                case NetworkedVarPermission.OwnerOnly:
                    return networkedBehaviour.OwnerClientId == clientId;
                case NetworkedVarPermission.Custom:
                {
                    if (Settings.WritePermissionCallback == null) return false;
                    return Settings.WritePermissionCallback(clientId);
                }
            }

            return true;
        }

        /// <inheritdoc />
        public bool CanClientRead(ulong clientId)
        {
            switch (Settings.ReadPermission)
            {
                case NetworkedVarPermission.Everyone:
                    return true;
                case NetworkedVarPermission.ServerOnly:
                    return false;
                case NetworkedVarPermission.OwnerOnly:
                    return networkedBehaviour.OwnerClientId == clientId;
                case NetworkedVarPermission.Custom:
                {
                    if (Settings.ReadPermissionCallback == null) return false;
                    return Settings.ReadPermissionCallback(clientId);
                }
            }

            return true;
        }

        /// <inheritdoc />
        public void WriteDelta(Stream stream)
        {
            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                writer.WriteUInt16Packed((ushort) dirtyEvents.Count);
                for (int i = 0; i < dirtyEvents.Count; i++)
                {
                    writer.WriteBits((byte) dirtyEvents[i].eventType, 3);
                    switch (dirtyEvents[i].eventType)
                    {
                        case NetworkedListEvent<T>.EventType.Add:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Insert:
                        {
                            writer.WriteInt32Packed(dirtyEvents[i].index);
                            writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Remove:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.RemoveAt:
                        {
                            writer.WriteInt32Packed(dirtyEvents[i].index);
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Value:
                        {
                            writer.WriteInt32Packed(dirtyEvents[i].index);
                            writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                        }

                            break;
                        case NetworkedListEvent<T>.EventType.Clear:
                        {
                            //Nothing has to be written
                        }
                            break;
                    }
                }
            }
        }

        /// <inheritdoc />
        public void WriteField(Stream stream)
        {
            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                writer.WriteUInt16Packed((ushort) list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    writer.WriteObjectPacked(list[i]); //BOX
                }
            }
        }

        /// <inheritdoc />
        public void ReadField(Stream stream, ushort localTick, ushort remoteTick)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                list.Clear();
                ushort count = reader.ReadUInt16Packed();
                for (int i = 0; i < count; i++)
                {
                    list.Add((T) reader.ReadObjectPacked(typeof(T))); //BOX
                }
            }
        }

        /// <inheritdoc />
        public void ReadDelta(Stream stream, bool keepDirtyDelta, ushort localTick, ushort remoteTick)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ushort deltaCount = reader.ReadUInt16Packed();
                for (int i = 0; i < deltaCount; i++)
                {
                    NetworkedListEvent<T>.EventType eventType = (NetworkedListEvent<T>.EventType) reader.ReadBits(3);
                    switch (eventType)
                    {
                        case NetworkedListEvent<T>.EventType.Add:
                        {
                            list.Add((T) reader.ReadObjectPacked(typeof(T))); //BOX

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkedListEvent<T>
                                {
                                    eventType = eventType,
                                    index = list.Count - 1,
                                    value = list[list.Count - 1]
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedListEvent<T>()
                                {
                                    eventType = eventType,
                                    index = list.Count - 1,
                                    value = list[list.Count - 1]
                                });
                            }
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Insert:
                        {
                            int index = reader.ReadInt32Packed();
                            list.Insert(index, (T) reader.ReadObjectPacked(typeof(T))); //BOX

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkedListEvent<T>
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = list[index]
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedListEvent<T>()
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = list[index]
                                });
                            }
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Remove:
                        {
                            T value = (T) reader.ReadObjectPacked(typeof(T)); //BOX
                            int index = list.IndexOf(value);
                            list.RemoveAt(index);

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkedListEvent<T>
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedListEvent<T>()
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.RemoveAt:
                        {
                            int index = reader.ReadInt32Packed();
                            T value = list[index];
                            list.RemoveAt(index);

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkedListEvent<T>
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedListEvent<T>()
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Value:
                        {
                            int index = reader.ReadInt32Packed();
                            T value = (T) reader.ReadObjectPacked(typeof(T)); //BOX
                            if (index < list.Count) list[index] = value;

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkedListEvent<T>
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedListEvent<T>()
                                {
                                    eventType = eventType,
                                    index = index,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedListEvent<T>.EventType.Clear:
                        {
                            //Read nothing
                            list.Clear();

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkedListEvent<T>
                                {
                                    eventType = eventType,
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedListEvent<T>()
                                {
                                    eventType = eventType
                                });
                            }
                        }
                            break;
                    }
                }
            }
        }

        /// <inheritdoc />
        public void SetNetworkedBehaviour(NetworkedBehaviour behaviour)
        {
            networkedBehaviour = behaviour;
        }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            return list.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable) list).GetEnumerator();
        }

        /// <inheritdoc />
        public void Add(T item)
        {
            if (NetworkingManager.Singleton.IsServer)
                list.Add(item);

            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T>()
            {
                eventType = NetworkedListEvent<T>.EventType.Add,
                value = item,
                index = list.Count - 1
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public void Clear()
        {
            if (NetworkingManager.Singleton.IsServer)
                list.Clear();

            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T>()
            {
                eventType = NetworkedListEvent<T>.EventType.Clear
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public bool Contains(T item)
        {
            return list.Contains(item);
        }

        /// <inheritdoc />
        public void CopyTo(T[] array, int arrayIndex)
        {
            list.CopyTo(array, arrayIndex);
        }

        /// <inheritdoc />
        public bool Remove(T item)
        {
            if (NetworkingManager.Singleton.IsServer)
                list.Remove(item);

            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T>()
            {
                eventType = NetworkedListEvent<T>.EventType.Remove,
                value = item
            };

            HandleAddListEvent(listEvent);
            return true;
        }

        /// <inheritdoc />
        public int Count => list.Count;

        /// <inheritdoc />
        public bool IsReadOnly => list.IsReadOnly;

        /// <inheritdoc />
        public int IndexOf(T item)
        {
            return list.IndexOf(item);
        }

        /// <inheritdoc />
        public void Insert(int index, T item)
        {
            if (NetworkingManager.Singleton.IsServer)
                list.Insert(index, item);

            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T>()
            {
                eventType = NetworkedListEvent<T>.EventType.Insert,
                index = index,
                value = item
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public void RemoveAt(int index)
        {
            if (NetworkingManager.Singleton.IsServer)
                list.RemoveAt(index);

            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T>()
            {
                eventType = NetworkedListEvent<T>.EventType.RemoveAt,
                index = index
            };

            HandleAddListEvent(listEvent);
        }


        /// <inheritdoc />
        public T this[int index]
        {
            get { return list[index]; }
            set
            {
                if (NetworkingManager.Singleton.IsServer)
                    list[index] = value;

                NetworkedListEvent<T> listEvent = new NetworkedListEvent<T>()
                {
                    eventType = NetworkedListEvent<T>.EventType.Value,
                    index = index,
                    value = value
                };

                HandleAddListEvent(listEvent);
            }
        }

        private void HandleAddListEvent(NetworkedListEvent<T> listEvent)
        {
            if (NetworkingManager.Singleton.IsServer)
            {
                if (NetworkingManager.Singleton.ConnectedClients.Count > 0)
                {
                    dirtyEvents.Add(listEvent);
                }

                OnListChanged?.Invoke(listEvent);
            }
            else
            {
                dirtyEvents.Add(listEvent);
            }
        }
    }

    /// <summary>
    /// Struct containing event information about changes to a NetworkedList.
    /// </summary>
    /// <typeparam name="T">The type for the list that the event is about</typeparam>
    public struct NetworkedListEvent<T>
    {
        /// <summary>
        /// Enum representing the different operations available for triggering an event.
        /// </summary>
        public enum EventType
        {
            /// <summary>
            /// Add
            /// </summary>
            Add,

            /// <summary>
            /// Insert
            /// </summary>
            Insert,

            /// <summary>
            /// Remove
            /// </summary>
            Remove,

            /// <summary>
            /// Remove at
            /// </summary>
            RemoveAt,

            /// <summary>
            /// Value changed
            /// </summary>
            Value,

            /// <summary>
            /// Clear
            /// </summary>
            Clear
        }

        /// <summary>
        /// Enum representing the operation made to the list.
        /// </summary>
        public EventType eventType;

        /// <summary>
        /// The value changed, added or removed if available.
        /// </summary>
        public T value;

        /// <summary>
        /// the index changed, added or removed if available
        /// </summary>
        public int index;
    }
}
