using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>One themed hill with several related Bugsee actions.</summary>
    public sealed class BugseeActionGroup
    {
        public string Title;
        public Color Color;
        public BugseeDemoAction[] Actions;

        public BugseeActionGroup(string title, Color color, params BugseeDemoAction[] actions)
        {
            Title = title;
            Color = color;
            Actions = actions ?? Array.Empty<BugseeDemoAction>();
        }
    }

    /// <summary>
    /// Hilltop zone: approach once, then tap any related action bubble on that hill.
    /// </summary>
    public sealed class BugseeHillStation : MonoBehaviour
    {
        public string Title;
        public Color Color = Color.cyan;
        public BugseeDemoAction[] Actions = Array.Empty<BugseeDemoAction>();

        bool _playerInside;
        static readonly List<BugseeHillStation> Nearby = new List<BugseeHillStation>();
        readonly List<Transform> _beacons = new List<Transform>();

        /// <summary>Last on-screen panel rect in screen pixels (origin bottom-left), or empty.</summary>
        public static Rect ActionPanelScreenRect { get; private set; }

        public static BugseeHillStation Focused
        {
            get
            {
                if (Nearby.Count == 0) return null;
                var player = AnteaterController.Instance;
                if (player == null) return Nearby[Nearby.Count - 1];
                BugseeHillStation best = Nearby[0];
                float bestSqr = float.MaxValue;
                var p = player.transform.position;
                for (int i = 0; i < Nearby.Count; i++)
                {
                    var s = Nearby[i];
                    if (s == null) continue;
                    float d = (s.transform.position - p).sqrMagnitude;
                    if (d < bestSqr)
                    {
                        bestSqr = d;
                        best = s;
                    }
                }

                return best;
            }
        }

        /// <summary>Legacy alias used by HUD/controller null-checks.</summary>
        public static BugseeHillStation FocusedStation => Focused;

        public void Configure(BugseeActionGroup group, IList<Transform> beacons)
        {
            Title = group.Title;
            Color = group.Color;
            Actions = group.Actions;
            _beacons.Clear();
            if (beacons != null)
            {
                for (int i = 0; i < beacons.Count; i++)
                    _beacons.Add(beacons[i]);
            }

            name = "Hill_" + group.Title.Replace(' ', '_');
        }

        void OnTriggerEnter(Collider other)
        {
            if (!IsPlayer(other)) return;
            _playerInside = true;
            if (!Nearby.Contains(this))
                Nearby.Add(this);
        }

        void OnTriggerExit(Collider other)
        {
            if (!IsPlayer(other)) return;
            _playerInside = false;
            Nearby.Remove(this);
            if (ReferenceEquals(Focused, this) || Nearby.Count == 0)
                ActionPanelScreenRect = Rect.zero;
        }

        void OnDestroy()
        {
            Nearby.Remove(this);
            ActionPanelScreenRect = Rect.zero;
        }

        static bool IsPlayer(Collider other)
        {
            return other.GetComponent<AnteaterController>() != null ||
                   other.GetComponentInParent<AnteaterController>() != null ||
                   other.GetComponent<CharacterController>() != null;
        }

        void Update()
        {
            for (int i = 0; i < _beacons.Count; i++)
            {
                var b = _beacons[i];
                if (b == null) continue;
                var rend = b.GetComponent<Renderer>();
                if (rend == null) continue;
                float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 3f + i + GetInstanceID() * 0.01f);
                rend.material.color = Color * pulse + Color.white * (1f - pulse) * 0.15f;
            }

            // E / Space runs the first action while on the hill (bubbles cover the rest).
            if (_playerInside && (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space)) &&
                Actions != null && Actions.Length > 0)
                Activate(Actions[0]);
        }

        public void Activate(BugseeDemoAction action)
        {
            BugseeActionCatalog.Instance?.Run(action);
        }

        void OnGUI()
        {
            if (!_playerInside || Actions == null || Actions.Length == 0)
            {
                if (ReferenceEquals(Focused, this))
                    ActionPanelScreenRect = Rect.zero;
                return;
            }

            var cam = Camera.main;
            if (cam == null) return;

            var world = transform.position + Vector3.up * 2.1f;
            var screen = cam.WorldToScreenPoint(world);
            if (screen.z < 0.2f) return;

            float w = 188f;
            float rowH = 44f;
            float titleH = 28f;
            float totalH = titleH + Actions.Length * (rowH + 6f);
            float x = screen.x - w * 0.5f;
            float yGui = Screen.height - screen.y - totalH * 0.35f;

            // Publish hit-test rect in screen space (bottom-left origin) for the stick.
            ActionPanelScreenRect = new Rect(x, screen.y - totalH * 0.65f, w, totalH + 8f);

            var prev = GUI.backgroundColor;
            GUI.backgroundColor = Color.Lerp(Color, Color.white, 0.25f);
            GUI.Box(new Rect(x, yGui, w, titleH), Title);
            yGui += titleH + 4f;

            for (int i = 0; i < Actions.Length; i++)
            {
                GUI.backgroundColor = Color.Lerp(Color, Color.white, 0.4f);
                var label = BugseeActionCatalog.DisplayName(Actions[i]);
                if (GUI.Button(new Rect(x, yGui, w, rowH), label))
                    Activate(Actions[i]);
                yGui += rowH + 6f;
            }

            GUI.backgroundColor = prev;
        }
    }

    /// <summary>Deprecated single-action station — prefer <see cref="BugseeHillStation"/>.</summary>
    public sealed class BugseeStation : MonoBehaviour
    {
        public static BugseeHillStation Focused => BugseeHillStation.Focused;
    }

    public sealed class BugseeStationSpawner : MonoBehaviour
    {
        public static readonly BugseeActionGroup[] Groups =
        {
            new BugseeActionGroup(
                "Reporting",
                new Color(0.91f, 0.35f, 0.35f),
                BugseeDemoAction.ShowReportDialog,
                BugseeDemoAction.Upload,
                BugseeDemoAction.ToggleReportHandler,
                BugseeDemoAction.CaptureViewHierarchy),

            new BugseeActionGroup(
                "Blackout",
                new Color(0.22f, 0.22f, 0.26f),
                BugseeDemoAction.ToggleBlackout,
                BugseeDemoAction.ObsoletePauseResume),

            new BugseeActionGroup(
                "Logging",
                new Color(0.95f, 0.7f, 0.2f),
                BugseeDemoAction.LogBundle,
                BugseeDemoAction.LogAllLevels,
                BugseeDemoAction.LogException,
                BugseeDemoAction.TestCrash),

            new BugseeActionGroup(
                "Attributes",
                new Color(0.35f, 0.65f, 1f),
                BugseeDemoAction.SetAttribute,
                BugseeDemoAction.GetAttribute,
                BugseeDemoAction.ClearAttribute,
                BugseeDemoAction.ClearAllAttributes),

            new BugseeActionGroup(
                "Identity",
                new Color(0.9f, 0.9f, 0.95f),
                BugseeDemoAction.SetIdentity,
                BugseeDemoAction.GetIdentity,
                BugseeDemoAction.ToggleIdentity,
                BugseeDemoAction.ClearIdentity),

            new BugseeActionGroup(
                "Privacy",
                new Color(0.55f, 0.4f, 0.9f),
                BugseeDemoAction.ToggleSecureRect,
                BugseeDemoAction.ClearSecureRects,
                BugseeDemoAction.ResetVideoPermission),

            new BugseeActionGroup(
                "Feedback",
                new Color(0.2f, 0.75f, 0.85f),
                BugseeDemoAction.ShowFeedback,
                BugseeDemoAction.FeedbackGreeting,
                BugseeDemoAction.ToggleFeedbackListener),

            new BugseeActionGroup(
                "Session",
                new Color(0.35f, 0.8f, 0.45f),
                BugseeDemoAction.Launch,
                BugseeDemoAction.Relaunch,
                BugseeDemoAction.Stop,
                BugseeDemoAction.StatusReadout,
                BugseeDemoAction.ToggleLifecycleListener),

            new BugseeActionGroup(
                "Appearance",
                new Color(0.85f, 0.45f, 0.65f),
                BugseeDemoAction.AppearanceDemo,
                BugseeDemoAction.ToggleFilters)
        };

        /// <summary>Backward-compatible flat list of all grouped actions.</summary>
        public static BugseeDemoAction[] WaypointOrder
        {
            get
            {
                var list = new List<BugseeDemoAction>();
                for (int g = 0; g < Groups.Length; g++)
                {
                    var actions = Groups[g].Actions;
                    for (int i = 0; i < actions.Length; i++)
                        list.Add(actions[i]);
                }

                return list.ToArray();
            }
        }

        public int GroupCount => Groups.Length;

        public void SpawnAtSummits(IReadOnlyList<Vector3> summits)
        {
            int n = Mathf.Min(summits.Count, Groups.Length);
            for (int i = 0; i < n; i++)
                SpawnGroup(Groups[i], summits[i]);
        }

        void SpawnGroup(BugseeActionGroup group, Vector3 summit)
        {
            var root = new GameObject("HillActions_" + group.Title.Replace(' ', '_'));
            root.transform.position = summit;

            // Shared proximity zone for the whole category.
            var zone = root.AddComponent<SphereCollider>();
            zone.isTrigger = true;
            zone.radius = 4.2f;

            var beacons = new List<Transform>();
            int count = group.Actions.Length;
            float ring = count <= 1 ? 0f : 1.15f;
            for (int i = 0; i < count; i++)
            {
                float angle = count == 1 ? 0f : (i / (float)count) * Mathf.PI * 2f - Mathf.PI * 0.5f;
                var offset = new Vector3(Mathf.Cos(angle) * ring, 0f, Mathf.Sin(angle) * ring);
                beacons.Add(SpawnBeacon(root.transform, offset, group.Color, group.Actions[i]));
            }

            // Category totem / title in the center.
            var titleGo = new GameObject("CategoryLabel");
            titleGo.transform.SetParent(root.transform, false);
            titleGo.transform.localPosition = new Vector3(0f, 1.85f, 0f);
            var tm = titleGo.AddComponent<TextMesh>();
            tm.text = group.Title;
            tm.characterSize = 0.12f;
            tm.fontSize = 56;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            titleGo.AddComponent<Billboard>();

            var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pillar.name = "CategoryPillar";
            pillar.transform.SetParent(root.transform, false);
            pillar.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            pillar.transform.localScale = new Vector3(0.35f, 0.55f, 0.35f);
            UnityEngine.Object.Destroy(pillar.GetComponent<Collider>());
            pillar.GetComponent<Renderer>().sharedMaterial = ProceduralMaterials.CreateLit(group.Color * 0.75f);

            var station = root.AddComponent<BugseeHillStation>();
            station.Configure(group, beacons);
        }

        static Transform SpawnBeacon(Transform parent, Vector3 localPos, Color color, BugseeDemoAction action)
        {
            var go = new GameObject("Beacon_" + action);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stem.name = "Stem";
            stem.transform.SetParent(go.transform, false);
            stem.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            stem.transform.localScale = new Vector3(0.1f, 0.35f, 0.1f);
            UnityEngine.Object.Destroy(stem.GetComponent<Collider>());
            stem.GetComponent<Renderer>().sharedMaterial = ProceduralMaterials.CreateLit(color * 0.65f);

            var beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            beacon.name = "Orb";
            beacon.transform.SetParent(go.transform, false);
            beacon.transform.localPosition = new Vector3(0f, 0.85f, 0f);
            beacon.transform.localScale = Vector3.one * 0.42f;
            UnityEngine.Object.Destroy(beacon.GetComponent<Collider>());
            beacon.GetComponent<Renderer>().sharedMaterial = ProceduralMaterials.CreateLit(color);

            var label = new GameObject("Label");
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = new Vector3(0f, 1.35f, 0f);
            var tm = label.AddComponent<TextMesh>();
            tm.text = BugseeActionCatalog.DisplayName(action);
            tm.characterSize = 0.055f;
            tm.fontSize = 42;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            label.AddComponent<Billboard>();

            return beacon.transform;
        }
    }

    public sealed class Billboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }
    }
}
