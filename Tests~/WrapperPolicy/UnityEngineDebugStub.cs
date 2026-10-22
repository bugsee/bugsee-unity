namespace UnityEngine
{
    /// <summary>Host-side stand-in for <c>UnityEngine.Debug.LogWarning</c>, so platform files that only log can compile in this project.</summary>
    static class Debug
    {
        public static void LogWarning(object message) { }
    }
}
