using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Runtime procedural textures/materials (Built-in RP, mobile-safe).</summary>
    public static class ProceduralMaterials
    {
        public static readonly Color AnteaterRed = new Color(0.91f, 0.35f, 0.35f, 1f);
        public static readonly Color GrassA = new Color(0.28f, 0.52f, 0.26f);
        public static readonly Color GrassB = new Color(0.22f, 0.42f, 0.20f);
        public static readonly Color StoneA = new Color(0.45f, 0.42f, 0.38f);
        public static readonly Color StoneB = new Color(0.32f, 0.30f, 0.28f);
        public static readonly Color Bark = new Color(0.36f, 0.24f, 0.14f);
        public static readonly Color Canopy = new Color(0.18f, 0.48f, 0.22f);
        public static readonly Color Path = new Color(0.48f, 0.40f, 0.28f);

        static Shader _litShader;
        static Shader _unlitShader;
        static bool _logged;

        public static Material CreateLit(Color color, Texture2D tex = null)
        {
            return CreateMaterial(ResolveLitShader(), color, tex);
        }

        public static Material CreateUnlit(Color color, Texture2D tex = null)
        {
            return CreateMaterial(ResolveUnlitShader(), color, tex);
        }

        static Material CreateMaterial(Shader shader, Color color, Texture2D tex)
        {
            var mat = new Material(shader);
            mat.SetColor("_Color", color);
            mat.color = color;
            if (tex != null)
                mat.mainTexture = tex;
            return mat;
        }

        static Shader ResolveLitShader()
        {
            if (_litShader != null) return _litShader;

            _litShader = Resources.Load<Shader>("Shaders/BugseeLitColored");
            if (_litShader == null)
                _litShader = Shader.Find("Bugsee/LitColored");
            if (_litShader == null)
                _litShader = ResolveUnlitShader();

            LogOnce(_litShader);
            if (_litShader == null)
                throw new System.InvalidOperationException("[MiniGame] No lit shader found.");
            return _litShader;
        }

        static Shader ResolveUnlitShader()
        {
            if (_unlitShader != null) return _unlitShader;

            _unlitShader = Resources.Load<Shader>("Shaders/BugseeUnlitColored");
            if (_unlitShader == null)
                _unlitShader = Shader.Find("Bugsee/UnlitColored");
            if (_unlitShader == null)
                _unlitShader = Shader.Find("Unlit/Color");
            if (_unlitShader == null)
                _unlitShader = Shader.Find("Sprites/Default");

            return _unlitShader;
        }

        static void LogOnce(Shader shader)
        {
            if (_logged) return;
            _logged = true;
            Debug.Log("[MiniGame] Material shader: " + (shader != null ? shader.name : "(null)"));
        }

        public static Texture2D NoiseTexture(int size, Color a, Color b, float scale, int seed)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            var rng = new System.Random(seed);
            float ox = (float)rng.NextDouble() * 100f;
            float oy = (float)rng.NextDouble() * 100f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float n = Mathf.PerlinNoise(ox + x * scale, oy + y * scale);
                    float n2 = Mathf.PerlinNoise(ox + x * scale * 2.7f, oy + y * scale * 2.7f);
                    float t = Mathf.Clamp01(n * 0.7f + n2 * 0.3f);
                    tex.SetPixel(x, y, Color.Lerp(a, b, t));
                }
            }

            tex.Apply(false, false);
            return tex;
        }

        public static Texture2D CheckerTexture(int size, Color a, Color b, int cells)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            int cell = Mathf.Max(1, size / cells);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool on = ((x / cell) + (y / cell)) % 2 == 0;
                    tex.SetPixel(x, y, on ? a : b);
                }
            }

            tex.Apply(false, false);
            return tex;
        }
    }
}
