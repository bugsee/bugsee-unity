using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Third-person anteater: WASD / left virtual stick, walk-cycle legs, CharacterController.</summary>
    public sealed class AnteaterController : MonoBehaviour
    {
        [SerializeField] float moveSpeed = 9.5f;
        [SerializeField] float turnSpeedDeg = 95f; // max yaw °/s at full stick deflection
        [SerializeField] float mouseDragThresholdPx = 12f;
        [SerializeField] float gravity = -20f;

        CharacterController _cc;
        AnteaterLeg[] _legs;
        Transform _visualRoot;
        Vector3 _visualBaseLocalPos;
        bool _useGeneratedVisual;
        Vector3 _moveInput;
        float _verticalVel;
        float _walkPhase;
        bool _pointerActive;
        bool _pointerDragging;
        Vector2 _pointerStart;
        Vector2 _pointerCurrent;
        bool _hasMoved;

        public static AnteaterController Instance { get; private set; }
        public bool HasMoved => _hasMoved;
        public bool IsMoving => _moveInput.sqrMagnitude > 0.02f;

        /// <summary>True while the left virtual stick is being held.</summary>
        public bool StickActive => _pointerActive;

        public void BindVisual(Transform visualRoot)
        {
            _visualRoot = visualRoot;
            _visualBaseLocalPos = visualRoot != null ? visualRoot.localPosition : Vector3.zero;
            _useGeneratedVisual = visualRoot != null && visualRoot.GetComponent<AnteaterGeneratedVisual>() != null;
            _legs = _useGeneratedVisual || visualRoot == null
                ? System.Array.Empty<AnteaterLeg>()
                : visualRoot.GetComponentsInChildren<AnteaterLeg>();
        }

        void Awake()
        {
            Instance = this;
            _cc = gameObject.GetComponent<CharacterController>();
            if (_cc == null)
            {
                _cc = gameObject.AddComponent<CharacterController>();
                _cc.height = 0.95f;
                _cc.radius = 0.42f;
                _cc.center = new Vector3(0f, 0.5f, 0f);
                _cc.stepOffset = 0.5f;
                _cc.slopeLimit = 55f;
                _cc.skinWidth = 0.06f;
            }
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        void Update()
        {
            // Stick/keyboard in pad space: x = yaw around character, z = forward/back along facing.
            var stick = ReadKeyboard() + ReadPointerStick();
            if (stick.sqrMagnitude > 1f)
                stick.Normalize();

            if (stick.sqrMagnitude > 0.01f)
                _hasMoved = true;

            // Yaw around the character's ground pivot (not a camera-space strafe).
            ApplyYaw(stick.x, Time.deltaTime);

            // Move only along facing — stick down walks backward without flipping.
            _moveInput = transform.forward * stick.z;

            MoveCharacter(Time.deltaTime);
            AnimateWalk(Time.deltaTime);
        }

        void ApplyYaw(float stickX, float dt)
        {
            float response = StickTurnResponse(Mathf.Abs(stickX));
            if (response <= 0.001f)
                return;

            float yawDeg = stickX * turnSpeedDeg * response * dt;
            // Rotate around world up through the character root (feet / ground center).
            transform.Rotate(0f, yawDeg, 0f, Space.World);
        }

        /// <summary>
        /// Map stick deflection 0..1 → turn response. Quadratic so small offsets stay gentle
        /// and outer range reaches full sensitivity.
        /// </summary>
        static float StickTurnResponse(float magnitude)
        {
            const float deadzone = 0.08f;
            float t = Mathf.InverseLerp(deadzone, 1f, Mathf.Clamp01(magnitude));
            return t * t;
        }

        void MoveCharacter(float dt)
        {
            var planar = new Vector3(_moveInput.x, 0f, _moveInput.z) * moveSpeed;
            if (_cc.isGrounded && _verticalVel < 0f)
                _verticalVel = -2f;
            _verticalVel += gravity * dt;
            _cc.Move((planar + Vector3.up * _verticalVel) * dt);
        }

        void AnimateWalk(float dt)
        {
            float gait = IsMoving ? 9f + _moveInput.magnitude * 4f : 0f;
            _walkPhase += dt * gait;

            // Generated mesh has no skin/clips — light bob so motion still reads.
            if (_useGeneratedVisual && _visualRoot != null)
            {
                float bob = IsMoving ? Mathf.Sin(_walkPhase * 2f) * 0.025f : 0f;
                float sway = IsMoving ? Mathf.Sin(_walkPhase) * 1.8f : 0f;
                _visualRoot.localPosition = _visualBaseLocalPos + new Vector3(0f, bob, 0f);
                _visualRoot.localRotation = Quaternion.Euler(0f, 0f, sway);
                return;
            }

            if (_legs == null || _legs.Length == 0) return;

            for (int i = 0; i < _legs.Length; i++)
            {
                var leg = _legs[i];
                if (leg == null) continue;

                // Diagonal gait: FL+BR together, FR+BL together.
                bool pairA = leg.IsFront == (leg.Side < 0);
                float phase = _walkPhase + (pairA ? 0f : Mathf.PI);
                float swing = IsMoving ? Mathf.Sin(phase) * (leg.IsFront ? 26f : 22f) : 0f;
                float lift = IsMoving ? Mathf.Max(0f, Mathf.Sin(phase)) * 8f : 0f;
                float knee = IsMoving ? Mathf.Abs(Mathf.Sin(phase)) * (leg.IsFront ? 28f : 24f) : 0f;

                leg.transform.localRotation = Quaternion.Euler(
                    leg.BaseLocalEuler.x + swing - lift * 0.35f,
                    leg.BaseLocalEuler.y,
                    leg.BaseLocalEuler.z);

                if (leg.Lower != null)
                {
                    leg.Lower.localRotation = Quaternion.Euler(
                        leg.LowerBaseLocalEuler.x + knee,
                        leg.LowerBaseLocalEuler.y,
                        leg.LowerBaseLocalEuler.z);
                }
            }
        }

        static Vector3 ReadKeyboard()
        {
            return new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        }

        Vector3 ReadPointerStick()
        {
            if (Input.touchCount > 0)
            {
                var t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Began)
                {
                    if (IsBlockedByUi(t.position) || !IsInsideStickPad(t.position))
                    {
                        _pointerActive = false;
                        _pointerDragging = false;
                        return Vector3.zero;
                    }

                    _pointerActive = true;
                    _pointerDragging = false;
                    _pointerStart = DefaultStickBase();
                    _pointerCurrent = t.position;
                }

                if (!_pointerActive) return Vector3.zero;

                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    _pointerActive = false;
                    _pointerDragging = false;
                    return Vector3.zero;
                }

                if (IsBlockedByUi(t.position))
                {
                    _pointerActive = false;
                    _pointerDragging = false;
                    return Vector3.zero;
                }

                _pointerCurrent = t.position;
                var delta = _pointerCurrent - DefaultStickBase();
                if (!_pointerDragging && delta.sqrMagnitude >= mouseDragThresholdPx * mouseDragThresholdPx)
                    _pointerDragging = true;

                return _pointerDragging ? StickFromDelta(delta) : Vector3.zero;
            }

            Vector2 mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                if (IsBlockedByUi(mouse) || !IsInsideStickPad(mouse))
                {
                    _pointerActive = false;
                    _pointerDragging = false;
                    return Vector3.zero;
                }

                _pointerActive = true;
                _pointerDragging = false;
                _pointerStart = DefaultStickBase();
                _pointerCurrent = mouse;
            }

            if (Input.GetMouseButtonUp(0))
            {
                _pointerActive = false;
                _pointerDragging = false;
            }

            if (!_pointerActive || !Input.GetMouseButton(0))
                return Vector3.zero;

            if (IsBlockedByUi(mouse))
            {
                _pointerActive = false;
                _pointerDragging = false;
                return Vector3.zero;
            }

            _pointerCurrent = mouse;
            var mouseDelta = _pointerCurrent - DefaultStickBase();
            if (!_pointerDragging && mouseDelta.sqrMagnitude >= mouseDragThresholdPx * mouseDragThresholdPx)
                _pointerDragging = true;

            return _pointerDragging ? StickFromDelta(mouseDelta) : Vector3.zero;
        }

        static bool IsInsideStickPad(Vector2 screen)
        {
            // Slightly larger than the drawn pad so the control is easy to grab.
            float hitR = StickRadiusPx * 1.35f;
            return (screen - DefaultStickBase()).sqrMagnitude <= hitR * hitR;
        }

        static bool IsBlockedByUi(Vector2 screen)
        {
            if (screen.x > Screen.width - 180f)
                return true;

            var panel = BugseeHillStation.ActionPanelScreenRect;
            if (panel.width > 1f && panel.height > 1f)
            {
                // Inflate a bit so edge taps still count as UI.
                panel.xMin -= 12f;
                panel.xMax += 12f;
                panel.yMin -= 12f;
                panel.yMax += 12f;
                if (panel.Contains(screen))
                    return true;
            }

            var debug = BugseeDebugPanel.Instance;
            if (debug != null && debug.Visible)
            {
                var w = Mathf.Min(420f, Screen.width - 24f);
                var guiY = Screen.height - screen.y;
                if (screen.x >= 12f && screen.x <= 12f + w && guiY >= 80f && guiY <= Screen.height - 20f)
                    return true;
            }

            return false;
        }

        static float StickRadiusPx => Screen.height * 0.12f;

        static Vector2 DefaultStickBase()
        {
            return new Vector2(Screen.width * 0.18f, Screen.height * 0.22f);
        }

        static Vector3 StickFromDelta(Vector2 delta)
        {
            float r = StickRadiusPx;
            if (r < 1f) return Vector3.zero;
            var n = Vector2.ClampMagnitude(delta / r, 1f);
            return new Vector3(n.x, 0f, n.y);
        }

        static Texture2D _circleTex;

        static Texture2D CircleTexture()
        {
            if (_circleTex != null) return _circleTex;

            const int size = 128;
            _circleTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _circleTex.wrapMode = TextureWrapMode.Clamp;
            _circleTex.filterMode = FilterMode.Bilinear;
            float r = (size - 1) * 0.5f;
            float rInner = r - 1.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r;
                    float dy = y - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(rInner + 1.25f - d);
                    // Soft ring edge for the outer pad look.
                    _circleTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }

            _circleTex.Apply(false, true);
            return _circleTex;
        }

        void OnGUI()
        {
            DrawVirtualStick();
        }

        void DrawVirtualStick()
        {
            float radius = StickRadiusPx;
            Vector2 basePos = DefaultStickBase();
            Vector2 knobPos;

            if (_pointerActive)
            {
                var delta = Vector2.ClampMagnitude(_pointerCurrent - basePos, radius);
                knobPos = basePos + delta;
            }
            else
            {
                var kb = ReadKeyboard();
                if (kb.sqrMagnitude > 0.01f)
                {
                    var n = new Vector2(kb.x, kb.z);
                    if (n.sqrMagnitude > 1f) n.Normalize();
                    knobPos = basePos + n * radius;
                }
                else
                {
                    knobPos = basePos;
                }
            }

            float baseGuiX = basePos.x;
            float baseGuiY = Screen.height - basePos.y;
            float knobGuiX = knobPos.x;
            float knobGuiY = Screen.height - knobPos.y;

            float baseSize = radius * 2f;
            float knobSize = radius * 0.9f;
            var circle = CircleTexture();
            var prev = GUI.color;

            // Outer pad (round).
            GUI.color = new Color(1f, 1f, 1f, _pointerActive ? 0.4f : 0.22f);
            GUI.DrawTexture(
                new Rect(baseGuiX - baseSize * 0.5f, baseGuiY - baseSize * 0.5f, baseSize, baseSize),
                circle,
                ScaleMode.StretchToFill,
                true);

            // Subtle inner ring to read as a control well.
            float well = baseSize * 0.72f;
            GUI.color = new Color(1f, 1f, 1f, _pointerActive ? 0.2f : 0.12f);
            GUI.DrawTexture(
                new Rect(baseGuiX - well * 0.5f, baseGuiY - well * 0.5f, well, well),
                circle,
                ScaleMode.StretchToFill,
                true);

            // Thumb knob (round, coral).
            GUI.color = new Color(0.95f, 0.45f, 0.4f, _pointerActive ? 0.9f : 0.45f);
            GUI.DrawTexture(
                new Rect(knobGuiX - knobSize * 0.5f, knobGuiY - knobSize * 0.5f, knobSize, knobSize),
                circle,
                ScaleMode.StretchToFill,
                true);

            if (!_pointerActive)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                GUI.Label(new Rect(baseGuiX - 54f, baseGuiY + baseSize * 0.55f, 108f, 22f), "Move");
            }

            GUI.color = prev;
        }

        void OnTriggerEnter(Collider other)
        {
            var collectible = other.GetComponent<Collectible>();
            if (collectible != null)
                collectible.Collect();
        }
    }
}
