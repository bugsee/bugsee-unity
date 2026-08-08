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
    /// Keeps grass materials aligned with <see cref="FieldWind"/>. Vertex wind lives in
    /// Bugsee/LitColored — rotating combined chunk meshes looked like vertical bobbing.
    /// </summary>
    public sealed class GrassField : MonoBehaviour
    {
        Material[] _mats;

        public void Bind(Material[] mats)
        {
            _mats = mats;
            PushWind();
        }

        void LateUpdate()
        {
            PushWind();
        }

        void PushWind()
        {
            if (_mats == null) return;
            var d = FieldWind.Direction;
            var dir = new Vector4(d.x, 0f, d.z, 0f);
            float strength = Mathf.Clamp(FieldWind.Strength, 0.2f, 2f);
            for (int i = 0; i < _mats.Length; i++)
            {
                var mat = _mats[i];
                if (mat == null) continue;
                mat.SetVector("_WindDir", dir);
                mat.SetFloat("_WindStrength", strength);
            }
        }
    }
}

