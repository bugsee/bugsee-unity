using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Drifting translucent "pollen/leaf" spheres for continuous motion in video.</summary>
    public sealed class DriftParticles : MonoBehaviour
    {
        sealed class Speck
        {
            public Transform Transform;
            public Vector3 Velocity;
            public float Phase;
        }

        Speck[] _specks;
        float _halfExtent;

        public void Init(float halfExtent, int count = 48)
        {
            _halfExtent = halfExtent;
            _specks = new Speck[count];
            var mat = ProceduralMaterials.CreateUnlit(new Color(0.9f, 0.95f, 0.5f, 0.45f));
            mat.renderQueue = 3000;

            var rng = new System.Random(123);
            for (int i = 0; i < count; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Speck_" + i;
                go.transform.SetParent(transform, false);
                Object.Destroy(go.GetComponent<Collider>());
                go.GetComponent<Renderer>().sharedMaterial = mat;
                float s = 0.12f + (float)rng.NextDouble() * 0.18f;
                go.transform.localScale = Vector3.one * s;
                go.transform.position = RandomPos(rng);
                _specks[i] = new Speck
                {
                    Transform = go.transform,
                    Velocity = new Vector3(
                        ((float)rng.NextDouble() - 0.5f) * 1.2f,
                        0.15f + (float)rng.NextDouble() * 0.35f,
                        ((float)rng.NextDouble() - 0.5f) * 1.2f),
                    Phase = (float)rng.NextDouble() * 10f
                };
            }
        }

        Vector3 RandomPos(System.Random rng)
        {
            return new Vector3(
                ((float)rng.NextDouble() * 2f - 1f) * _halfExtent,
                1.5f + (float)rng.NextDouble() * 4f,
                ((float)rng.NextDouble() * 2f - 1f) * _halfExtent);
        }

        void Update()
        {
            if (_specks == null) return;
            float dt = Time.deltaTime;
            for (int i = 0; i < _specks.Length; i++)
            {
                var s = _specks[i];
                var p = s.Transform.position;
                p += s.Velocity * dt;
                p.y += Mathf.Sin(Time.time * 1.3f + s.Phase) * 0.15f * dt * 10f;
                if (p.y > 7f || Mathf.Abs(p.x) > _halfExtent + 4f || Mathf.Abs(p.z) > _halfExtent + 4f)
                {
                    p.x = Random.Range(-_halfExtent, _halfExtent);
                    p.z = Random.Range(-_halfExtent, _halfExtent);
                    p.y = 1.2f;
                }

                s.Transform.position = p;
            }
        }
    }
}
