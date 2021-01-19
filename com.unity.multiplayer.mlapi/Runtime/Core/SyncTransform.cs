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
    public class SyncTransform : NetworkedBehaviour
    {
        NetworkedVar<Vector3> _varPos = new NetworkedVar<Vector3>();
        NetworkedVar<Quaternion> _varRot = new NetworkedVar<Quaternion>();
        float totalElapsed = 0.0f;
        const float k_updateRate = 0.1f;

        SyncTransform()
        {
            _varPos.OnValueChanged = SyncPosChanged;
            _varRot.OnValueChanged = SyncRotChanged;
        }

        void SyncRotChanged(Quaternion before, Quaternion after)
        {
            // todo: this is problematic. Why couldn't this filtering be done server-side?
            if (!IsLocalPlayer)
            {
                gameObject.transform.rotation = after;
            }
        }

        void SyncPosChanged(Vector3 before, Vector3 after)
        {
            if (!IsLocalPlayer)
            {
                gameObject.transform.position = after;
            }
        }

        void Start()
        {
            _varPos.Settings.WritePermission = NetworkedVarPermission.Everyone;
            _varRot.Settings.WritePermission = NetworkedVarPermission.Everyone;
        }

        void Update()
        {
            // if this.gameObject is local let's send its position
            if (IsLocalPlayer)
            {
                float timeSinceLat = Time.deltaTime;
                totalElapsed += timeSinceLat;

                while (totalElapsed > k_updateRate)
                {
                    totalElapsed -= k_updateRate;

                    _varPos.Value = gameObject.transform.position;
                    _varRot.Value = gameObject.transform.rotation;
                }
            }
        }
    }
}
