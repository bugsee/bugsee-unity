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
        [Tooltip("World m/s at which walk clip speed=1 matches movement.")]
        [SerializeField] float walkAnimReferenceMoveSpeed = 2.6f;
        [SerializeField] float walkAnimMinMultiplier = 0.55f;
        [SerializeField] float walkAnimMaxMultiplier = 4.5f;

        CharacterController _cc;
        AnteaterLeg[] _legs;
        Transform _visualRoot;
        Vector3 _visualBaseLocalPos;
        bool _useGeneratedVisual;
        bool _useSkinnedWalk;
        AnimationClip _walkClip;
        GameObject _animatedRoot;
        float _walkTime;
        Vector3 _moveInput;
        float _verticalVel;
        float _walkPhase;
        bool _pointerActive;
        bool _pointerDragging;
        int _moveFingerId = -1;
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

            var generated = visualRoot != null ? visualRoot.GetComponent<AnteaterGeneratedVisual>() : null;
            _useGeneratedVisual = generated != null;
            _useSkinnedWalk = generated != null && generated.HasSkinnedWalk;
            _walkClip = generated != null ? generated.WalkClip : null;
            _animatedRoot = generated != null ? generated.AnimatedRoot : null;
            _walkTime = 0f;
            if (_walkClip != null && _animatedRoot != null)
                _walkClip.SampleAnimation(_animatedRoot, 0f);

            _legs = _useGeneratedVisual || visualRoot == null
                ? System.Array.Empty<AnteaterLeg>()
                : visualRoot.GetComponentsInChildren<AnteaterLeg>();
        }

        void Awake()
        {
            Instance = this;
            _cc = gameObject.GetComponent<CharacterController>();
            if (_cc == null)
                _cc = gameObject.AddComponent<CharacterController>();

            // Authoritative locomotion capsule — tuned for tree trunks / totems / hills.
            _cc.height = 0.85f;
            _cc.radius = 0.38f;
            // Capsule bottom at ~y=0 so the controller pivot matches the paws / ground.
            _cc.center = new Vector3(0f, 0.425f, 0f);
            _cc.stepOffset = 0.3f;
            _cc.slopeLimit = 55f;
            _cc.skinWidth = 0.08f;
            _cc.minMoveDistance = 0f;
            // Overlap recovery can shove the capsule into one-sided hill MeshColliders.
            _cc.enableOverlapRecovery = false;
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        void Update()
        {
            // Stick/keyboard in pad space: x = yaw around character, z = forward/back along facing.
            Vector3 stick = Vector3.zero;
            if (!BugseeActionForms.IsBlockingInput)
            {
                stick = ReadKeyboard() + ReadPointerStick();
                if (stick.sqrMagnitude > 1f)
                    stick.Normalize();
            }

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

            // Horizontal then vertical — combined Move + side hits on hills can bury the capsule
            // inside the dome MeshCollider (no interior faces → fall-through).
            if (planar.sqrMagnitude > 0.0001f)
                _cc.Move(planar * dt);

            if (_cc.isGrounded && _verticalVel < 0f)
                _verticalVel = -2f;
            _verticalVel += gravity * dt;
            _cc.Move(Vector3.up * (_verticalVel * dt));

            RecoverIfBuried();
        }

        /// <summary>
        /// If the capsule sinks under walkable ground (hill mesh / summit pad), snap back up.
        /// </summary>
        void RecoverIfBuried()
        {
            // Cast from well above so we still find the outer hill surface after fall-through.
            var origin = transform.position + Vector3.up * 10f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore);
            float bestY = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || hit.collider is CharacterController)
                    continue;
                if (hit.collider.GetComponentInParent<AnteaterController>() != null)
                    continue;
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.45f)
                    continue;
                if (hit.point.y > bestY)
                {
                    bestY = hit.point.y;
                    found = true;
                }
            }

            if (!found)
                return;

            // Feet should sit near ground; if clearly below, snap back up.
            if (transform.position.y >= bestY - 0.05f)
                return;

            _cc.enabled = false;
            transform.position = new Vector3(transform.position.x, bestY + 0.02f, transform.position.z);
            _cc.enabled = true;
            _verticalVel = -2f;
        }

        void AnimateWalk(float dt)
        {
            float gait = IsMoving ? 9f + _moveInput.magnitude * 4f : 0f;
            _walkPhase += dt * gait;

            if (_useSkinnedWalk && _walkClip != null && _animatedRoot != null)
            {
                // Scale clip playback with actual planar move speed so paws keep up with CC.
                float planarSpeed = moveSpeed * _moveInput.magnitude;
                float animSpeed = 0f;
                if (IsMoving && walkAnimReferenceMoveSpeed > 0.01f)
                {
                    animSpeed = Mathf.Clamp(
                        planarSpeed / walkAnimReferenceMoveSpeed,
                        walkAnimMinMultiplier,
                        walkAnimMaxMultiplier);
                }

                float clipLen = _walkClip.length > 0.01f ? _walkClip.length : 1f;
                if (animSpeed > 0.001f)
                {
                    _walkTime += dt * animSpeed;
                    _walkTime %= clipLen;
                    if (_walkTime < 0f)
                        _walkTime += clipLen;
                }

                _walkClip.SampleAnimation(_animatedRoot, _walkTime);
                return;
            }

            // Static generated mesh — light bob so motion still reads.
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
                if (_moveFingerId >= 0)
                {
                    for (int i = 0; i < Input.touchCount; i++)
                    {
                        var t = Input.GetTouch(i);
                        if (t.fingerId != _moveFingerId) continue;

                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            _pointerActive = false;
                            _pointerDragging = false;
                            _moveFingerId = -1;
                            return Vector3.zero;
                        }

                        if (IsBlockedByUi(t.position) || ThirdPersonCamera.IsInsideLookPad(t.position))
                        {
                            _pointerActive = false;
                            _pointerDragging = false;
                            _moveFingerId = -1;
                            return Vector3.zero;
                        }

                        _pointerCurrent = t.position;
                        var delta = _pointerCurrent - DefaultStickBase();
                        if (!_pointerDragging && delta.sqrMagnitude >= mouseDragThresholdPx * mouseDragThresholdPx)
                            _pointerDragging = true;
                        return _pointerDragging ? StickFromDelta(delta) : Vector3.zero;
                    }

                    _pointerActive = false;
                    _pointerDragging = false;
                    _moveFingerId = -1;
                }

                for (int i = 0; i < Input.touchCount; i++)
                {
                    var t = Input.GetTouch(i);
                    if (t.phase != TouchPhase.Began) continue;
                    if (IsBlockedByUi(t.position) || !IsInsideStickPad(t.position))
                        continue;
                    if (ThirdPersonCamera.IsInsideLookPad(t.position))
                        continue;

                    _pointerActive = true;
                    _pointerDragging = false;
                    _moveFingerId = t.fingerId;
                    _pointerStart = DefaultStickBase();
                    _pointerCurrent = t.position;
                    return Vector3.zero;
                }

                return Vector3.zero;
            }

            _moveFingerId = -1;
            Vector2 mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                if (IsBlockedByUi(mouse) || !IsInsideStickPad(mouse) || ThirdPersonCamera.IsInsideLookPad(mouse))
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

            if (IsBlockedByUi(mouse) || ThirdPersonCamera.IsInsideLookPad(mouse))
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
            if (BugseeActionForms.IsBlockingInput)
                return true;

            // Top-right Bugsee HUD only — bottom-right is the look stick.
            if (screen.x > Screen.width - 180f && screen.y > Screen.height * 0.45f)
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
