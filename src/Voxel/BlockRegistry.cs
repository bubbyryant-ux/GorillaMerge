using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    public enum RenderKind { Opaque, Leaves, Glass, Water, Portal, Lava, Plant, Torch }

    public class BlockDef
    {
        public byte Light;          // light it gives off, 0-15 (torch 14, glowstone and lava 15)
        public string Tool;         // "pickaxe", "axe", "shovel" or null
        public int ToolLevel;       // tier needed to get a drop: 0 hand, 1 wood, 2 stone, 3 iron, 4 diamond
        public JToken Drops;        // null = drops itself; "none"; an item id; or a list of {item, chance, min, max}
        public string Next;         // crops: the block this grows into
        public bool Liquid => Render == RenderKind.Water || Render == RenderKind.Lava;
        public ushort Index;
        public string Key;      // "<pack id>:<block id>", unique across packs
        public string LocalId;
        public string Name;
        public Pack Pack;
        public RenderKind Render;
        public bool Solid = true;
        public float Hardness = 1f;
        public string Sound = "stone";
        public string Surface = "stone";
        public string Special;
        public bool Item = true;
        public readonly int[] Tiles = new int[6]; // per face: east, west, top, bottom, south, north
        public int SurfaceIndex;                  // Gorilla Tag hand-tap material

        public bool Opaque => Render == RenderKind.Opaque || Render == RenderKind.Leaves;
        public bool Targetable => !Liquid;
        public bool IsTnt => Special == "tnt";
        public bool IsPortal => Special == "portal";
        public bool Unbreakable => Hardness < 0f;

        /// <summary>Seconds of holding the mine trigger to break it.</summary>
        public float BreakTime => 0.15f + Mathf.Clamp(Hardness, 0f, 6f) * 0.35f;
        /// <summary>Fist punches needed to break it.</summary>
        public int PunchHits => Mathf.Max(1, Mathf.CeilToInt(BreakTime / 0.3f));
    }

    /// <summary>
    /// Every block from every voxel pack, plus one texture atlas holding all their pictures.
    /// Each picture is stored 4 times at different brightness (top / north-south / east-west / bottom),
    /// which gives the Minecraft-style shaded look with a plain unlit shader.
    /// </summary>
    public static class BlockRegistry
    {
        public static readonly List<BlockDef> All = new List<BlockDef> { new BlockDef { Index = 0, Key = "air", LocalId = "air", Name = "Air", Solid = false, Item = false } };
        private static readonly Dictionary<string, BlockDef> ByKey = new Dictionary<string, BlockDef>(StringComparer.OrdinalIgnoreCase);

        public static Texture2D Atlas;
        public static Material OpaqueMat, TransparentMat, GhostMat;
        public static readonly float[] Shades = { 1f, 0.8f, 0.6f, 0.5f };
        public static readonly int[] FaceShade = { 2, 2, 0, 3, 1, 1 };

        private class RawTile { public Color32[] Px; public int W, H; }
        private static readonly List<RawTile> Raw = new List<RawTile>();
        private static readonly Dictionary<string, int> TileCache = new Dictionary<string, int>();
        private static int _tileSize, _pad, _cell, _cols, _w, _h;

        public static BlockDef Get(string key) => key != null && ByKey.TryGetValue(key, out var b) ? b : null;

        public static void AddPack(Pack pack)
        {
            if (!(pack.Json["blocks"] is JArray blocks)) return;
            foreach (var t in blocks)
            {
                if (!(t is JObject o)) continue;
                try { AddBlock(pack, o); }
                catch (Exception ex) { Plugin.Log.LogWarning($"{pack.Name}: bad block {o["id"]}: {ex.Message}"); }
            }
        }

        private static void AddBlock(Pack pack, JObject o)
        {
            string id = (string)o["id"];
            if (string.IsNullOrEmpty(id)) return;
            var b = new BlockDef
            {
                Index = (ushort)All.Count,
                LocalId = id,
                Key = pack.Id + ":" + id,
                Name = (string)o["name"] ?? id,
                Pack = pack,
                Hardness = (float?)o["hardness"] ?? 1f,
                Sound = (string)o["sound"] ?? "stone",
                Special = (string)o["special"],
            };
            switch (((string)o["render"] ?? "opaque").ToLowerInvariant())
            {
                case "leaves": b.Render = RenderKind.Leaves; break;
                case "glass": b.Render = RenderKind.Glass; break;
                case "water": b.Render = RenderKind.Water; break;
                case "portal": b.Render = RenderKind.Portal; break;
                case "lava": b.Render = RenderKind.Lava; break;
                case "plant": b.Render = RenderKind.Plant; break;
                case "torch": b.Render = RenderKind.Torch; break;
                default: b.Render = RenderKind.Opaque; break;
            }
            bool walkThrough = b.Liquid || b.Render == RenderKind.Portal || b.Render == RenderKind.Plant || b.Render == RenderKind.Torch;
            b.Solid = (bool?)o["solid"] ?? !walkThrough;
            b.Item = (bool?)o["item"] ?? (b.Render != RenderKind.Portal && !b.Liquid);
            b.Surface = (string)o["surface"] ?? b.Sound;
            b.Light = (byte)Mathf.Clamp((int?)o["light"] ?? 0, 0, 15);
            b.Tool = (string)o["tool"];
            b.ToolLevel = (int?)o["toolLevel"] ?? 0;
            b.Drops = o["drops"];
            b.Next = (string)o["next"];

            Color fallback = GameAssets.ParseColor((string)o["color"], new Color(0.5f, 0.5f, 0.5f));
            Color? tint = o["tint"] != null ? GameAssets.ParseColor((string)o["tint"], Color.white) : (Color?)null;
            string tintFaces = ((string)o["tintFaces"] ?? "all").ToLowerInvariant();
            string all = (string)o["texture"];
            string top = (string)o["top"] ?? (string)o["end"] ?? all;
            string bottom = (string)o["bottom"] ?? (string)o["end"] ?? all;
            string side = (string)o["side"] ?? all;
            string overlay = (string)o["overlay"];
            string[] faceSpec =
            {
                (string)o["east"] ?? side, (string)o["west"] ?? side, top, bottom,
                (string)o["south"] ?? side, (string)o["north"] ?? side,
            };
            for (int f = 0; f < 6; f++)
            {
                bool isSide = f != 2 && f != 3;
                bool tinted = tint.HasValue && (tintFaces == "all" || (tintFaces == "top" && f == 2) || (tintFaces == "sides" && isSide));
                string ov = isSide ? overlay : null;
                Color? faceTint = tinted ? tint : null;
                // Grass block sides: plain dirt-coloured picture with a tinted green overlay on top.
                Color? ovTint = ov != null ? tint : null;
                b.Tiles[f] = Tile(pack, faceSpec[f] ?? "#", faceTint, ov, ovTint, b.Render, fallback);
            }

            All.Add(b);
            ByKey[b.Key] = b;
            pack.Blocks.Add(b);
            if (b.Item) pack.Items.Add(b);
        }

        private static int Tile(Pack pack, string spec, Color? tint, string overlay, Color? overlayTint, RenderKind render, Color fallback)
        {
            string key = $"{pack.Dir}|{spec}|{tint}|{overlay}|{overlayTint}|{render}|{fallback}";
            if (TileCache.TryGetValue(key, out int existing)) return existing;

            var tex = GameAssets.LoadTexture(spec, pack.Dir);
            RawTile raw;
            if (tex != null)
            {
                raw = new RawTile { Px = tex.GetPixels32(), W = tex.width, H = tex.height };
                UnityEngine.Object.Destroy(tex);
            }
            else
            {
                Color baseCol = spec.StartsWith("#", StringComparison.Ordinal) && spec.Length > 1 ? GameAssets.ParseColor(spec, fallback) : fallback;
                raw = Procedural(baseCol, render, key.GetHashCode());
                tint = null; // the fallback colour is already the final colour
                overlay = null;
            }

            if (tint.HasValue) Multiply(raw.Px, tint.Value);
            if (overlay != null)
            {
                var ovTex = GameAssets.LoadTexture(overlay, pack.Dir);
                if (ovTex != null)
                {
                    var ov = Resize(ovTex.GetPixels32(), ovTex.width, ovTex.height, raw.W, raw.H);
                    UnityEngine.Object.Destroy(ovTex);
                    if (overlayTint.HasValue) Multiply(ov, overlayTint.Value);
                    for (int i = 0; i < raw.Px.Length; i++)
                    {
                        float a = ov[i].a / 255f;
                        raw.Px[i] = Color32.Lerp(raw.Px[i], new Color32(ov[i].r, ov[i].g, ov[i].b, 255), a);
                    }
                }
            }

            for (int i = 0; i < raw.Px.Length; i++)
            {
                var c = raw.Px[i];
                switch (render)
                {
                    case RenderKind.Opaque: c.a = 255; break;
                    case RenderKind.Leaves:
                        // "Fast graphics" leaves: see-through gaps become dark leaf colour (no cutout shader needed).
                        if (c.a < 128) c = new Color32((byte)(c.r * 0.25f + 8), (byte)(c.g * 0.3f + 18), (byte)(c.b * 0.25f + 6), 255);
                        else c.a = 255;
                        break;
                    case RenderKind.Water: c.a = (byte)Mathf.Clamp(c.a * 0.85f, 120, 200); break;
                    case RenderKind.Lava: c.a = 255; break;
                    case RenderKind.Portal: c.a = (byte)Mathf.Clamp(c.a, 150, 210); break;
                }
                raw.Px[i] = c;
            }

            Raw.Add(raw);
            TileCache[key] = Raw.Count - 1;
            return Raw.Count - 1;
        }

        private static RawTile Procedural(Color c, RenderKind render, int seed)
        {
            const int n = 16;
            var px = new Color32[n * n];
            var rng = new System.Random(seed);
            for (int i = 0; i < px.Length; i++)
            {
                float k = 0.88f + (float)rng.NextDouble() * 0.24f;
                var col = new Color(c.r * k, c.g * k, c.b * k, c.a);
                if (render == RenderKind.Leaves && rng.NextDouble() < 0.25) col *= 0.55f;
                if (render == RenderKind.Glass)
                {
                    int x = i % n, y = i / n;
                    bool edge = x == 0 || y == 0 || x == n - 1 || y == n - 1;
                    col.a = edge ? 1f : 0.15f;
                }
                px[i] = col;
            }
            return new RawTile { Px = px, W = n, H = n };
        }

        private static void Multiply(Color32[] px, Color t)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                px[i] = new Color32((byte)(c.r * t.r), (byte)(c.g * t.g), (byte)(c.b * t.b), c.a);
            }
        }

        private static Color32[] Resize(Color32[] src, int sw, int sh, int dw, int dh)
        {
            if (sw == dw && sh == dh) return (Color32[])src.Clone();
            var dst = new Color32[dw * dh];
            if (dw >= sw && dh >= sh)
            {
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                        dst[y * dw + x] = src[(y * sh / dh) * sw + (x * sw / dw)];
                return dst;
            }
            // Shrinking: average the source pixels that land in each destination pixel.
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Mathf.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Mathf.Max(x0 + 1, (x + 1) * sw / dw);
                    float r = 0, g = 0, b = 0, a = 0; int count = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            var c = src[yy * sw + xx];
                            r += c.r; g += c.g; b += c.b; a += c.a; count++;
                        }
                    dst[y * dw + x] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), (byte)(a / count));
                }
            }
            return dst;
        }

        public static int TileSize => _tileSize;

        public static void BuildAtlas()
        {
            int biggest = 16;
            foreach (var r in Raw) biggest = Mathf.Max(biggest, Mathf.Min(r.W, 64));
            _tileSize = Mathf.ClosestPowerOfTwo(biggest);
            _pad = Mathf.Max(2, _tileSize / 4);
            _cell = _tileSize + _pad * 2;
            int cells = Mathf.Max(1, Raw.Count * 4);
            _cols = Mathf.CeilToInt(Mathf.Sqrt(cells));
            int rows = Mathf.CeilToInt(cells / (float)_cols);
            _w = Mathf.NextPowerOfTwo(_cols * _cell);
            _h = Mathf.NextPowerOfTwo(rows * _cell);

            var atlasPx = new Color32[_w * _h];
            int T = _tileSize;
            for (int t = 0; t < Raw.Count; t++)
            {
                var r = Raw[t];
                var px = Resize(r.Px, r.W, r.H, T, T);
                for (int shade = 0; shade < 4; shade++)
                {
                    float k = Shades[shade];
                    int cellIndex = t * 4 + shade;
                    int ox = (cellIndex % _cols) * _cell, oy = (cellIndex / _cols) * _cell;
                    for (int cy = 0; cy < _cell; cy++)
                    {
                        int sy = Mathf.Clamp(cy - _pad, 0, T - 1);
                        for (int cx = 0; cx < _cell; cx++)
                        {
                            int sx = Mathf.Clamp(cx - _pad, 0, T - 1);
                            var c = px[sy * T + sx];
                            atlasPx[(oy + cy) * _w + ox + cx] = new Color32((byte)(c.r * k), (byte)(c.g * k), (byte)(c.b * k), c.a);
                        }
                    }
                }
            }

            int mips = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(T, 2)) - 1, 1, 4);
            Atlas = new Texture2D(_w, _h, TextureFormat.RGBA32, mips, false)
            {
                name = "GorillaMergeAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
            Atlas.SetPixels32(atlasPx);
            Atlas.Apply(true, false);
            // With a vertex-colour shader the world gets real lighting (sky, torches, corner shadows);
            // otherwise fall back to the brightness baked into the atlas.
            VertexLighting = Materials.HasVertexColorShader;
            OpaqueMat = VertexLighting ? Materials.VertexLit(Atlas) : Materials.Opaque(Atlas);
            TransparentMat = VertexLighting ? Materials.VertexLitTransparent(Atlas, Color.white) : Materials.Transparent(Atlas, Color.white);
            GhostMat = Materials.Transparent(Atlas, new Color(1f, 1f, 1f, 0.45f));
            BuildTables();
            Plugin.Log.LogInfo($"Atlas: {Raw.Count} pictures, {T}px tiles, {_w}x{_h}, {All.Count - 1} blocks.");
            Raw.Clear();
        }

        public static bool VertexLighting;

        // ---- Flat lookup tables, read by the background meshing/generation threads ----
        public static bool[] OpaqueT;     // hides neighbour faces, blocks ambient occlusion (cubes and leaves)
        public static bool[] SolidT;      // you collide with it
        public static bool[] BlocksLightT; // stops light (opaque cubes, not leaves)
        public static bool[] LeavesT;     // lets light through but dims it
        public static byte[] EmitT;       // light it gives off
        public static byte[] KindT;       // RenderKind
        public static int[] SurfT;        // Gorilla Tag surface index
        public static Vector4[] UvT;      // [id * 6 + face] atlas rectangle (unshaded tile)
        public static Vector4[] UvShadedT; // [id * 6 + face] atlas rectangle with the baked face brightness

        public static bool IsOpaqueId(ushort id) => OpaqueT != null ? id < OpaqueT.Length && OpaqueT[id] : All[id].Opaque;

        private static void BuildTables()
        {
            int n = All.Count;
            OpaqueT = new bool[n]; SolidT = new bool[n]; BlocksLightT = new bool[n]; LeavesT = new bool[n];
            EmitT = new byte[n]; KindT = new byte[n]; SurfT = new int[n];
            UvT = new Vector4[n * 6]; UvShadedT = new Vector4[n * 6];
            for (int i = 1; i < n; i++)
            {
                var b = All[i];
                OpaqueT[i] = b.Opaque;
                SolidT[i] = b.Solid;
                BlocksLightT[i] = b.Render == RenderKind.Opaque;
                LeavesT[i] = b.Render == RenderKind.Leaves;
                EmitT[i] = b.Light;
                KindT[i] = (byte)b.Render;
                SurfT[i] = b.SurfaceIndex;
                for (int f = 0; f < 6; f++)
                {
                    UvT[i * 6 + f] = TileRect(b.Tiles[f], 0);
                    UvShadedT[i * 6 + f] = TileRect(b.Tiles[f], FaceShade[f]);
                }
            }
        }

        /// <summary>UV rectangle (u0, v0, u1, v1) of a tile at a brightness level.</summary>
        public static Vector4 TileRect(int tile, int shade)
        {
            int cellIndex = tile * 4 + shade;
            float u0 = ((cellIndex % _cols) * _cell + _pad) / (float)_w;
            float v0 = ((cellIndex / _cols) * _cell + _pad) / (float)_h;
            float eu = 0.02f / _w, ev = 0.02f / _h;
            return new Vector4(u0 + eu, v0 + ev, u0 + _tileSize / (float)_w - eu, v0 + _tileSize / (float)_h - ev);
        }
    }
}
