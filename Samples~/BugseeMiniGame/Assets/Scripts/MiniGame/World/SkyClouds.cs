using UnityEngine;
using UnityEngine.Rendering;

namespace Bugsee.Sample
{
    /// <summary>
    /// Soft multi-lobe clouds that always drift across the sky and follow the camera.
    /// </summary>
    public sealed class SkyClouds : MonoBehaviour
    {
        sealed class Cloud
        {
            public Transform Root;
            public Vector3 LocalPos;
            public float Speed;
            public float BobAmp;
            public float BobPhase;
            public float BaseY;
            public Renderer[] Renderers;
        }

        static readonly Color DayTint = new Color(1f, 1f, 1f, 0.92f);
        static readonly Color DayTintShade = new Color(0.86f, 0.9f, 0.96f, 0.88f);
        static readonly Color NightTint = new Color(0.22f, 0.26f, 0.38f, 0.55f);
        static readonly Color NightTintShade = new Color(0.14f, 0.16f, 0.24f, 0.5f);

        Cloud[] _clouds;
        Material _matBright;
        Material _matShade;
        Vector3 _wind = new Vector3(1f, 0f, 0.35f).normalized;
        float _wrapRadius = 110f;
        float _night;

        public static SkyClouds Ensure(Transform parent)
        {
            if (parent != null)
            {
                var existing = parent.GetComponentInChildren<SkyClouds>();
                if (existing != null)
                    return existing;
            }

            var go = new GameObject("SkyClouds");
            if (parent != null)
                go.transform.SetParent(parent, false);
            return go.AddComponent<SkyClouds>();
        }

        public void Build(int count = 18, int seed = 41)
        {
            Clear();
            _wind = new Vector3(1f, 0f, 0.28f).normalized;
            FieldWind.Direction = _wind;
            _wrapRadius = 115f;

            _matBright = ProceduralMaterials.CreateUnlit(DayTint);
            _matBright.renderQueue = 2450;
            _matShade = ProceduralMaterials.CreateUnlit(DayTintShade);
            _matShade.renderQueue = 2450;

            var rng = new System.Random(seed);
            _clouds = new Cloud[count];
            for (int i = 0; i < count; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 25f + (float)rng.NextDouble() * (_wrapRadius - 30f);
                float y = 38f + (float)rng.NextDouble() * 28f;
                var local = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);

                var root = new GameObject("Cloud_" + i).transform;
                root.SetParent(transform, false);
                root.localPosition = local;
                root.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

                float size = 2.4f + (float)rng.NextDouble() * 3.6f;
                var renderers = BuildPuffs(root, size, rng);

                _clouds[i] = new Cloud
                {
                    Root = root,
                    LocalPos = local,
                    Speed = 1.6f + (float)rng.NextDouble() * 3.2f,
                    BobAmp = 0.35f + (float)rng.NextDouble() * 0.7f,
                    BobPhase = (float)rng.NextDouble() * Mathf.PI * 2f,
                    BaseY = y,
                    Renderers = renderers
                };
            }

            ApplyNightBlend(_night);
        }

        public void SetNightBlend(float night)
        {
            _night = Mathf.Clamp01(night);
            ApplyNightBlend(_night);
        }

        void Clear()
        {
            if (_clouds != null)
            {
                for (int i = 0; i < _clouds.Length; i++)
                {
                    if (_clouds[i] != null && _clouds[i].Root != null)
                        Destroy(_clouds[i].Root.gameObject);
                }
            }

            _clouds = null;
            if (_matBright != null) Destroy(_matBright);
            if (_matShade != null) Destroy(_matShade);
            _matBright = null;
            _matShade = null;
        }

        void OnDestroy()
        {
            Clear();
        }

        void LateUpdate()
        {
            if (_clouds == null) return;

            var cam = Camera.main;
            if (cam != null)
                transform.position = new Vector3(cam.transform.position.x, 0f, cam.transform.position.z);

            float dt = Time.deltaTime;
            float wrapSqr = _wrapRadius * _wrapRadius;
            for (int i = 0; i < _clouds.Length; i++)
            {
                var c = _clouds[i];
                c.LocalPos += _wind * (c.Speed * dt);

                // Continuous wrap on the horizontal ring so motion never stops.
                var flat = new Vector2(c.LocalPos.x, c.LocalPos.z);
                if (flat.sqrMagnitude > wrapSqr)
                {
                    flat = -flat.normalized * (_wrapRadius * 0.92f);
                    // Nudge sideways so re-entry isn't a straight reverse.
                    var side = new Vector2(-flat.y, flat.x).normalized;
                    flat += side * (8f + (i % 5) * 3f);
                    c.LocalPos.x = flat.x;
                    c.LocalPos.z = flat.y;
                }

                float bob = Mathf.Sin(Time.time * 0.35f + c.BobPhase) * c.BobAmp;
                c.Root.localPosition = new Vector3(c.LocalPos.x, c.BaseY + bob, c.LocalPos.z);
            }
        }

        Renderer[] BuildPuffs(Transform root, float size, System.Random rng)
        {
            int puffs = 5 + rng.Next(0, 5);
            var renderers = new Renderer[puffs];
            for (int i = 0; i < puffs; i++)
            {
                var puff = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                puff.name = "Puff_" + i;
                puff.transform.SetParent(root, false);
                Object.Destroy(puff.GetComponent<Collider>());

                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radial = size * (0.2f + (float)rng.NextDouble() * 1.35f);
                float y = size * ((float)rng.NextDouble() * 0.45f - 0.08f);
                puff.transform.localPosition = new Vector3(
                    Mathf.Cos(a) * radial,
                    y,
                    Mathf.Sin(a) * radial);

                float sx = size * (1.4f + (float)rng.NextDouble() * 2.0f);
                float sy = size * (0.65f + (float)rng.NextDouble() * 0.7f);
                float sz = size * (1.3f + (float)rng.NextDouble() * 1.9f);
                puff.transform.localScale = new Vector3(sx, sy, sz);

                var rend = puff.GetComponent<Renderer>();
                rend.sharedMaterial = i == 0 || rng.NextDouble() > 0.45 ? _matBright : _matShade;
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
                renderers[i] = rend;
            }

            return renderers;
        }

        void ApplyNightBlend(float night)
        {
            if (_matBright != null)
            {
                var c = Color.Lerp(DayTint, NightTint, night);
                _matBright.SetColor("_Color", c);
                _matBright.color = c;
            }

            if (_matShade != null)
            {
                var c = Color.Lerp(DayTintShade, NightTintShade, night);
                _matShade.SetColor("_Color", c);
                _matShade.color = c;
            }
        }
    }
}
