using System.Collections;
using System.Collections.Generic;
using System.IO;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;

namespace MLAPI.NetworkedVar.Collections
{
    /// <summary>
    /// Event based networkedVar container for syncing Dictionaries
    /// </summary>
    /// <typeparam name="TKey">The type for the dictionary keys</typeparam>
    /// <typeparam name="TValue">The type for the dictionary values</typeparam>
    public class NetworkedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, INetworkedVar
    {
        /// <summary>
        /// Gets the last time the variable was synced
        /// </summary>
        public float LastSyncedTime { get; internal set; }

        /// <summary>
        /// The settings for this container
        /// </summary>
        public readonly NetworkedVarSettings Settings = new NetworkedVarSettings();

        private readonly IDictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();
        private NetworkedBehaviour networkedBehaviour;
        private readonly List<NetworkedDictionaryEvent<TKey, TValue>> dirtyEvents = new List<NetworkedDictionaryEvent<TKey, TValue>>();

        /// <summary>
        /// Delegate type for dictionary changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnDictionaryChangedDelegate(NetworkedDictionaryEvent<TKey, TValue> changeEvent);

        /// <summary>
        /// The callback to be invoked when the dictionary gets changed
        /// </summary>
        public event OnDictionaryChangedDelegate OnDictionaryChanged;


        /// <summary>
        /// Creates a NetworkedDictionary with the default value and settings
        /// </summary>
        public NetworkedDictionary()
        {

        }

        /// <summary>
        /// Creates a NetworkedDictionary with the default value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedDictionary</param>
        public NetworkedDictionary(NetworkedVarSettings settings)
        {
            this.Settings = settings;
        }

        /// <summary>
        /// Creates a NetworkedDictionary with a custom value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedDictionary</param>
        /// <param name="value">The initial value to use for the NetworkedDictionary</param>
        public NetworkedDictionary(NetworkedVarSettings settings, IDictionary<TKey, TValue> value)
        {
            this.Settings = settings;
            this.dictionary = value;
        }

        /// <summary>
        /// Creates a NetworkedDictionary with a custom value and the default settings
        /// </summary>
        /// <param name="value">The initial value to use for the NetworkedDictionary</param>
        public NetworkedDictionary(IDictionary<TKey, TValue> value)
        {
            this.dictionary = value;
        }

        /// <inheritdoc />
        public void ResetDirty()
        {
            dirtyEvents.Clear();
            LastSyncedTime = NetworkingManager.Singleton.NetworkTime;
        }

        /// <inheritdoc />
        public Channel GetChannel()
        {
            return Settings.SendChannel;
        }

        /// <inheritdoc />
        public void ReadDelta(Stream stream, bool keepDirtyDelta, ushort localTick, ushort remoteTick)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ushort deltaCount = reader.ReadUInt16Packed();
                for (int i = 0; i < deltaCount; i++)
                {
                    NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType eventType = (NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType) reader.ReadBits(3);
                    switch (eventType)
                    {
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Add:
                        {
                            TKey key = (TKey) reader.ReadObjectPacked(typeof(TKey));
                            TValue value = (TValue) reader.ReadObjectPacked(typeof(TValue));
                            dictionary.Add(key, value);

                            if (OnDictionaryChanged != null)
                            {
                                OnDictionaryChanged(new NetworkedDictionaryEvent<TKey, TValue>
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedDictionaryEvent<TKey, TValue>()
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Remove:
                        {
                            TKey key = (TKey) reader.ReadObjectPacked(typeof(TKey));
                            TValue value;
                            dictionary.TryGetValue(key, out value);
                            dictionary.Remove(key);

                            if (OnDictionaryChanged != null)
                            {
                                OnDictionaryChanged(new NetworkedDictionaryEvent<TKey, TValue>
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedDictionaryEvent<TKey, TValue>()
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.RemovePair:
                        {
                            TKey key = (TKey) reader.ReadObjectPacked(typeof(TKey));
                            TValue value = (TValue) reader.ReadObjectPacked(typeof(TValue));
                            dictionary.Remove(new KeyValuePair<TKey, TValue>(key, value));

                            if (OnDictionaryChanged != null)
                            {
                                OnDictionaryChanged(new NetworkedDictionaryEvent<TKey, TValue>
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedDictionaryEvent<TKey, TValue>()
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Clear:
                        {
                            //read nothing
                            dictionary.Clear();

                            if (OnDictionaryChanged != null)
                            {
                                OnDictionaryChanged(new NetworkedDictionaryEvent<TKey, TValue>
                                {
                                    eventType = eventType
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedDictionaryEvent<TKey, TValue>
                                {
                                    eventType = eventType
                                });
                            }
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Value:
                        {
                            TKey key = (TKey) reader.ReadObjectPacked(typeof(TKey));
                            TValue value = (TValue) reader.ReadObjectPacked(typeof(TValue));

                            dictionary[key] = value;

                            if (OnDictionaryChanged != null)
                            {
                                OnDictionaryChanged(new NetworkedDictionaryEvent<TKey, TValue>
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                dirtyEvents.Add(new NetworkedDictionaryEvent<TKey, TValue>()
                                {
                                    eventType = eventType,
                                    key = key,
                                    value = value
                                });
                            }
                        }
                            break;
                    }
                }
            }
        }

        /// <inheritdoc />
        public void ReadField(Stream stream, ushort localTick, ushort remoteTick)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                dictionary.Clear();
                ushort entryCount = reader.ReadUInt16Packed();
                for (int i = 0; i < entryCount; i++)
                {
                    TKey key = (TKey) reader.ReadObjectPacked(typeof(TKey));
                    TValue value = (TValue) reader.ReadObjectPacked(typeof(TValue));
                    dictionary.Add(key, value);
                }
            }
        }

        /// <inheritdoc />
        public void SetNetworkedBehaviour(NetworkedBehaviour behaviour)
        {
            networkedBehaviour = behaviour;
        }

        /// <inheritdoc />
        public bool TryGetValue(TKey key, out TValue value)
        {
            return dictionary.TryGetValue(key, out value);
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
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Add:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].key);
                            writer.WriteObjectPacked(dirtyEvents[i].value);
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Remove:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].key);
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.RemovePair:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].key);
                            writer.WriteObjectPacked(dirtyEvents[i].value);
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Clear:
                        {
                            //write nothing
                        }
                            break;
                        case NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Value:
                        {
                            writer.WriteObjectPacked(dirtyEvents[i].key);
                            writer.WriteObjectPacked(dirtyEvents[i].value);
                        }
                            break;
                        default:
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
                writer.WriteUInt16Packed((ushort) dictionary.Count);
                foreach (KeyValuePair<TKey, TValue> pair in dictionary)
                {
                    writer.WriteObjectPacked(pair.Key);
                    writer.WriteObjectPacked(pair.Value);
                }
            }
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
        public bool IsDirty()
        {
            if (dirtyEvents.Count == 0) return false;
            if (Settings.SendTickrate == 0) return true;
            if (Settings.SendTickrate < 0) return false;
            if (NetworkingManager.Singleton.NetworkTime - LastSyncedTime >= (1f / Settings.SendTickrate)) return true;
            return false;
        }


        /// <inheritdoc />
        public TValue this[TKey key]
        {
            get { return dictionary[key]; }
            set
            {
                if (NetworkingManager.Singleton.IsServer)
                    dictionary[key] = value;

                NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent = new NetworkedDictionaryEvent<TKey, TValue>()
                {
                    eventType = NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Value,
                    key = key,
                    value = value
                };

                HandleAddDictionaryEvent(dictionaryEvent);
            }
        }

        /// <inheritdoc />
        public ICollection<TKey> Keys => dictionary.Keys;

        /// <inheritdoc />
        public ICollection<TValue> Values => dictionary.Values;

        /// <inheritdoc />
        public int Count => dictionary.Count;

        /// <inheritdoc />
        public bool IsReadOnly => dictionary.IsReadOnly;

        /// <inheritdoc />
        public void Add(TKey key, TValue value)
        {
            if (NetworkingManager.Singleton.IsServer)
                dictionary.Add(key, value);

            NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent = new NetworkedDictionaryEvent<TKey, TValue>()
            {
                eventType = NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Add,
                key = key,
                value = value
            };

            HandleAddDictionaryEvent(dictionaryEvent);
        }

        /// <inheritdoc />
        public void Add(KeyValuePair<TKey, TValue> item)
        {
            if (NetworkingManager.Singleton.IsServer)
                dictionary.Add(item);

            NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent = new NetworkedDictionaryEvent<TKey, TValue>()
            {
                eventType = NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Add,
                key = item.Key,
                value = item.Value
            };

            HandleAddDictionaryEvent(dictionaryEvent);
        }

        /// <inheritdoc />
        public void Clear()
        {
            if (NetworkingManager.Singleton.IsServer)
                dictionary.Clear();

            NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent = new NetworkedDictionaryEvent<TKey, TValue>()
            {
                eventType = NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Clear
            };

            HandleAddDictionaryEvent(dictionaryEvent);
        }

        /// <inheritdoc />
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return dictionary.Contains(item);
        }

        /// <inheritdoc />
        public bool ContainsKey(TKey key)
        {
            return dictionary.ContainsKey(key);
        }

        /// <inheritdoc />
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            dictionary.CopyTo(array, arrayIndex);
        }

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }

        /// <inheritdoc />
        public bool Remove(TKey key)
        {
            if (NetworkingManager.Singleton.IsServer)
                dictionary.Remove(key);

            TValue value;
            dictionary.TryGetValue(key, out value);

            NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent = new NetworkedDictionaryEvent<TKey, TValue>()
            {
                eventType = NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.Remove,
                key = key,
                value = value
            };

            HandleAddDictionaryEvent(dictionaryEvent);

            return true;
        }


        /// <inheritdoc />
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            if (NetworkingManager.Singleton.IsServer)
                dictionary.Remove(item);

            NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent = new NetworkedDictionaryEvent<TKey, TValue>()
            {
                eventType = NetworkedDictionaryEvent<TKey, TValue>.NetworkedListEventType.RemovePair,
                key = item.Key,
                value = item.Value
            };

            HandleAddDictionaryEvent(dictionaryEvent);
            return true;
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }

        private void HandleAddDictionaryEvent(NetworkedDictionaryEvent<TKey, TValue> dictionaryEvent)
        {
            if (NetworkingManager.Singleton.IsServer)
            {
                if (NetworkingManager.Singleton.ConnectedClients.Count > 0)
                {
                    dirtyEvents.Add(dictionaryEvent);
                }

                OnDictionaryChanged?.Invoke(dictionaryEvent);
            }
            else
            {
                dirtyEvents.Add(dictionaryEvent);
            }
        }
    }

    /// <summary>
    /// Struct containing event information about changes to a NetworkedDictionary.
    /// </summary>
    /// <typeparam name="TKey">The type for the dictionary key that the event is about</typeparam>
    /// <typeparam name="TValue">The type for the dictionary value that the event is about</typeparam>
    public struct NetworkedDictionaryEvent<TKey, TValue>
    {
        /// <summary>
        /// Enum representing the different operations available for triggering an event.
        /// </summary>
        public enum NetworkedListEventType
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
            /// Remove pair
            /// </summary>
            RemovePair,

            /// <summary>
            /// Clear
            /// </summary>
            Clear,

            /// <summary>
            /// Value changed
            /// </summary>
            Value
        }

        /// <summary>
        /// Enum representing the operation made to the dictionary.
        /// </summary>
        public NetworkedListEventType eventType;

        /// <summary>
        /// the key changed, added or removed if available.
        /// </summary>
        public TKey key;

        /// <summary>
        /// The value changed, added or removed if available.
        /// </summary>
        public TValue value;
    }
}
