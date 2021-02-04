#if !NET35
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;

namespace MLAPI.NetworkedVar.Collections
{
    /// <summary>
    /// Event based networkedVar container for syncing Sets
    /// </summary>
    /// <typeparam name="T">The type for the set</typeparam>
    public class NetworkedSet<T> : ISet<T>, INetworkedVar
    {
        private readonly ISet<T> set = new HashSet<T>();
        private readonly List<NetworkedSetEvent<T>> dirtyEvents = new List<NetworkedSetEvent<T>>();
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
        /// Delegate type for set changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnSetChangedDelegate(NetworkedSetEvent<T> changeEvent);

        /// <summary>
        /// The callback to be invoked when the set gets changed
        /// </summary>
        public event OnSetChangedDelegate OnSetChanged;

        /// <summary>
        /// Creates a NetworkedSet with the default value and settings
        /// </summary>
        public NetworkedSet()
        {

        }

        /// <summary>
        /// Creates a NetworkedSet with the default value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedList</param>
        public NetworkedSet(NetworkedVarSettings settings)
        {
            this.Settings = settings;
        }

        /// <summary>
        /// Creates a NetworkedSet with a custom value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedSet</param>
        /// <param name="value">The initial value to use for the NetworkedSet</param>
        public NetworkedSet(NetworkedVarSettings settings, ISet<T> value)
        {
            this.Settings = settings;
            this.set = value;
        }

        /// <summary>
        /// Creates a NetworkedSet with a custom value and the default settings
        /// </summary>
        /// <param name="value">The initial value to use for the NetworkedList</param>
        public NetworkedSet(ISet<T> value)
        {
            this.set = value;
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
                    writer.WriteBits((byte) dirtyEvents[i].eventType, 2);

                    switch (dirtyEvents[i].eventType)
                    {
                        case NetworkedSetEvent<T>.EventType.Add:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                        }
                            break;
                        case NetworkedSetEvent<T>.EventType.Remove:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                        }
                            break;
                        case NetworkedSetEvent<T>.EventType.Clear:
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
                writer.WriteUInt16Packed((ushort) set.Count);

                foreach (T value in set)
                {
                    writer.WriteObjectPacked(value); //BOX
                }
            }
        }

        /// <inheritdoc />
        public void ReadField(Stream stream, ushort srcTick)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                set.Clear();
                ushort count = reader.ReadUInt16Packed();

                for (int i = 0; i < count; i++)
                {
                    set.Add((T) reader.ReadObjectPacked(typeof(T))); //BOX
                }
            }
        }

        /// <inheritdoc />
        public void ReadDelta(Stream stream, bool keepDirtyDelta, ushort srcTick)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ushort deltaCount = reader.ReadUInt16Packed();
                for (int i = 0; i < deltaCount; i++)
                {
                    NetworkedSetEvent<T>.EventType eventType = (NetworkedSetEvent<T>.EventType) reader.ReadBits(2);
                    switch (eventType)
                    {
                        case NetworkedSetEvent<T>.EventType.Add:
                        {
                            T value = (T) reader.ReadObjectPacked(typeof(T)); //BOX
                            set.Add(value);

                            if (OnSetChanged != null)
                            {
                                OnSetChanged(new NetworkedSetEvent<T>
                                {
                                    eventType = eventType,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedSetEvent<T>()
                                {
                                    eventType = eventType,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedSetEvent<T>.EventType.Remove:
                        {
                            T value = (T) reader.ReadObjectPacked(typeof(T)); //BOX
                            set.Remove(value);

                            if (OnSetChanged != null)
                            {
                                OnSetChanged(new NetworkedSetEvent<T>
                                {
                                    eventType = eventType,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedSetEvent<T>()
                                {
                                    eventType = eventType,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedSetEvent<T>.EventType.Clear:
                        {
                            //Read nothing
                            set.Clear();

                            if (OnSetChanged != null)
                            {
                                OnSetChanged(new NetworkedSetEvent<T>
                                {
                                    eventType = eventType,
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedSetEvent<T>()
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
            return set.GetEnumerator();
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator()
        {
            return set.GetEnumerator();
        }

        /// <inheritdoc />
        void ICollection<T>.Add(T item)
        {
            if (NetworkingManager.Singleton.IsServer)
                set.Add(item);

            NetworkedSetEvent<T> setEvent = new NetworkedSetEvent<T>()
            {
                eventType = NetworkedSetEvent<T>.EventType.Add,
                value = item
            };
            dirtyEvents.Add(setEvent);

            if (NetworkingManager.Singleton.IsServer && OnSetChanged != null)
                OnSetChanged(setEvent);
        }

        /// <inheritdoc />
        public void ExceptWith(IEnumerable<T> other)
        {
            foreach (T value in other)
            {
                if (set.Contains(value))
                {
                    Remove(value);
                }
            }
        }

        /// <inheritdoc />
        public void IntersectWith(IEnumerable<T> other)
        {
            HashSet<T> otherSet = new HashSet<T>(other);

            foreach (T value in set)
            {
                if (!otherSet.Contains(value))
                {
                    Remove(value);
                }
            }
        }

        /// <inheritdoc />
        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            return set.IsProperSubsetOf(other);
        }

        /// <inheritdoc />
        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            return set.IsProperSupersetOf(other);
        }

        /// <inheritdoc />
        public bool IsSubsetOf(IEnumerable<T> other)
        {
            return set.IsSubsetOf(other);
        }

        /// <inheritdoc />
        public bool IsSupersetOf(IEnumerable<T> other)
        {
            return set.IsSupersetOf(other);
        }

        /// <inheritdoc />
        public bool Overlaps(IEnumerable<T> other)
        {
            return set.Overlaps(other);
        }

        /// <inheritdoc />
        public bool SetEquals(IEnumerable<T> other)
        {
            return set.SetEquals(other);
        }

        /// <inheritdoc />
        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            foreach (T value in other)
            {
                if (set.Contains(value))
                {
                    Remove(value);
                }
                else
                {
                    if (NetworkingManager.Singleton.IsServer)
                        set.Add(value);

                    NetworkedSetEvent<T> setEvent = new NetworkedSetEvent<T>()
                    {
                        eventType = NetworkedSetEvent<T>.EventType.Add,
                        value = value
                    };
                    dirtyEvents.Add(setEvent);

                    if (NetworkingManager.Singleton.IsServer && OnSetChanged != null)
                        OnSetChanged(setEvent);
                }
            }
        }

        /// <inheritdoc />
        public void UnionWith(IEnumerable<T> other)
        {
            foreach (T value in other)
            {
                if (!set.Contains(value))
                {
                    if (NetworkingManager.Singleton.IsServer)
                        set.Add(value);

                    NetworkedSetEvent<T> setEvent = new NetworkedSetEvent<T>()
                    {
                        eventType = NetworkedSetEvent<T>.EventType.Add,
                        value = value
                    };
                    dirtyEvents.Add(setEvent);

                    if (NetworkingManager.Singleton.IsServer && OnSetChanged != null)
                        OnSetChanged(setEvent);
                }
            }
        }

        /// <inheritdoc />
        bool ISet<T>.Add(T item)
        {
            if (NetworkingManager.Singleton.IsServer)
                set.Add(item);

            NetworkedSetEvent<T> setEvent = new NetworkedSetEvent<T>()
            {
                eventType = NetworkedSetEvent<T>.EventType.Add,
                value = item
            };
            dirtyEvents.Add(setEvent);

            if (NetworkingManager.Singleton.IsServer && OnSetChanged != null)
                OnSetChanged(setEvent);

            return true;
        }

        /// <inheritdoc />
        public void Clear()
        {
            if (NetworkingManager.Singleton.IsServer)
                set.Clear();

            NetworkedSetEvent<T> setEvent = new NetworkedSetEvent<T>()
            {
                eventType = NetworkedSetEvent<T>.EventType.Clear
            };
            dirtyEvents.Add(setEvent);

            if (NetworkingManager.Singleton.IsServer && OnSetChanged != null)
                OnSetChanged(setEvent);
        }

        /// <inheritdoc />
        public bool Contains(T item)
        {
            return set.Contains(item);
        }

        /// <inheritdoc />
        public void CopyTo(T[] array, int arrayIndex)
        {
            set.CopyTo(array, arrayIndex);
        }

        /// <inheritdoc />
        public bool Remove(T item)
        {
            if (NetworkingManager.Singleton.IsServer)
                set.Remove(item);

            NetworkedSetEvent<T> setEvent = new NetworkedSetEvent<T>()
            {
                eventType = NetworkedSetEvent<T>.EventType.Remove,
                value = item
            };
            dirtyEvents.Add(setEvent);

            if (NetworkingManager.Singleton.IsServer && OnSetChanged != null)
                OnSetChanged(setEvent);

            return true;
        }

        /// <inheritdoc />
        public int Count => set.Count;

        /// <inheritdoc />
        public bool IsReadOnly => set.IsReadOnly;

        public ushort SrcTick { get; }
    }

    /// <summary>
    /// Struct containing event information about changes to a NetworkedSet.
    /// </summary>
    /// <typeparam name="T">The type for the set that the event is about</typeparam>
    public struct NetworkedSetEvent<T>
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
            /// Remove
            /// </summary>
            Remove,

            /// <summary>
            /// Clear
            /// </summary>
            Clear
        }

        /// <summary>
        /// Enum representing the operation made to the set.
        /// </summary>
        public EventType eventType;

        /// <summary>
        /// The value changed, added or removed if available.
        /// </summary>
        public T value;
    }
}
#endif
