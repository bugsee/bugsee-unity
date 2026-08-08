using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Sine-based canopy/tree sway for stutter-visible ambient motion.</summary>
    public sealed class TreeSway : MonoBehaviour
    {
        float _amp = 1f;
        float _speed = 1f;
        float _phase;
        Quaternion _baseRot;

        public void Configure(float speed)
        {
            _speed = speed;
            _amp = 2.5f + (GetInstanceID() & 7);
            _phase = (GetInstanceID() % 360) * Mathf.Deg2Rad;
        }

        void Start()
        {
            _baseRot = transform.localRotation;
            if (_speed <= 0f)
                Configure(1f);
        }

        void Update()
        {
            float t = Time.time * _speed + _phase;
            float yaw = Mathf.Sin(t) * _amp;
            float pitch = Mathf.Sin(t * 1.37f + 0.6f) * (_amp * 0.35f);
            transform.localRotation = _baseRot * Quaternion.Euler(pitch, yaw, pitch * 0.25f);
        }
    }
}
