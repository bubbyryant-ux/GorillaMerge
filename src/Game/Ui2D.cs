using System.Collections.Generic;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>Minecraft's pixel font (font/ascii.png from your jar), with the 1-pixel drop shadow.</summary>
    public static class McFont
    {
        public static Texture2D Tex;
        private static readonly int[] Widths = new int[256];

        public static bool Ready => Tex != null;

        public static void Init()
        {
            Tex = GameAssets.LoadTexture("mc:font/ascii", null, false);
            if (Tex == null) return;
            Tex.filterMode = FilterMode.Point;
            var px = Tex.GetPixels32();
            int w = Tex.width, cell = w / 16;
            for (int c = 0; c < 256; c++)
            {
                int cx = (c % 16) * cell, cyTop = (c / 16) * cell, last = -1;
                for (int x = 0; x < cell; x++)
                    for (int y = 0; y < cell; y++)
                        if (px[(Tex.height - 1 - (cyTop + y)) * w + cx + x].a > 0) { last = x; break; }
                Widths[c] = c == ' ' ? 3 : last + 1;
            }
        }

        public static int Advance(char c) => (c < 256 ? Widths[c] : 5) + 1;

        public static int Width(string s)
        {
            int w = 0;
            foreach (var ch in s) w += Advance(ch);
            return w > 0 ? w - 1 : 0;
        }
    }

    /// <summary>Loads Minecraft GUI pictures once.</summary>
    public static class GuiTex
    {
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string path)
        {
            if (Cache.TryGetValue(path, out var t)) return t;
            t = GameAssets.LoadTexture("mc:" + path, null, false);
            if (t != null) t.filterMode = FilterMode.Point;
            Cache[path] = t;
            return t;
        }
    }

    /// <summary>
    /// Draws flat 2D things (Minecraft GUI pictures, block and item icons, text) in pixel units on a panel.
    /// Each picture gets its own mesh, all drawn on top of the world so they never vanish into walls.
    /// Coordinates are Minecraft GUI pixels: x to the right, y downward from the panel's top-left.
    ///
    /// Stacking follows drawing order every frame: a picture first drawn later this frame goes on top of the
    /// ones drawn before it, and a higher Band (Front, tooltips, the held stack) goes on top of every lower band.
    /// </summary>
    public class Canvas2D
    {
        private class Layer
        {
            public GameObject Go;
            public Mesh Mesh;
            public Material Mat;
            public int UsedFrame = -1;
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> U = new List<Vector2>();
            public readonly List<Color32> C = new List<Color32>();
            public readonly List<int> T = new List<int>();
        }

        private const int Bands = 8, PerBand = 16;

        public readonly GameObject Root;
        public float Px;
        /// <summary>What goes on top: everything in a higher band covers every lower band. Begin resets it to 0.</summary>
        public int Band;
        private readonly Dictionary<(Texture, int), Layer> _layers = new Dictionary<(Texture, int), Layer>();
        private readonly int[] _usedInBand = new int[Bands];
        private int _frame;
        private float _z;

        public Canvas2D(string name, float pixelSize, Transform parent = null)
        {
            Root = new GameObject(name);
            if (parent != null) Root.transform.SetParent(parent, false);
            else Object.DontDestroyOnLoad(Root);
            Px = pixelSize;
        }

        private Layer L(Texture tex)
        {
            if (tex == null) tex = Texture2D.whiteTexture;
            int band = Mathf.Clamp(Band, 0, Bands - 1);
            if (!_layers.TryGetValue((tex, band), out var l))
            {
                l = new Layer { Mesh = new Mesh { name = "GM_ui" } };
                l.Mesh.MarkDynamic();
                l.Mat = Materials.Overlay(tex);
                l.Go = MeshUtil.Object("ui " + tex.name, l.Mesh, l.Mat, Root.transform);
                _layers[(tex, band)] = l;
            }
            if (l.UsedFrame != _frame)
            {
                l.UsedFrame = _frame;
                l.Mat.renderQueue = 4000 + band * PerBand + Mathf.Min(PerBand - 1, _usedInBand[band]++);
            }
            return l;
        }

        public void Begin()
        {
            foreach (var l in _layers.Values) { l.V.Clear(); l.U.Clear(); l.C.Clear(); l.T.Clear(); }
            _frame++;
            for (int i = 0; i < Bands; i++) _usedInBand[i] = 0;
            Band = 0;
            _z = 0;
        }

        public void End()
        {
            foreach (var l in _layers.Values)
            {
                l.Mesh.Clear();
                l.Mesh.SetVertices(l.V);
                l.Mesh.SetUVs(0, l.U);
                l.Mesh.SetColors(l.C);
                l.Mesh.SetTriangles(l.T, 0, false);
                l.Mesh.RecalculateBounds();
            }
        }

        private Vector3 P(float x, float y) => new Vector3(x * Px, -y * Px, _z);

        private void Quad(Layer l, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Color32 col)
        {
            int s = l.V.Count;
            l.V.Add(a); l.V.Add(b); l.V.Add(c); l.V.Add(d);
            l.U.Add(ua); l.U.Add(ub); l.U.Add(uc); l.U.Add(ud);
            l.C.Add(col); l.C.Add(col); l.C.Add(col); l.C.Add(col);
            l.T.Add(s); l.T.Add(s + 1); l.T.Add(s + 2); l.T.Add(s); l.T.Add(s + 2); l.T.Add(s + 3);
        }

        /// <summary>Part of a picture (src in its pixels, top-left origin) drawn at x,y with size w,h.</summary>
        public void Sprite(Texture2D tex, float x, float y, float w, float h, RectInt src, Color32 col)
        {
            if (tex == null) return;
            var l = L(tex);
            float tw = tex.width, th = tex.height;
            float u0 = src.x / tw, u1 = (src.x + src.width) / tw;
            float v1 = 1f - src.y / th, v0 = 1f - (src.y + src.height) / th;
            Quad(l, P(x, y + h), P(x, y), P(x + w, y), P(x + w, y + h), new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0), col);
        }

        public void Sprite(Texture2D tex, float x, float y, Color32? col = null)
        {
            if (tex == null) return;
            Sprite(tex, x, y, tex.width, tex.height, new RectInt(0, 0, tex.width, tex.height), col ?? new Color32(255, 255, 255, 255));
        }

        public void Rect(float x, float y, float w, float h, Color32 col)
        {
            var l = L(Texture2D.whiteTexture);
            var uv = new Vector2(0.5f, 0.5f);
            Quad(l, P(x, y + h), P(x, y), P(x + w, y), P(x + w, y + h), uv, uv, uv, uv, col);
        }

        /// <summary>A block drawn the way Minecraft shows it in menus: three faces of a little cube.</summary>
        public void BlockIcon(BlockDef b, float x, float y, float size, byte alpha = 255)
        {
            if (b == null || BlockRegistry.Atlas == null || BlockRegistry.UvT == null) return;
            var l = L(BlockRegistry.Atlas);
            float k = size / 16f;
            Vector3 Pt(float px, float py) => P(x + px * k, y + py * k);
            var top = BlockRegistry.UvT[b.Index * 6 + 2];
            var left = BlockRegistry.UvT[b.Index * 6 + 4];
            var right = BlockRegistry.UvT[b.Index * 6 + 0];
            if (b.Render == RenderKind.Plant || b.Render == RenderKind.Torch)
            {
                Quad(l, Pt(1, 15), Pt(1, 1), Pt(15, 1), Pt(15, 15), new Vector2(top.x, top.y), new Vector2(top.x, top.w), new Vector2(top.z, top.w), new Vector2(top.z, top.y), new Color32(255, 255, 255, alpha));
                return;
            }
            Quad(l, Pt(8, 8.2f), Pt(1.2f, 4.4f), Pt(8, 0.6f), Pt(14.8f, 4.4f),
                new Vector2(top.x, top.y), new Vector2(top.x, top.w), new Vector2(top.z, top.w), new Vector2(top.z, top.y), new Color32(255, 255, 255, alpha));
            Quad(l, Pt(1.2f, 12f), Pt(1.2f, 4.4f), Pt(8, 8.2f), Pt(8, 15.8f),
                new Vector2(left.x, left.y), new Vector2(left.x, left.w), new Vector2(left.z, left.w), new Vector2(left.z, left.y), new Color32(205, 205, 205, alpha));
            Quad(l, Pt(8, 15.8f), Pt(8, 8.2f), Pt(14.8f, 4.4f), Pt(14.8f, 12f),
                new Vector2(right.x, right.y), new Vector2(right.x, right.w), new Vector2(right.z, right.w), new Vector2(right.z, right.y), new Color32(150, 150, 150, alpha));
        }

        public void ItemIcon(ItemDef d, float x, float y, float size = 16)
        {
            if (d == null) return;
            if (d.Block != null) { BlockIcon(d.Block, x, y, size); return; }
            if (Items.Atlas == null) return;
            var l = L(Items.Atlas);
            var r = d.AtlasUV;
            Quad(l, P(x, y + size), P(x, y), P(x + size, y), P(x + size, y + size),
                new Vector2(r.x, r.y), new Vector2(r.x, r.w), new Vector2(r.z, r.w), new Vector2(r.z, r.y), new Color32(255, 255, 255, 255));
        }

        /// <summary>A slot's stack: icon, count in the corner, and a durability bar for worn tools.</summary>
        public void Stack(ItemStack s, float x, float y)
        {
            if (s.Empty) return;
            ItemIcon(s.Item, x, y);
            Front();
            if (s.Count > 1)
            {
                string n = s.Count.ToString();
                Text(n, x + 17 - McFont.Width(n), y + 9, new Color32(255, 255, 255, 255));
            }
            if (s.Item.Durability > 0 && s.Wear > 0)
            {
                float left = 1f - s.Wear / (float)s.Item.Durability;
                Rect(x + 2, y + 13, 13, 2, new Color32(0, 0, 0, 255));
                var col = Color.HSVToRGB(left / 3f, 1f, 1f);
                Rect(x + 2, y + 13, Mathf.Round(13 * left), 1, col);
            }
            Back();
        }

        /// <summary>Everything drawn after this sits in front (text over icons) until Back.</summary>
        public void Front() { _z -= 0.0004f; Band++; }
        public void Back() { _z += 0.0004f; Band--; }

        public void Text(string s, float x, float y, Color32 col, float scale = 1f, bool shadow = true)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (!McFont.Ready) return;
            var l = L(McFont.Tex);
            float cell = McFont.Tex.width / 16f;
            float tw = McFont.Tex.width, th = McFont.Tex.height;
            for (int pass = shadow ? 0 : 1; pass < 2; pass++)
            {
                float cx = x + (pass == 0 ? scale : 0), cy = y + (pass == 0 ? scale : 0);
                var c = pass == 0 ? new Color32((byte)(col.r / 4), (byte)(col.g / 4), (byte)(col.b / 4), col.a) : col;
                if (pass == 0) Back(); else Front();
                foreach (var ch in s)
                {
                    int code = ch < 256 ? ch : '?';
                    int gx = (code % 16), gy = (code / 16);
                    float u0 = gx * cell / tw, u1 = (gx * cell + 8) / tw;
                    float v1 = 1f - gy * cell / th, v0 = 1f - (gy * cell + 8) / th;
                    Quad(l, P(cx, cy + 8 * scale), P(cx, cy), P(cx + 8 * scale, cy), P(cx + 8 * scale, cy + 8 * scale),
                        new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0), c);
                    cx += McFont.Advance(ch) * scale;
                }
                if (pass == 0) Front(); else Back();
            }
        }

        public void TextCentered(string s, float cx, float y, Color32 col, float scale = 1f)
        {
            Text(s, cx - McFont.Width(s) * scale / 2f, y, col, scale);
        }

        public void SetVisible(bool on)
        {
            if (Root.activeSelf != on) Root.SetActive(on);
        }
    }
}
