using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Platform budgets for the MiniGame sample. Desktop keeps the dense look;
    /// mobile cuts draw calls / shadow cost so devices stay playable.
    /// </summary>
    public static class SampleQuality
    {
        public static bool IsMobile
        {
            get
            {
#if UNITY_ANDROID || UNITY_IOS
                return !Application.isEditor;
#else
                return Application.isMobilePlatform && !Application.isEditor;
#endif
            }
        }

        public static float GrassStep => IsMobile ? 2.8f : 1.55f;
        public static int GrassBladesMin => IsMobile ? 2 : 4;
        public static int GrassBladesExtra => IsMobile ? 2 : 4;
        public static float GrassChunkSize => IsMobile ? 24f : 16f;

        public static int TreeFieldWant => IsMobile ? 55 : 110;
        public static int TreeOuterRing => IsMobile ? 28 : 72;
        public static int CloudCount => IsMobile ? 10 : 20;
        public static int DriftParticleCount => IsMobile ? 18 : 48;

        public static void ApplyRuntimeSettings()
        {
            if (IsMobile)
            {
                QualitySettings.shadows = ShadowQuality.HardOnly;
                QualitySettings.shadowResolution = ShadowResolution.Medium;
                QualitySettings.shadowDistance = 14f;
                QualitySettings.shadowCascades = 2;
                QualitySettings.antiAliasing = 0;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
                // Soft particles / realtime reflections aren't used, but keep pixel lights low.
                QualitySettings.pixelLightCount = 1;
            }
            else
            {
                QualitySettings.shadows = ShadowQuality.All;
                QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
                QualitySettings.shadowDistance = 28f;
                QualitySettings.shadowCascades = 4;
                QualitySettings.shadowCascade4Split = new Vector3(0.02f, 0.08f, 0.22f);
                QualitySettings.shadowProjection = ShadowProjection.StableFit;
                QualitySettings.shadowNearPlaneOffset = 3f;
            }
        }
    }
}
