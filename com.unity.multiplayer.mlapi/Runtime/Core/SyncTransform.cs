using MLAPI.NetworkedVar;
using UnityEngine;

namespace MLAPI
{
    /// <summary>
    /// A component for syncing variables
    /// Initial goal: allow an FPS-style snapshot
    /// with variables updating at specific place in the frame
    /// </summary>
    [AddComponentMenu("MLAPI/SyncTransform")]
    // todo: check inheriting from NetworkedBehaviour. Currently needed for IsLocalPlayer, to synchronize position
    public class SyncTransform : NetworkedBehaviour
    {
        NetworkedVar<Vector3> m_varPos = new NetworkedVar<Vector3>();
        NetworkedVar<Quaternion> m_varRot = new NetworkedVar<Quaternion>();
        const float k_updateRate = 0.1f;
        const float k_epsilon = 0.001f;

        // data structures for interpolation
        Vector3[] m_PosStore = new Vector3[2];
        Quaternion[] m_RotStore = new Quaternion[2];
        float[] m_PosTimes = new float[2];
        float[] m_RotTimes = new float[2];
        float m_lastSent = 0.0f;

        SyncTransform()
        {
            m_PosTimes[0] = -1.0f;
            m_PosTimes[1] = -1.0f;
            m_RotTimes[0] = -1.0f;
            m_RotTimes[1] = -1.0f;

            m_varPos.OnValueChanged = SyncPosChanged;
            m_varRot.OnValueChanged = SyncRotChanged;
        }

        void SyncPosChanged(Vector3 before, Vector3 after)
        {
            if (!IsLocalPlayer)
            {
                m_PosTimes[0] = m_PosTimes[1];
                m_PosTimes[1] = Time.fixedTime;
                m_PosStore[0] = m_PosStore[1];
                m_PosStore[1] = after;

                gameObject.transform.position = after;
            }
        }

        void SyncRotChanged(Quaternion before, Quaternion after)
        {
            // todo: this is problematic. Why couldn't this filtering be done server-side?
            if (!IsLocalPlayer)
            {
                m_RotTimes[0] = m_RotTimes[1];
                m_RotTimes[1] = Time.fixedTime;
                m_RotStore[0] = m_RotStore[1];
                m_RotStore[1] = after;

                gameObject.transform.rotation = after;
            }
        }

        void Start()
        {
            m_varPos.Settings.WritePermission = NetworkedVarPermission.Everyone;
            m_varRot.Settings.WritePermission = NetworkedVarPermission.Everyone;
        }

        void FixedUpdate()
        {
            float now = Time.fixedTime;
            if (m_lastSent == 0.0f)
            {
                m_lastSent = now;
            }

            // if this.gameObject is local let's send its position
            if (IsLocalPlayer)
            {
                while ((now - m_lastSent) > k_updateRate)
                {
                    m_lastSent += k_updateRate;

                    m_varPos.Value = gameObject.transform.position;
                    m_varRot.Value = gameObject.transform.rotation;
                }
            }
            else
            {
                // todo: do we want to perform local interpolation on Update() instead?

                // let's interpolate the last received transform
                if (m_PosTimes[0] >= 0.0 && m_PosTimes[1] >= 0.0)
                {
                    if (m_PosTimes[1] - m_PosTimes[0] > k_epsilon)
                    {
                        gameObject.transform.position = Vector3.LerpUnclamped(
                            m_PosStore[0],
                            m_PosStore[1],
                            (now - m_PosTimes[0]) / (m_PosTimes[1] - m_PosTimes[0]));
                    }
                }
                if (m_RotTimes[0] >= 0.0 && m_RotTimes[1] >= 0.0)
                {
                    if (m_RotTimes[1] - m_RotTimes[0] > k_epsilon)
                    {
                        gameObject.transform.rotation = Quaternion.SlerpUnclamped(
                            m_RotStore[0],
                            m_RotStore[1],
                            (now - m_RotTimes[0]) / (m_RotTimes[1] - m_RotTimes[0]));
                    }
                }
            }
        }
    }
}
