using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System;
using MLAPI.Logging;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;

namespace MLAPI.NetworkedVar
{
    /// <summary>
    /// A variable that can be synchronized over the network.
    /// </summary>
    [Serializable]
    public class NetworkedVar<T> : INetworkedVar
    {
        /// <summary>
        /// Gets or sets Whether or not the variable needs to be delta synced
        /// </summary>
        public bool isDirty { get; set; }
        /// <summary>
        /// The settings for this var
        /// </summary>
        public readonly NetworkedVarSettings Settings = new NetworkedVarSettings();
        /// <summary>
        /// The last time the variable was written to locally
        /// </summary>
        public ushort LocalTick { get; internal set; }
        /// <summary>
        /// The last time the variable was written to remotely. Uses the remote timescale
        /// </summary>
        public ushort RemoteTick { get; internal set; }
        /// <summary>
        /// Delegate type for value changed event
        /// </summary>
        /// <param name="previousValue">The value before the change</param>
        /// <param name="newValue">The new value</param>
        public delegate void OnValueChangedDelegate(T previousValue, T newValue);
        /// <summary>
        /// The callback to be invoked when the value gets changed
        /// </summary>
        public OnValueChangedDelegate OnValueChanged;
        private NetworkedBehaviour networkedBehaviour;

        /// <summary>
        /// Creates a NetworkedVar with the default value and settings
        /// </summary>
        public NetworkedVar()
        {

        }

        /// <summary>
        /// Creates a NetworkedVar with the default value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedVar</param>
        public NetworkedVar(NetworkedVarSettings settings)
        {
            this.Settings = settings;
        }

        /// <summary>
        /// Creates a NetworkedVar with a custom value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedVar</param>
        /// <param name="value">The initial value to use for the NetworkedVar</param>
        public NetworkedVar(NetworkedVarSettings settings, T value)
        {
            this.Settings = settings;
            this.InternalValue = value;
        }

        /// <summary>
        /// Creates a NetworkedVar with a custom value and the default settings
        /// </summary>
        /// <param name="value">The initial value to use for the NetworkedVar</param>
        public NetworkedVar(T value)
        {
            this.InternalValue = value;
        }

        [SerializeField]
        private T InternalValue = default(T);
        /// <summary>
        /// The value of the NetworkedVar container
        /// </summary>
        public T Value
        {
            get
            {
                return InternalValue;
            }
            set
            {
                if (!EqualityComparer<T>.Default.Equals(InternalValue, value))
                {
                    // Setter is assumed to be called locally, by game code.
                    // When used by the host, it is its responsibility to set the RemoteTick
                    RemoteTick = 0;

                    isDirty = true;
                    T previousValue = InternalValue;
                    InternalValue = value;
                    if (OnValueChanged != null)
                        OnValueChanged(previousValue, InternalValue);
                }
            }
        }

        /// <inheritdoc />
        public void ResetDirty()
        {
            isDirty = false;
        }

        /// <inheritdoc />
        public bool IsDirty()
        {
            return isDirty;
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

        /// <summary>
        /// Writes the variable to the writer
        /// </summary>
        /// <param name="stream">The stream to write the value to</param>
        public void WriteDelta(Stream stream)
        {
            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                // write the network tick at which this NetworkedVar was modified remotely
                // this will allow lag-compensation
                // todo: this is currently only done on delta updates. Consider whether it should be done in WriteField
                writer.WriteUInt16Packed(RemoteTick);
            }

            WriteField(stream);
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

        /// <summary>
        /// Reads value from the reader and applies it
        /// </summary>
        /// <param name="stream">The stream to read the value from</param>
        /// <param name="keepDirtyDelta">Whether or not the container should keep the dirty delta, or mark the delta as consumed</param>
        public void ReadDelta(Stream stream, bool keepDirtyDelta, ushort localTick, ushort remoteTick)
        {
            // todo: This allows the host-returned value to be set back to an old value
            // this will need to be adjusted to check if we're have a most recent value
            LocalTick = localTick;
            RemoteTick = remoteTick;

            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                T previousValue = InternalValue;
                InternalValue = (T)reader.ReadObjectPacked((typeof(T)));

                if (keepDirtyDelta) isDirty = true;

                if (OnValueChanged != null)
                    OnValueChanged(previousValue, InternalValue);
            }
        }

        /// <inheritdoc />
        public void SetNetworkedBehaviour(NetworkedBehaviour behaviour)
        {
            networkedBehaviour = behaviour;
        }

        /// <inheritdoc />
        public void ReadField(Stream stream, ushort localTick, ushort remoteTick)
        {
            ReadDelta(stream, false, localTick, remoteTick);
        }

        /// <inheritdoc />
        public void WriteField(Stream stream)
        {
            // Store the local tick at which this NetworkedVar was modified
            LocalTick = NetworkedBehaviour.currentTick;
            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                writer.WriteObjectPacked(InternalValue); //BOX
            }
        }

        /// <inheritdoc />
        public Channel GetChannel()
        {
            return Settings.SendChannel;
        }
    }

    /// <summary>
    /// A NetworkedVar that holds strings and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarString : NetworkedVar<string>
    {
        /// <inheritdoc />
        public NetworkedVarString() : base(string.Empty) { }
        /// <inheritdoc />
        public NetworkedVarString(NetworkedVarSettings settings) : base(settings, string.Empty) { }
        /// <inheritdoc />
        public NetworkedVarString(string value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarString(NetworkedVarSettings settings, string value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds bools and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarBool : NetworkedVar<bool>
    {
        /// <inheritdoc />
        public NetworkedVarBool() { }
        /// <inheritdoc />
        public NetworkedVarBool(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarBool(bool value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarBool(NetworkedVarSettings settings, bool value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds bytes and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarByte : NetworkedVar<byte>
    {
        /// <inheritdoc />
        public NetworkedVarByte() { }
        /// <inheritdoc />
        public NetworkedVarByte(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarByte(byte value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarByte(NetworkedVarSettings settings, byte value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds sbytes and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarSByte : NetworkedVar<sbyte>
    {
        /// <inheritdoc />
        public NetworkedVarSByte() { }
        /// <inheritdoc />
        public NetworkedVarSByte(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarSByte(sbyte value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarSByte(NetworkedVarSettings settings, sbyte value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds ushorts and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarUShort : NetworkedVar<ushort>
    {
        /// <inheritdoc />
        public NetworkedVarUShort() { }
        /// <inheritdoc />
        public NetworkedVarUShort(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarUShort(ushort value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarUShort(NetworkedVarSettings settings, ushort value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds shorts and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarShort : NetworkedVar<short>
    {
        /// <inheritdoc />
        public NetworkedVarShort() { }
        /// <inheritdoc />
        public NetworkedVarShort(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarShort(short value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarShort(NetworkedVarSettings settings, short value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds uints and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarUInt : NetworkedVar<uint>
    {
        /// <inheritdoc />
        public NetworkedVarUInt() { }
        /// <inheritdoc />
        public NetworkedVarUInt(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarUInt(uint value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarUInt(NetworkedVarSettings settings, uint value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds ints and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarInt : NetworkedVar<int>
    {
        /// <inheritdoc />
        public NetworkedVarInt() { }
        /// <inheritdoc />
        public NetworkedVarInt(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarInt(int value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarInt(NetworkedVarSettings settings, int value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds ulongs and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarULong : NetworkedVar<ulong>
    {
        /// <inheritdoc />
        public NetworkedVarULong() { }
        /// <inheritdoc />
        public NetworkedVarULong(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarULong(ulong value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarULong(NetworkedVarSettings settings, ulong value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds longs and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarLong : NetworkedVar<long>
    {
        /// <inheritdoc />
        public NetworkedVarLong() { }
        /// <inheritdoc />
        public NetworkedVarLong(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarLong(long value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarLong(NetworkedVarSettings settings, long value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds floats and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarFloat : NetworkedVar<float>
    {
        /// <inheritdoc />
        public NetworkedVarFloat() { }
        /// <inheritdoc />
        public NetworkedVarFloat(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarFloat(float value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarFloat(NetworkedVarSettings settings, float value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds doubles and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarDouble : NetworkedVar<double>
    {
        /// <inheritdoc />
        public NetworkedVarDouble() { }
        /// <inheritdoc />
        public NetworkedVarDouble(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarDouble(double value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarDouble(NetworkedVarSettings settings, double value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds vector2s and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarVector2 : NetworkedVar<Vector2>
    {
        /// <inheritdoc />
        public NetworkedVarVector2() { }
        /// <inheritdoc />
        public NetworkedVarVector2(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarVector2(Vector2 value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarVector2(NetworkedVarSettings settings, Vector2 value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds vector3s and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarVector3 : NetworkedVar<Vector3>
    {
        /// <inheritdoc />
        public NetworkedVarVector3() { }
        /// <inheritdoc />
        public NetworkedVarVector3(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarVector3(Vector3 value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarVector3(NetworkedVarSettings settings, Vector3 value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds vector4s and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarVector4 : NetworkedVar<Vector4>
    {
        /// <inheritdoc />
        public NetworkedVarVector4() { }
        /// <inheritdoc />
        public NetworkedVarVector4(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarVector4(Vector4 value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarVector4(NetworkedVarSettings settings, Vector4 value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds colors and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarColor : NetworkedVar<Color>
    {
        /// <inheritdoc />
        public NetworkedVarColor() { }
        /// <inheritdoc />
        public NetworkedVarColor(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarColor(Color value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarColor(NetworkedVarSettings settings, Color value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds color32s and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarColor32 : NetworkedVar<Color32>
    {
        /// <inheritdoc />
        public NetworkedVarColor32() { }
        /// <inheritdoc />
        public NetworkedVarColor32(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarColor32(Color32 value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarColor32(NetworkedVarSettings settings, Color32 value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds rays and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarRay : NetworkedVar<Ray>
    {
        /// <inheritdoc />
        public NetworkedVarRay() { }
        /// <inheritdoc />
        public NetworkedVarRay(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarRay(Ray value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarRay(NetworkedVarSettings settings, Ray value) : base(settings, value) { }
    }

    /// <summary>
    /// A NetworkedVar that holds quaternions and support serialization
    /// </summary>
    [Serializable]
    public class NetworkedVarQuaternion : NetworkedVar<Quaternion>
    {
        /// <inheritdoc />
        public NetworkedVarQuaternion() { }
        /// <inheritdoc />
        public NetworkedVarQuaternion(NetworkedVarSettings settings) : base(settings) { }
        /// <inheritdoc />
        public NetworkedVarQuaternion(Quaternion value) : base(value) { }
        /// <inheritdoc />
        public NetworkedVarQuaternion(NetworkedVarSettings settings, Quaternion value) : base(settings, value) { }
    }
}
