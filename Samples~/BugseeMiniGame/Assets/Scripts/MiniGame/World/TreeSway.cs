using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Shared wind used by grass (and anything else that wants to lean with it).</summary>
    public static class FieldWind
    {
        public static Vector3 Direction = new Vector3(1f, 0f, 0.32f).normalized;
        public static float Strength = 1f;
    }

    /// <summary>Gentle canopy sway (attach to foliage root, not the trunk).</summary>
    public sealed class TreeSway : MonoBehaviour
    {
        float _ampDeg = 2.2f;
        float _speed = 1f;
        float _phase;
        Quaternion _baseRot;

        public void Configure(float speed, float amplitudeDeg = 2.4f)
        {
            _speed = Mathf.Max(0.15f, speed);
            _ampDeg = Mathf.Clamp(amplitudeDeg, 0.6f, 6f);
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
            float yaw = Mathf.Sin(t) * _ampDeg;
            float pitch = Mathf.Sin(t * 1.31f + 0.7f) * (_ampDeg * 0.45f);
            float roll = Mathf.Sin(t * 0.77f + 1.1f) * (_ampDeg * 0.2f);
            transform.localRotation = _baseRot * Quaternion.Euler(pitch, yaw, roll);
        }
    }

    /// <summary>
    /// Batched grass-clump sway. One Update drives every clump — cheaper than per-blade scripts.
    /// </summary>
    public sealed class GrassField : MonoBehaviour
    {
        Transform[] _clumps;
        Quaternion[] _baseRot;
        float[] _phase;
        float[] _amp;
        float _speed = 1.7f;

        public void Bind(Transform[] clumps, float[] phases, float[] amps, float speed = 1.7f)
        {
            _clumps = clumps;
            _phase = phases;
            _amp = amps;
            _speed = speed;
            _baseRot = new Quaternion[clumps.Length];
            for (int i = 0; i < clumps.Length; i++)
                _baseRot[i] = clumps[i] != null ? clumps[i].localRotation : Quaternion.identity;
        }

        void Update()
        {
            if (_clumps == null) return;

            var wind = FieldWind.Direction;
            float strength = FieldWind.Strength;
            float t = Time.time * _speed;

            for (int i = 0; i < _clumps.Length; i++)
            {
                var tr = _clumps[i];
                if (tr == null) continue;

                float s = Mathf.Sin(t + _phase[i]) * _amp[i] * strength;
                // Second harmonic for less mechanical motion.
                s += Mathf.Sin(t * 1.7f + _phase[i] * 1.3f) * (_amp[i] * 0.35f) * strength;
                float pitch = wind.z * s;
                float roll = -wind.x * s;
                tr.localRotation = _baseRot[i] * Quaternion.Euler(pitch, 0f, roll);
            }
        }
    }
}
