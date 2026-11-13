using UnityEngine;

namespace HS2CameraSweep.UI
{
    /// <summary>
    /// Draws a small camera glyph in code so the plugin ships as a single DLL with no image files.
    /// Swap in a real 32x32 PNG later (embedded resource + Texture2D.LoadImage) if you want.
    /// </summary>
    internal static class ToolbarIcon
    {
        public static Texture2D Create()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var body = new Color(0.92f, 0.92f, 0.92f, 1f);
            var lens = new Color(0.25f, 0.55f, 0.95f, 1f);

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var c = clear;

                // Camera body
                if (x >= 4 && x <= 27 && y >= 7 && y <= 22) c = body;
                // Viewfinder bump
                if (x >= 9 && x <= 15 && y >= 23 && y <= 25) c = body;

                // Lens ring
                var dx = x - 16f;
                var dy = y - 14.5f;
                var d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d <= 6f) c = lens;
                if (d <= 3.5f) c = body;

                // Three small "repeat" ticks to the right = multi-shot
                if (y == 26 && (x == 20 || x == 23 || x == 26)) c = lens;
                if (y == 27 && (x == 20 || x == 23 || x == 26)) c = lens;

                tex.SetPixel(x, y, c);
            }

            tex.Apply();
            return tex;
        }
    }
}
