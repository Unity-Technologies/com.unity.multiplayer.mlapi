using System;
using UnityEngine;
using MLAPI.NetworkedVar;

namespace MLAPI
{
    /// <summary>
    /// A prototype component for syncing transforms
    /// </summary>
    [AddComponentMenu("MLAPI/ManualNetworkedVarTest")]
    public class ManualNetworkedVarTest : NetworkedBehaviour
    {
        private NetworkedVar<int> m_TestVar;
        private int m_MinDelta = 0;
        private int m_MaxDelta = 0;
        private int m_LastRemoteTick = 0;
        private bool m_Valid = false;
        private string m_Problems = "";
        private int m_Count = 0;

        // todo: address issue with initial values
        private const int m_WaitIterations = 5;
        private const int m_EndIterations = 1000;

        void Start()
        {
            Debug.Log("Start");
            m_TestVar.Value = 0;
            m_TestVar.OnValueChanged = ValueChanged;
            m_TestVar.Settings.WritePermission = NetworkedVarPermission.Everyone;
        }

        void Awake()
        {
            Debug.Log("Awake");
        }

        private void FixedUpdate()
        {
            if (IsOwner)
            {
                m_TestVar.Value = m_TestVar.Value + 1;
            }
        }

        private void ValueChanged(int before, int after)
        {
            if (!IsLocalPlayer && !IsServer)
            {
                // compute the delta in tick between client and server,
                // as seen from the client, when it receives a value not from itself
                if (m_TestVar.LocalTick != TickSystem.k_NoTick)
                {
                    int delta = m_TestVar.LocalTick - m_TestVar.RemoteTick;
                    m_Count++;

                    if (m_Count > m_WaitIterations)
                    {
                        if (!m_Valid)
                        {
                            m_Valid = true;
                            m_MinDelta = delta;
                            m_MaxDelta = delta;
                            m_LastRemoteTick = m_TestVar.RemoteTick;
                        }
                        else
                        {
                            m_MinDelta = Math.Min(delta, m_MinDelta);
                            m_MaxDelta = Math.Max(delta, m_MaxDelta);

                            // tick should not go backward until wrap around (which should be a long time)
                            if (m_TestVar.RemoteTick == m_LastRemoteTick)
                            {
                                m_Problems += "Same remote tick receive twice\n";
                            }
                            else if (m_TestVar.RemoteTick < m_LastRemoteTick)
                            {
                                m_Problems += "Ticks went backward\n";
                            }
                        }
                    }
                }
            }

            if (m_Count == m_EndIterations)
            {
                if (m_Problems == "" && Math.Abs(m_MaxDelta - m_MinDelta) < 2)
                {
                    Debug.Log("**** TEST PASSED ****");
                }
                else
                {
                    Debug.Log("**** TEST FAILED ****");
                    Debug.Log("Delta range: " + m_MinDelta + ", " + m_MaxDelta);

                    if (m_Problems != "")
                    {
                        Debug.Log(m_Problems);
                    }
                }

                enabled = false;
            }
        }
    }
}
