using UnityEngine;

namespace EngineSim
{
    // A Windows 95 look for IMGUI (the Chicago95 theme): grey 3D-bevelled frames, a navy title bar, sunken tracks
    // and fields, raised buttons that press in, black text. Everything is drawn from small generated textures
    // (nine-sliced by their borders), so nothing is loaded.
    internal static class Win95
    {
        public static readonly Color Face = Rgb(192, 192, 192), Light = Rgb(223, 223, 223), White = Color.white,
                                     Shadow = Rgb(128, 128, 128), Dark = Color.black, Title = Rgb(0, 0, 128), TitleEnd = Rgb(16, 132, 208);

        private static GUISkin _skin;
        private static Texture2D _titleBar;

        private static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        public static Texture2D TitleBar => _titleBar != null ? _titleBar : (_titleBar = Gradient(Title, TitleEnd));

        public static GUISkin Skin
        {
            get
            {
                if (_skin != null) return _skin;
                _skin = ScriptableObject.CreateInstance<GUISkin>();
                var font = Font.CreateDynamicFontFromOSFont(new[] { "MS Sans Serif", "Microsoft Sans Serif", "Tahoma", "Arial" }, 11);
                _skin.font = font;
                var raised = Bevel(White, Light, Shadow, Dark);
                var sunken = Bevel(Shadow, Dark, Light, White);
                var pressed = Bevel(Dark, Shadow, Light, White);

                _skin.window = Style(raised, 3, Dark);
                _skin.window.padding = new RectOffset(6, 6, 24, 6);
                _skin.window.onNormal.background = raised;
                _skin.box = Style(sunken, 2, Dark);
                _skin.label = new GUIStyle { normal = { textColor = Dark }, font = font, fontSize = 11, padding = new RectOffset(2, 2, 1, 1), wordWrap = false };
                _skin.button = Style(raised, 3, Dark);
                _skin.button.alignment = TextAnchor.MiddleCenter;
                _skin.button.padding = new RectOffset(8, 8, 4, 4);
                _skin.button.margin = new RectOffset(4, 4, 4, 4);
                _skin.button.active.background = pressed;
                _skin.button.active.textColor = Dark;
                _skin.button.hover = _skin.button.normal;
                _skin.button.contentOffset = Vector2.zero;
                _skin.horizontalSlider = Style(Bevel(Shadow, Dark, Light, White, Face), 2, Dark);
                _skin.horizontalSlider.fixedHeight = 6;
                _skin.horizontalSlider.margin = new RectOffset(6, 6, 6, 6);
                _skin.horizontalSlider.padding = new RectOffset(0, 0, 0, 0);
                _skin.horizontalSliderThumb = Style(raised, 2, Dark);
                _skin.horizontalSliderThumb.fixedWidth = 11;
                _skin.horizontalSliderThumb.fixedHeight = 20;
                _skin.horizontalSliderThumb.margin = new RectOffset(0, 0, -7, 0);
                _skin.horizontalSliderThumb.active.background = raised;
                _skin.horizontalSliderThumb.hover.background = raised;
                foreach (var st in new[] { _skin.window, _skin.box, _skin.button, _skin.horizontalSlider, _skin.horizontalSliderThumb }) { st.font = font; st.fontSize = 11; }
                return _skin;
            }
        }

        private static GUIStyle Style(Texture2D bg, int border, Color text)
        {
            return new GUIStyle
            {
                normal = { background = bg, textColor = text },
                border = new RectOffset(border, border, border, border),
                padding = new RectOffset(4, 4, 4, 4),
            };
        }

        // a 3D bevel two pixels deep: outer and inner edges on the top and left, then on the bottom and right
        private static Texture2D Bevel(Color outerTL, Color innerTL, Color innerBR, Color outerBR, Color? fill = null)
        {
            const int n = 8;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int top = n - 1 - y;   // texture rows run bottom up
                    Color c = fill ?? Face;
                    if (x == 0 || top == 0) c = outerTL;
                    else if (x == n - 1 || top == n - 1) c = outerBR;
                    else if (x == 1 || top == 1) c = innerTL;
                    else if (x == n - 2 || top == n - 2) c = innerBR;
                    if ((x == 0 && top == n - 1) || (x == n - 1 && top == 0)) c = outerBR;   // the corners as Windows draws them
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        private static Texture2D Gradient(Color from, Color to)
        {
            const int w = 64;
            var t = new Texture2D(w, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int x = 0; x < w; x++) t.SetPixel(x, 0, Color.Lerp(from, to, x / (w - 1f)));
            t.Apply();
            return t;
        }

        // the title bar across the top of a window, and its close box; true when the close box is clicked
        public static bool TitleBarWithClose(Rect window, string title)
        {
            var bar = new Rect(3, 3, window.width - 6, 18);
            GUI.DrawTexture(bar, TitleBar);
            var style = new GUIStyle(Skin.label) { fontStyle = FontStyle.Bold, normal = { textColor = White }, alignment = TextAnchor.MiddleLeft };
            GUI.Label(new Rect(bar.x + 4, bar.y, bar.width - 30, bar.height), title, style);
            var close = new GUIStyle(Skin.button) { padding = new RectOffset(0, 0, 0, 1), margin = new RectOffset(), fontStyle = FontStyle.Bold };
            return GUI.Button(new Rect(bar.xMax - 18, bar.y + 2, 16, 14), "×", close);
        }
    }
}
