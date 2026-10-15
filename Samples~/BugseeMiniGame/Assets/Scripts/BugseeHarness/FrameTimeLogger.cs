using System;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Logs the Unity frame rate and frame-time percentiles every 10 s, to compare runs with the experimental frame pusher on and off.</summary>
    public sealed class FrameTimeLogger : MonoBehaviour
    {
        const float WindowSeconds = 10f;
        readonly float[] _frameMs = new float[4096];
        int _count;
        float _elapsed;

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (_count < _frameMs.Length) _frameMs[_count++] = dt * 1000f;
            _elapsed += dt;
            if (_elapsed < WindowSeconds) return;

            Array.Sort(_frameMs, 0, _count);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Debug.Log("[BugseeSample] frames fps=" + (_count / _elapsed).ToString("F1", inv)
                + " p50ms=" + _frameMs[_count / 2].ToString("F1", inv)
                + " p95ms=" + _frameMs[(int)(_count * 0.95f)].ToString("F1", inv)
                + " p99ms=" + _frameMs[Math.Min(_count - 1, (int)(_count * 0.99f))].ToString("F1", inv)
                + " maxms=" + _frameMs[_count - 1].ToString("F1", inv));
            _count = 0;
            _elapsed = 0f;
        }
    }
}
