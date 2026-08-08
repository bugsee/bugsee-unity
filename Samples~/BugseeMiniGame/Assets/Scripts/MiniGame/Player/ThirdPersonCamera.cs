using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Third-person follow that orbits the character's ground pivot (feet).
    /// Left stick (AnteaterController) moves; right virtual stick orbits the camera.
    /// After the look stick is released, orbit recenters behind the character once they move.
    /// </summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] Vector3 offset = new Vector3(0f, 1.9f, -3.05f);
        [SerializeField] float followLerp = 16f;
        [SerializeField] float lookHeight = 0.75f;
        [SerializeField] float collisionRadius = 0.22f;
        [SerializeField] float maxOrbitYawDeg = 105f;
        [SerializeField] float maxOrbitPitchDeg = 28f;
        [SerializeField] float recenterLerp = 5.5f;
        [SerializeField] float mouseDragThresholdPx = 10f;

        Transform _target;
        float _orbitYaw;
        float _orbitPitch;

        bool _lookActive;
        bool _lookDragging;
        int _lookFingerId = -1;
        Vector2 _lookPointerCurrent;

        static Texture2D _circleTex;

        public void SetTarget(Transform target)
        {
            _target = target;
            _orbitYaw = 0f;
            _orbitPitch = 0f;
            if (_target != null)
                SnapNow();
        }

        public void SnapNow()
        {
            if (_target == null) return;
            transform.position = DesiredPosition();
            transform.rotation = DesiredRotation();
        }

        /// <summary>Screen-space center of the look stick (for move stick to ignore).</summary>
        public static Vector2 LookStickBase()
        {
            return new Vector2(Screen.width * 0.82f, Screen.height * 0.22f);
        }

        public static float StickRadiusPx => Screen.height * 0.12f;

        public static bool IsInsideLookPad(Vector2 screen)
        {
            float hitR = StickRadiusPx * 1.35f;
            return (screen - LookStickBase()).sqrMagnitude <= hitR * hitR;
        }

        void LateUpdate()
        {
            if (_target == null) return;

            UpdateLookInput();
            UpdateOrbitRecentering(Time.deltaTime);

            var pivot = CharacterPivot();
            var desired = DesiredPosition();
            desired = TrimForCollision(pivot, desired);

            float t = 1f - Mathf.Exp(-followLerp * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, DesiredRotation(), t);
        }

        void UpdateLookInput()
        {
            Vector2 stick = ReadLookStick();
            if (_lookActive && _lookDragging)
            {
                _orbitYaw = stick.x * maxOrbitYawDeg;
                _orbitPitch = Mathf.Clamp(-stick.y * maxOrbitPitchDeg, -maxOrbitPitchDeg, maxOrbitPitchDeg * 0.65f);
            }
        }

        void UpdateOrbitRecentering(float dt)
        {
            // Hold free-look while the right knob is down. After release, wait until the
            // character moves, then ease back to the default behind-the-character framing.
            if (_lookActive)
                return;

            var player = AnteaterController.Instance;
            if (player == null || !player.IsMoving)
                return;

            float t = 1f - Mathf.Exp(-recenterLerp * dt);
            _orbitYaw = Mathf.LerpAngle(_orbitYaw, 0f, t);
            _orbitPitch = Mathf.Lerp(_orbitPitch, 0f, t);
            if (Mathf.Abs(Mathf.DeltaAngle(_orbitYaw, 0f)) < 0.15f)
                _orbitYaw = 0f;
            if (Mathf.Abs(_orbitPitch) < 0.15f)
                _orbitPitch = 0f;
        }

        Vector3 CharacterPivot()
        {
            return _target.position;
        }

        Vector3 DesiredPosition()
        {
            var pivoted = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f) * offset;
            return _target.position + _target.rotation * pivoted;
        }

        Quaternion DesiredRotation()
        {
            var lookAt = _target.position + Vector3.up * lookHeight;
            return Quaternion.LookRotation(lookAt - transform.position, Vector3.up);
        }

        Vector3 TrimForCollision(Vector3 pivot, Vector3 desired)
        {
            var lookPivot = pivot + Vector3.up * lookHeight;
            var dir = desired - lookPivot;
            float dist = dir.magnitude;
            if (dist < 0.01f) return desired;
            if (Physics.SphereCast(lookPivot, collisionRadius, dir.normalized, out var hit, dist,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                return lookPivot + dir.normalized * Mathf.Max(0.45f, hit.distance - 0.12f);
            }

            return desired;
        }

        Vector2 ReadLookStick()
        {
            if (Input.touchCount > 0)
            {
                // Continue / end claimed finger first.
                if (_lookFingerId >= 0)
                {
                    for (int i = 0; i < Input.touchCount; i++)
                    {
                        var t = Input.GetTouch(i);
                        if (t.fingerId != _lookFingerId) continue;

                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            _lookActive = false;
                            _lookDragging = false;
                            _lookFingerId = -1;
                            return Vector2.zero;
                        }

                        if (IsLookBlockedByUi(t.position))
                        {
                            _lookActive = false;
                            _lookDragging = false;
                            _lookFingerId = -1;
                            return Vector2.zero;
                        }

                        _lookPointerCurrent = t.position;
                        var delta = _lookPointerCurrent - LookStickBase();
                        if (!_lookDragging && delta.sqrMagnitude >= mouseDragThresholdPx * mouseDragThresholdPx)
                            _lookDragging = true;
                        return _lookDragging ? StickFromDelta(delta) : Vector2.zero;
                    }

                    // Claimed finger disappeared.
                    _lookActive = false;
                    _lookDragging = false;
                    _lookFingerId = -1;
                }

                // Claim a new finger that begins inside the look pad.
                for (int i = 0; i < Input.touchCount; i++)
                {
                    var t = Input.GetTouch(i);
                    if (t.phase != TouchPhase.Began) continue;
                    if (IsLookBlockedByUi(t.position) || !IsInsideLookPad(t.position))
                        continue;

                    _lookActive = true;
                    _lookDragging = false;
                    _lookFingerId = t.fingerId;
                    _lookPointerCurrent = t.position;
                    return Vector2.zero;
                }

                return Vector2.zero;
            }

            // Mouse / editor: right-pad press.
            _lookFingerId = -1;
            Vector2 mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                if (!IsLookBlockedByUi(mouse) && IsInsideLookPad(mouse))
                {
                    _lookActive = true;
                    _lookDragging = false;
                    _lookPointerCurrent = mouse;
                }
            }

            if (Input.GetMouseButtonUp(0) && _lookActive)
            {
                _lookActive = false;
                _lookDragging = false;
            }

            if (!_lookActive || !Input.GetMouseButton(0))
                return Vector2.zero;

            if (IsLookBlockedByUi(mouse))
            {
                _lookActive = false;
                _lookDragging = false;
                return Vector2.zero;
            }

            _lookPointerCurrent = mouse;
            var mouseDelta = _lookPointerCurrent - LookStickBase();
            if (!_lookDragging && mouseDelta.sqrMagnitude >= mouseDragThresholdPx * mouseDragThresholdPx)
                _lookDragging = true;
            return _lookDragging ? StickFromDelta(mouseDelta) : Vector2.zero;
        }

        static Vector2 StickFromDelta(Vector2 delta)
        {
            float r = StickRadiusPx;
            if (r < 1f) return Vector2.zero;
            return Vector2.ClampMagnitude(delta / r, 1f);
        }

        static bool IsLookBlockedByUi(Vector2 screen)
        {
            // Top-right floating Bugsee HUD (leave bottom-right free for the look stick).
            if (screen.x > Screen.width - 180f && screen.y > Screen.height * 0.45f)
                return true;

            var panel = BugseeHillStation.ActionPanelScreenRect;
            if (panel.width > 1f && panel.height > 1f)
            {
                panel.xMin -= 12f;
                panel.xMax += 12f;
                panel.yMin -= 12f;
                panel.yMax += 12f;
                if (panel.Contains(screen))
                    return true;
            }

            return false;
        }

        void OnGUI()
        {
            DrawLookStick();
        }

        void DrawLookStick()
        {
            float radius = StickRadiusPx;
            Vector2 basePos = LookStickBase();
            Vector2 knobPos = basePos;
            if (_lookActive)
                knobPos = basePos + Vector2.ClampMagnitude(_lookPointerCurrent - basePos, radius);

            float baseGuiX = basePos.x;
            float baseGuiY = Screen.height - basePos.y;
            float knobGuiX = knobPos.x;
            float knobGuiY = Screen.height - knobPos.y;

            float baseSize = radius * 2f;
            float knobSize = radius * 0.9f;
            var circle = CircleTexture();
            var prev = GUI.color;

            GUI.color = new Color(1f, 1f, 1f, _lookActive ? 0.4f : 0.22f);
            GUI.DrawTexture(
                new Rect(baseGuiX - baseSize * 0.5f, baseGuiY - baseSize * 0.5f, baseSize, baseSize),
                circle,
                ScaleMode.StretchToFill,
                true);

            float well = baseSize * 0.72f;
            GUI.color = new Color(1f, 1f, 1f, _lookActive ? 0.2f : 0.12f);
            GUI.DrawTexture(
                new Rect(baseGuiX - well * 0.5f, baseGuiY - well * 0.5f, well, well),
                circle,
                ScaleMode.StretchToFill,
                true);

            // Cool accent so left (coral / move) and right (look) read as different controls.
            GUI.color = new Color(0.45f, 0.7f, 0.95f, _lookActive ? 0.9f : 0.45f);
            GUI.DrawTexture(
                new Rect(knobGuiX - knobSize * 0.5f, knobGuiY - knobSize * 0.5f, knobSize, knobSize),
                circle,
                ScaleMode.StretchToFill,
                true);

            if (!_lookActive)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                GUI.Label(new Rect(baseGuiX - 54f, baseGuiY + baseSize * 0.55f, 108f, 22f), "Look");
            }

            GUI.color = prev;
        }

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
                    _circleTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }

            _circleTex.Apply(false, true);
            return _circleTex;
        }
    }
}
