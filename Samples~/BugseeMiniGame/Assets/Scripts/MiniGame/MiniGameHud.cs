using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Onboarding strip + score / FPS HUD for the open-field sample.</summary>
    public sealed class MiniGameHud : MonoBehaviour
    {
        const float OnboardingSeconds = 8f;
        const float FpsSmooth = 0.1f;

        BugseeSampleBootstrap _bootstrap;
        float _startTime;
        bool _dismissed;
        float _fps = 60f;

        public void Init(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            _startTime = Time.unscaledTime;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;
            float instant = 1f / dt;
            _fps = Mathf.Lerp(_fps, instant, FpsSmooth);
        }

        void OnGUI()
        {
            var score = _bootstrap != null ? _bootstrap.Score : 0;
            GUI.Box(new Rect(12, 12, 300, 66),
                "Score: " + score +
                "\nFPS: " + Mathf.RoundToInt(_fps) +
                "\nClimb a category hill · tap an action");

            if (ShouldShowOnboarding())
            {
                float w = Mathf.Min(560f, Screen.width - 40f);
                var rect = new Rect((Screen.width - w) * 0.5f, 88f, w, 56f);
                GUI.Box(rect,
                    "WASD or left stick to move\nEach hill is a category — tap bubbles for related APIs");
            }
        }

        bool ShouldShowOnboarding()
        {
            if (_dismissed) return false;
            var anteater = AnteaterController.Instance;
            if (anteater != null && anteater.HasMoved)
            {
                _dismissed = true;
                return false;
            }

            if (Time.unscaledTime - _startTime > OnboardingSeconds)
            {
                _dismissed = true;
                return false;
            }

            return true;
        }
    }
}
