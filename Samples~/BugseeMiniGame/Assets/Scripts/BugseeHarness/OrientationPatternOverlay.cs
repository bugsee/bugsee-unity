using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Paints the top half of the screen red and the bottom half blue, to check which readback row of the frame pusher is the screen's top.</summary>
    public sealed class OrientationPatternOverlay : MonoBehaviour
    {
        Texture2D _red, _blue;

        void Awake()
        {
            _red = Solid(Color.red);
            _blue = Solid(Color.blue);
        }

        static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        void OnGUI()
        {
            GUI.depth = -1000;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height / 2f), _red);
            GUI.DrawTexture(new Rect(0, Screen.height / 2f, Screen.width, Screen.height / 2f), _blue);
        }
    }
}
