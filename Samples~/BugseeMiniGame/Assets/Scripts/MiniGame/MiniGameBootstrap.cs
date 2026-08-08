using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Builds the anteater field sample at runtime: open hills, player, camera,
    /// ambient motion, hilltop Bugsee actions, and HUD.
    /// </summary>
    public sealed class MiniGameBootstrap : MonoBehaviour
    {
        [SerializeField] string bugseeAppToken = "";
        [SerializeField] int worldSeed = 42;

        static bool _worldBuilt;

        void Awake()
        {
            if (_worldBuilt)
            {
                Debug.LogWarning("[MiniGame] World already built; skipping duplicate BuildWorld.");
                return;
            }

            BuildWorld();
            _worldBuilt = true;
        }

        void OnDestroy()
        {
            _worldBuilt = false;
        }

        void BuildWorld()
        {
            // Day/night owns directional lights, ambient, sky clear color, and shadows.
            var dayNight = DayNightController.Ensure();

            // Camera first so a failed world build never leaves a black void.
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = false;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.15f;
            cam.farClipPlane = 220f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.65f, 0.9f);
            camGo.AddComponent<AudioListener>();
            var follow = camGo.AddComponent<ThirdPersonCamera>();
            dayNight.SyncFromBlackoutApi();

            int hillCount = BugseeStationSpawner.Groups.Length;

            FieldWorldBuilder world = null;
            try
            {
                var worldGo = new GameObject("FieldWorld");
                world = worldGo.AddComponent<FieldWorldBuilder>();
                world.Build(hillCount, worldSeed);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = "FallbackGround";
                floor.GetComponent<Renderer>().sharedMaterial =
                    ProceduralMaterials.CreateLit(ProceduralMaterials.GrassA);
            }

            var bugseeGo = new GameObject("BugseeSample");
            var bootstrap = bugseeGo.AddComponent<BugseeSampleBootstrap>();
            bootstrap.Configure(bugseeAppToken, launchOnStart: true);

            var playerGo = new GameObject("Player");
            playerGo.transform.position = world != null ? world.SpawnWorldPos : Vector3.zero;
            playerGo.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            var controller = playerGo.AddComponent<AnteaterController>();
            var visual = AnteaterBuilder.Build(playerGo.transform);
            controller.BindVisual(visual.transform);
            follow.SetTarget(playerGo.transform);

            var logo = LoadLogoTexture();
            if (logo != null)
                AnteaterBuilder.SpawnLogoTotem(playerGo.transform.position + new Vector3(2.2f, 0f, 1.4f), logo);

            if (world != null)
            {
                var ambient = new GameObject("Ambient");
                ambient.AddComponent<DriftParticles>().Init(
                    FieldWorldBuilder.WorldHalfExtent,
                    SampleQuality.DriftParticleCount);

                var spawnerGo = new GameObject("FieldCollectibles");
                spawnerGo.AddComponent<TargetSpawner>().InitField(bootstrap, FieldWorldBuilder.WorldHalfExtent);

                var stationsGo = new GameObject("BugseeStations");
                stationsGo.AddComponent<BugseeStationSpawner>().SpawnAtSummits(world.HillSummits);

                Debug.Log("[MiniGame] Anteater fields ready — category hills=" + world.HillSummits.Count);
            }

            var hudGo = new GameObject("UI");
            hudGo.AddComponent<MiniGameHud>().Init(bootstrap);
            var debug = hudGo.AddComponent<BugseeDebugPanel>();
            debug.Init(bootstrap);
            hudGo.AddComponent<BugseeFloatingHud>().Init(bootstrap, debug);
            BugseeActionForms.Ensure(bootstrap);
        }

        static Texture2D LoadLogoTexture()
        {
            var tex = Resources.Load<Texture2D>("Anteater/bugsee-anteater-logo");
            if (tex != null) return tex;

#if UNITY_EDITOR
            var path = "Assets/Art/Anteater/bugsee-anteater-logo.png";
            tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
#endif
            return tex;
        }
    }
}
