using UnityEngine;
using UnityEngine.Rendering;

namespace Bugsee.Sample
{
    /// <summary>
    /// Day/night presentation driven by Bugsee blackout: StartBlackout → night (moon + stars),
    /// EndBlackout → day (sun). Both celestial lights cast shadows (moon much weaker).
    /// </summary>
    public sealed class DayNightController : MonoBehaviour
    {
        public static DayNightController Instance { get; private set; }

        [SerializeField] float transitionSeconds = 1.35f;

        Light _sunLight;
        Light _moonLight;
        Transform _sunBody;
        Transform _moonBody;
        Transform _starsRoot;
        SkyClouds _clouds;
        Camera _camera;
        bool _nightTarget;
        float _blend; // 0 = day, 1 = night
        bool? _lastPolledBlackout;

        static readonly Color DaySky = new Color(0.45f, 0.65f, 0.9f);
        static readonly Color NightSky = new Color(0.04f, 0.05f, 0.12f);
        static readonly Color DayAmbientSky = new Color(0.55f, 0.7f, 0.95f);
        static readonly Color DayAmbientEq = new Color(0.45f, 0.55f, 0.4f);
        static readonly Color DayAmbientGnd = new Color(0.25f, 0.22f, 0.18f);
        static readonly Color NightAmbientSky = new Color(0.08f, 0.1f, 0.18f);
        static readonly Color NightAmbientEq = new Color(0.06f, 0.07f, 0.12f);
        static readonly Color NightAmbientGnd = new Color(0.03f, 0.03f, 0.05f);

        public bool IsNight => _nightTarget;
        public float NightBlend => _blend;

        public static DayNightController Ensure()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject("DayNight");
            return go.AddComponent<DayNightController>();
        }

        void Awake()
        {
            Instance = this;
            BuildCelestials();
            ApplyQuality();
            _nightTarget = false;
            _blend = 0f;
            ApplyVisuals(0f);
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        void Update()
        {
            // Keep in sync if blackout flips from HUD/debug/native without going through SetNight.
            if (Application.isPlaying)
            {
                bool blackout = Bugsee.IsBlackout;
                if (_lastPolledBlackout != blackout)
                {
                    _lastPolledBlackout = blackout;
                    _nightTarget = blackout;
                }
            }

            float target = _nightTarget ? 1f : 0f;
            if (!Mathf.Approximately(_blend, target))
            {
                float step = transitionSeconds > 0.01f ? Time.deltaTime / transitionSeconds : 1f;
                _blend = Mathf.MoveTowards(_blend, target, step);
                ApplyVisuals(_blend);
            }

            BillboardCelestials();
        }

        /// <summary>Explicit night toggle (also used when native IsBlackout is a no-op).</summary>
        public void SetNight(bool night)
        {
            _nightTarget = night;
            _lastPolledBlackout = night;
        }

        public void SyncFromBlackoutApi()
        {
            SetNight(Bugsee.IsBlackout);
        }

        void BuildCelestials()
        {
            _camera = Camera.main;

            // Sun — steeper angle → shorter shadows, less grazing-angle acne.
            var sunGo = new GameObject("SunLight");
            sunGo.transform.SetParent(transform, false);
            sunGo.transform.rotation = Quaternion.Euler(65f, -38f, 0f);
            _sunLight = sunGo.AddComponent<Light>();
            _sunLight.type = LightType.Directional;
            _sunLight.color = new Color(1f, 0.96f, 0.88f);
            _sunLight.intensity = 1.25f;
            // Soft on desktop (hides Meshy depth acne); hard/cheaper on mobile.
            bool mobile = SampleQuality.IsMobile;
            _sunLight.shadows = mobile ? LightShadows.Hard : LightShadows.Soft;
            _sunLight.shadowStrength = 0.55f;
            _sunLight.shadowBias = 0.08f;
            _sunLight.shadowNormalBias = 1.1f;
            _sunLight.shadowNearPlane = 0.5f;
            _sunLight.shadowResolution = mobile
                ? LightShadowResolution.Medium
                : LightShadowResolution.VeryHigh;

            _sunBody = CreateCelestialBody("SunDisc", new Color(1f, 0.92f, 0.55f), 6.5f);

            // Moon — cool fill, weaker shadows.
            var moonGo = new GameObject("MoonLight");
            moonGo.transform.SetParent(transform, false);
            moonGo.transform.rotation = Quaternion.Euler(60f, 145f, 0f);
            _moonLight = moonGo.AddComponent<Light>();
            _moonLight.type = LightType.Directional;
            _moonLight.color = new Color(0.72f, 0.8f, 1f);
            _moonLight.intensity = 0.28f;
            _moonLight.shadows = mobile ? LightShadows.Hard : LightShadows.Soft;
            _moonLight.shadowStrength = 0.18f;
            _moonLight.shadowBias = 0.09f;
            _moonLight.shadowNormalBias = 1.1f;
            _moonLight.shadowNearPlane = 0.5f;
            _moonLight.shadowResolution = mobile
                ? LightShadowResolution.Medium
                : LightShadowResolution.VeryHigh;
            _moonLight.enabled = false;

            _moonBody = CreateCelestialBody("MoonDisc", new Color(0.85f, 0.9f, 1f), 4.2f);

            BuildStarfield(mobile ? 60 : 160);

            _clouds = SkyClouds.Ensure(transform);
            _clouds.Build(SampleQuality.CloudCount, seed: 41);
        }

        Transform CreateCelestialBody(string name, Color color, float scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * scale;
            Object.Destroy(go.GetComponent<Collider>());
            var mat = ProceduralMaterials.CreateUnlit(color);
            mat.renderQueue = 2500;
            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = mat;
            rend.shadowCastingMode = ShadowCastingMode.Off;
            rend.receiveShadows = false;
            return go.transform;
        }

        void BuildStarfield(int count)
        {
            _starsRoot = new GameObject("Stars").transform;
            _starsRoot.SetParent(transform, false);

            var mat = ProceduralMaterials.CreateUnlit(new Color(0.95f, 0.97f, 1f, 1f));
            mat.renderQueue = 2400;
            var rng = new System.Random(77);
            for (int i = 0; i < count; i++)
            {
                // Hemisphere above the field.
                float yaw = (float)rng.NextDouble() * Mathf.PI * 2f;
                float pitch = (float)rng.NextDouble() * Mathf.PI * 0.48f; // 0=horizon-ish → up
                var dir = new Vector3(
                    Mathf.Cos(pitch) * Mathf.Cos(yaw),
                    Mathf.Sin(pitch) + 0.08f,
                    Mathf.Cos(pitch) * Mathf.Sin(yaw)).normalized;
                float dist = 95f + (float)rng.NextDouble() * 40f;

                var star = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                star.name = "Star_" + i;
                star.transform.SetParent(_starsRoot, false);
                star.transform.position = dir * dist;
                float s = 0.25f + (float)rng.NextDouble() * 0.55f;
                star.transform.localScale = Vector3.one * s;
                Object.Destroy(star.GetComponent<Collider>());
                var rend = star.GetComponent<Renderer>();
                rend.sharedMaterial = mat;
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
            }

            _starsRoot.gameObject.SetActive(false);
        }

        void ApplyQuality()
        {
            SampleQuality.ApplyRuntimeSettings();
        }

        void ApplyVisuals(float night)
        {
            if (_camera == null)
                _camera = Camera.main;

            float day = 1f - night;

            if (_sunLight != null)
            {
                _sunLight.enabled = day > 0.02f;
                _sunLight.intensity = Mathf.Lerp(0f, 1.25f, day);
                _sunLight.shadowStrength = Mathf.Lerp(0f, 0.55f, day);
            }

            if (_moonLight != null)
            {
                _moonLight.enabled = night > 0.02f;
                _moonLight.intensity = Mathf.Lerp(0f, 0.28f, night);
                _moonLight.shadowStrength = Mathf.Lerp(0f, 0.18f, night);
            }

            if (_sunBody != null)
                _sunBody.gameObject.SetActive(day > 0.15f);
            if (_moonBody != null)
                _moonBody.gameObject.SetActive(night > 0.15f);
            if (_starsRoot != null)
                _starsRoot.gameObject.SetActive(night > 0.2f);

            if (_clouds != null)
                _clouds.SetNightBlend(night);

            if (_camera != null)
                _camera.backgroundColor = Color.Lerp(DaySky, NightSky, night);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Color.Lerp(DayAmbientSky, NightAmbientSky, night);
            RenderSettings.ambientEquatorColor = Color.Lerp(DayAmbientEq, NightAmbientEq, night);
            RenderSettings.ambientGroundColor = Color.Lerp(DayAmbientGnd, NightAmbientGnd, night);
            RenderSettings.ambientIntensity = Mathf.Lerp(1.05f, 0.55f, night);
            RenderSettings.skybox = null;
        }

        void BillboardCelestials()
        {
            if (_camera == null)
                _camera = Camera.main;
            if (_camera == null)
                return;

            var camPos = _camera.transform.position;
            // Keep sun/moon/stars centered on the player camera so the sky travels with exploration.
            if (_starsRoot != null)
                _starsRoot.position = new Vector3(camPos.x, 0f, camPos.z);

            if (_sunBody != null && _sunLight != null)
                _sunBody.position = camPos - _sunLight.transform.forward * 140f;

            if (_moonBody != null && _moonLight != null)
                _moonBody.position = camPos - _moonLight.transform.forward * 130f;
        }
    }
}
