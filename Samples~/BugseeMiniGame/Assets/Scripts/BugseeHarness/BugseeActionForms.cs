using System;
using Bugsee.Contracts.Appearance;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Modal IMGUI forms for harness actions that need user input
    /// (secure rect, attributes, user id, appearance colors).
    /// </summary>
    public sealed class BugseeActionForms : MonoBehaviour
    {
        public static BugseeActionForms Instance { get; private set; }

        public static bool IsOpen => Instance != null && Instance._kind != FormKind.None;

        /// <summary>True while a modal form should eat pointer / keyboard game input.</summary>
        public static bool IsBlockingInput => IsOpen;

        enum FormKind
        {
            None,
            SecureRect,
            Attribute,
            UserIdentifier,
            Appearance
        }

        static readonly string[] ReportColorKeys =
        {
            ReportAppearance.ActionBarColor,
            ReportAppearance.EditTextBackgroundColor,
            ReportAppearance.VersionColor,
            ReportAppearance.TextColor,
            ReportAppearance.HintColor,
            ReportAppearance.ActionBarTextColor,
            ReportAppearance.ActionBarButtonBackgroundClickedColor,
            ReportAppearance.BackgroundColor,
            ReportAppearance.SeverityLabelActiveColor
        };

        static readonly string[] FeedbackColorKeys =
        {
            FeedbackAppearance.ActionBarColor,
            FeedbackAppearance.BackgroundColor,
            FeedbackAppearance.ActionBarButtonBackgroundClickedColor,
            FeedbackAppearance.IncomingBubbleColor,
            FeedbackAppearance.OutgoingBubbleColor,
            FeedbackAppearance.IncomingTextColor,
            FeedbackAppearance.OutgoingTextColor,
            FeedbackAppearance.DateTextColor,
            FeedbackAppearance.TitleTextColor,
            FeedbackAppearance.EmailSkipTextColor,
            FeedbackAppearance.EmailSkipBackgroundClickedColor,
            FeedbackAppearance.EmailBackgroundColor,
            FeedbackAppearance.EmailContinueNotActiveColor,
            FeedbackAppearance.EmailContinueActiveColor,
            FeedbackAppearance.EmailContinueClickedColor,
            FeedbackAppearance.InputTextColor,
            FeedbackAppearance.InputTextHintColor,
            FeedbackAppearance.BottomDelimiterColor,
            FeedbackAppearance.LoadingBarBackgroundColor,
            FeedbackAppearance.LoadingTextColor,
            FeedbackAppearance.ErrorTextColor,
            FeedbackAppearance.VersionChangedBackgroundColor,
            FeedbackAppearance.VersionChangedTextColor
        };

        FormKind _kind;
        BugseeSampleBootstrap _bootstrap;
        Vector2 _scroll;
        string _error;

        // Secure rect (LTRB pixels).
        string _left = "40";
        string _top = "40";
        string _right = "260";
        string _bottom = "160";

        // Attribute / identity.
        string _attrKey = "sample.key";
        string _attrValue = "value";
        string _userId = "anteater-explorer";

        // Appearance editor.
        string _expandedColorKey;
        Color _pickerColor = Color.white;

        public static BugseeActionForms Ensure(BugseeSampleBootstrap bootstrap = null)
        {
            if (Instance != null)
            {
                if (bootstrap != null)
                    Instance._bootstrap = bootstrap;
                return Instance;
            }

            var go = GameObject.Find("UI");
            if (go == null)
                go = new GameObject("UI");
            var forms = go.GetComponent<BugseeActionForms>();
            if (forms == null)
                forms = go.AddComponent<BugseeActionForms>();
            forms._bootstrap = bootstrap;
            return forms;
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        void Update()
        {
            if (_kind == FormKind.None) return;
            if (Input.GetKeyDown(KeyCode.Escape))
                Close();
        }

        public void ShowSecureRect(RectInt? current = null)
        {
            if (current.HasValue)
            {
                var r = current.Value;
                _left = r.xMin.ToString();
                _top = r.yMin.ToString();
                _right = r.xMax.ToString();
                _bottom = r.yMax.ToString();
            }

            Open(FormKind.SecureRect);
        }

        public void ShowAttribute(string key = null, string value = null)
        {
            if (!string.IsNullOrEmpty(key))
                _attrKey = key;
            if (value != null)
                _attrValue = value;
            Open(FormKind.Attribute);
        }

        public void ShowUserIdentifier(string current = null)
        {
            if (current != null)
                _userId = current;
            else
            {
                try
                {
                    var id = Bugsee.GetUserIdentifier();
                    if (!string.IsNullOrEmpty(id))
                        _userId = id;
                }
                catch
                {
                    // Editor stub may throw or return null — keep draft.
                }
            }

            Open(FormKind.UserIdentifier);
        }

        public void ShowAppearance()
        {
            _expandedColorKey = null;
            Open(FormKind.Appearance);
        }

        public void Close()
        {
            _kind = FormKind.None;
            _error = null;
            _expandedColorKey = null;
        }

        void Open(FormKind kind)
        {
            _kind = kind;
            _error = null;
            _scroll = Vector2.zero;
        }

        void OnGUI()
        {
            if (_kind == FormKind.None) return;

            // Draw above other sample IMGUI (hill panels, HUD, debug).
            GUI.depth = -50;

            // Dim full screen.
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;

            // Clicking the dimmer does nothing (must Cancel / Esc) so accidental taps don't close.

            switch (_kind)
            {
                case FormKind.SecureRect:
                    DrawSecureRectForm();
                    break;
                case FormKind.Attribute:
                    DrawAttributeForm();
                    break;
                case FormKind.UserIdentifier:
                    DrawUserIdentifierForm();
                    break;
                case FormKind.Appearance:
                    DrawAppearanceForm();
                    break;
            }
        }

        void DrawSecureRectForm()
        {
            BeginModal(360f, 280f, "Set Secure Rectangle");
            GUILayout.Label("Pixel LTRB (non-negative; right > left, bottom > top)");
            RowField("Left", ref _left);
            RowField("Top", ref _top);
            RowField("Right", ref _right);
            RowField("Bottom", ref _bottom);
            DrawError();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Random", GUILayout.Height(32f)))
                RandomizeSecureRect();
            if (GUILayout.Button("Apply", GUILayout.Height(32f)))
                ApplySecureRect();
            if (GUILayout.Button("Cancel", GUILayout.Height(32f)))
                Close();
            GUILayout.EndHorizontal();
            EndModal();
        }

        void DrawAttributeForm()
        {
            BeginModal(360f, 220f, "Set Attribute");
            RowField("Key", ref _attrKey);
            RowField("Value", ref _attrValue);
            DrawError();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", GUILayout.Height(32f)))
                ApplyAttribute();
            if (GUILayout.Button("Cancel", GUILayout.Height(32f)))
                Close();
            GUILayout.EndHorizontal();
            EndModal();
        }

        void DrawUserIdentifierForm()
        {
            BeginModal(360f, 200f, "Set User Identifier");
            RowField("User ID", ref _userId);
            DrawError();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", GUILayout.Height(32f)))
                ApplyUserIdentifier();
            if (GUILayout.Button("Cancel", GUILayout.Height(32f)))
                Close();
            GUILayout.EndHorizontal();
            EndModal();
        }

        void DrawAppearanceForm()
        {
            float w = Mathf.Min(520f, Screen.width - 24f);
            float h = Mathf.Min(Screen.height - 40f, 640f);
            BeginModal(w, h, "Appearance Colors");

            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Report", BoldLabel());
            DrawColorGroup(ReportColorKeys);

            GUILayout.Space(10f);
            GUILayout.Label("Feedback", BoldLabel());
            DrawColorGroup(FeedbackColorKeys);

            GUILayout.EndScrollView();
            DrawError();

            if (GUILayout.Button("Close", GUILayout.Height(32f)))
                Close();
            EndModal();
        }

        void DrawColorGroup(string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var shortName = ShortColorName(key);
                Color32? current = null;
                try
                {
                    current = Bugsee.Appearance.GetColor(key);
                }
                catch
                {
                    current = null;
                }

                var display = current ?? new Color32(180, 180, 180, 255);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(shortName, GUILayout.MinWidth(220f));

                var swatchRect = GUILayoutUtility.GetRect(44f, 22f, GUILayout.Width(44f), GUILayout.Height(22f));
                DrawSwatch(swatchRect, display);
                if (GUI.Button(swatchRect, GUIContent.none, GUIStyle.none))
                    ToggleColorPicker(key, display);

                if (GUILayout.Button(current.HasValue ? "Edit" : "Pick", GUILayout.Width(52f), GUILayout.Height(22f)))
                    ToggleColorPicker(key, display);

                if (current.HasValue && GUILayout.Button("Clear", GUILayout.Width(52f), GUILayout.Height(22f)))
                {
                    try
                    {
                        Bugsee.Appearance.SetColor(key, null);
                        Status("Appearance cleared " + shortName);
                        if (_expandedColorKey == key)
                            _expandedColorKey = null;
                    }
                    catch (Exception ex)
                    {
                        _error = ex.Message;
                    }
                }

                GUILayout.EndHorizontal();

                if (_expandedColorKey == key)
                    DrawInlineColorPicker(key, shortName);

                GUILayout.EndVertical();
            }
        }

        void ToggleColorPicker(string key, Color32 seed)
        {
            if (_expandedColorKey == key)
            {
                _expandedColorKey = null;
                return;
            }

            _expandedColorKey = key;
            _pickerColor = seed;
            _error = null;
        }

        void DrawInlineColorPicker(string key, string shortName)
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            var preview = GUILayoutUtility.GetRect(56f, 56f, GUILayout.Width(56f), GUILayout.Height(56f));
            DrawSwatch(preview, _pickerColor);
            GUILayout.BeginVertical();
            _pickerColor.r = GUILayout.HorizontalSlider(_pickerColor.r, 0f, 1f);
            GUILayout.Label(string.Format("R {0}", Mathf.RoundToInt(_pickerColor.r * 255f)));
            _pickerColor.g = GUILayout.HorizontalSlider(_pickerColor.g, 0f, 1f);
            GUILayout.Label(string.Format("G {0}", Mathf.RoundToInt(_pickerColor.g * 255f)));
            _pickerColor.b = GUILayout.HorizontalSlider(_pickerColor.b, 0f, 1f);
            GUILayout.Label(string.Format("B {0}", Mathf.RoundToInt(_pickerColor.b * 255f)));
            _pickerColor.a = GUILayout.HorizontalSlider(_pickerColor.a, 0f, 1f);
            GUILayout.Label(string.Format("A {0}", Mathf.RoundToInt(_pickerColor.a * 255f)));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply color", GUILayout.Height(28f)))
            {
                try
                {
                    var c32 = (Color32)_pickerColor;
                    Bugsee.Appearance.SetColor(key, c32);
                    Status(string.Format("Appearance {0}=#{1:X2}{2:X2}{3:X2}{4:X2}",
                        shortName, c32.r, c32.g, c32.b, c32.a));
                    _expandedColorKey = null;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                }
            }

            if (GUILayout.Button("Random", GUILayout.Height(28f)))
            {
                _pickerColor = new Color(
                    UnityEngine.Random.value,
                    UnityEngine.Random.value,
                    UnityEngine.Random.value,
                    1f);
            }

            if (GUILayout.Button("Cancel", GUILayout.Height(28f)))
                _expandedColorKey = null;
            GUILayout.EndHorizontal();
        }

        void RandomizeSecureRect()
        {
            int maxW = Mathf.Max(64, Screen.width);
            int maxH = Mathf.Max(64, Screen.height);
            int left = UnityEngine.Random.Range(0, maxW / 2);
            int top = UnityEngine.Random.Range(0, maxH / 2);
            int right = UnityEngine.Random.Range(left + 16, maxW + 1);
            int bottom = UnityEngine.Random.Range(top + 16, maxH + 1);
            _left = left.ToString();
            _top = top.ToString();
            _right = right.ToString();
            _bottom = bottom.ToString();
            _error = null;
        }

        void ApplySecureRect()
        {
            if (!TryParseSecureRect(out var rect, out var err))
            {
                _error = err;
                return;
            }

            try
            {
                Bugsee.AddSecureRectangle(rect);
                BugseeActionCatalog.Instance?.RememberSecureRect(rect, active: true);
                Status(string.Format("AddSecureRectangle LTRB=({0},{1},{2},{3})",
                    rect.xMin, rect.yMin, rect.xMax, rect.yMax));
                Close();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        }

        void ApplyAttribute()
        {
            if (string.IsNullOrWhiteSpace(_attrKey))
            {
                _error = "Key is required";
                return;
            }

            try
            {
                Bugsee.SetAttribute(_attrKey.Trim(), _attrValue ?? "");
                Status("SetAttribute " + _attrKey.Trim() + "=" + _attrValue);
                Close();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        }

        void ApplyUserIdentifier()
        {
            if (string.IsNullOrWhiteSpace(_userId))
            {
                _error = "User identifier is required";
                return;
            }

            try
            {
                Bugsee.SetUserIdentifier(_userId.Trim());
                Status("SetUserIdentifier " + _userId.Trim());
                Close();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        }

        bool TryParseSecureRect(out RectInt rect, out string error)
        {
            rect = default;
            error = null;
            if (!int.TryParse(_left.Trim(), out int left) ||
                !int.TryParse(_top.Trim(), out int top) ||
                !int.TryParse(_right.Trim(), out int right) ||
                !int.TryParse(_bottom.Trim(), out int bottom))
            {
                error = "All values must be integers";
                return false;
            }

            if (left < 0 || top < 0 || right < 0 || bottom < 0)
            {
                error = "Values must be non-negative";
                return false;
            }

            // RB must be greater than LT (right > left, bottom > top).
            if (right <= left || bottom <= top)
            {
                error = "Right/Bottom must be greater than Left/Top";
                return false;
            }

            rect = new RectInt(left, top, right - left, bottom - top);
            return true;
        }

        void BeginModal(float width, float height, string title)
        {
            float x = (Screen.width - width) * 0.5f;
            float y = (Screen.height - height) * 0.5f;
            GUI.Box(new Rect(x, y, width, height), title);
            GUILayout.BeginArea(new Rect(x + 12f, y + 28f, width - 24f, height - 40f));
        }

        static void EndModal()
        {
            GUILayout.EndArea();
        }

        static void RowField(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(72f));
            value = GUILayout.TextField(value ?? "");
            GUILayout.EndHorizontal();
        }

        void DrawError()
        {
            if (string.IsNullOrEmpty(_error)) return;
            var prev = GUI.color;
            GUI.color = new Color(1f, 0.45f, 0.4f);
            GUILayout.Label(_error);
            GUI.color = prev;
        }

        static void DrawSwatch(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
            DrawRectBorder(rect, new Color(0.1f, 0.1f, 0.1f, 0.85f));
        }

        static void DrawRectBorder(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        static string ShortColorName(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int sep = key.IndexOf("::", StringComparison.Ordinal);
            return sep >= 0 && sep + 2 < key.Length ? key.Substring(sep + 2) : key;
        }

        static GUIStyle BoldLabel()
        {
            return new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        }

        void Status(string message)
        {
            if (_bootstrap != null)
                _bootstrap.SetStatus(message);
            else
                Debug.Log("[BugseeSample] " + message);
        }
    }
}
