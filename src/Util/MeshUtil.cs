using System.Collections.Generic;
using UnityEngine;

namespace GorillaMerge
{
    public static class MeshUtil
    {
        /// <summary>A centred cube showing a block's pictures (used for icons, ghosts, TNT and particles).</summary>
        public static Mesh BlockCube(BlockDef def, float size, Rect? subRect = null)
        {
            var v = new List<Vector3>(24);
            var u = new List<Vector2>(24);
            var t = new List<int>(36);
            var col = new List<Color32>(24);
            float[] shade = { 0.6f, 0.6f, 1f, 0.5f, 0.8f, 0.8f };
            for (int f = 0; f < 6; f++)
            {
                // With vertex lighting the faces are shaded by colour; otherwise the atlas has the shading baked in.
                var r = BlockRegistry.VertexLighting ? BlockRegistry.TileRect(def.Tiles[f], 0) : BlockRegistry.TileRect(def.Tiles[f], BlockRegistry.FaceShade[f]);
                byte sh = (byte)((BlockRegistry.VertexLighting ? shade[f] : 1f) * 255);
                for (int i = 0; i < 4; i++) col.Add(new Color32(sh, sh, sh, 255));
                if (subRect.HasValue)
                {
                    var s = subRect.Value;
                    float w = r.z - r.x, h = r.w - r.y;
                    r = new Vector4(r.x + s.xMin * w, r.y + s.yMin * h, r.x + s.xMax * w, r.y + s.yMax * h);
                }
                int start = v.Count;
                for (int i = 0; i < 4; i++) v.Add((VoxelWorld.FaceCorner(f, i) - new Vector3(0.5f, 0.5f, 0.5f)) * size);
                u.Add(new Vector2(r.x, r.y)); u.Add(new Vector2(r.x, r.w)); u.Add(new Vector2(r.z, r.w)); u.Add(new Vector2(r.z, r.y));
                t.Add(start); t.Add(start + 1); t.Add(start + 2); t.Add(start); t.Add(start + 2); t.Add(start + 3);
            }
            var m = new Mesh { name = "GM_cube" };
            m.SetVertices(v);
            m.SetUVs(0, u);
            m.SetColors(col);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A plain cube with 0..1 UVs on every face (crack overlay, flashes).</summary>
        public static Mesh UnitCube(float size)
        {
            var v = new List<Vector3>(24);
            var u = new List<Vector2>(24);
            var t = new List<int>(36);
            for (int f = 0; f < 6; f++)
            {
                int start = v.Count;
                for (int i = 0; i < 4; i++) v.Add((VoxelWorld.FaceCorner(f, i) - new Vector3(0.5f, 0.5f, 0.5f)) * size);
                u.Add(new Vector2(0, 0)); u.Add(new Vector2(0, 1)); u.Add(new Vector2(1, 1)); u.Add(new Vector2(1, 0));
                t.Add(start); t.Add(start + 1); t.Add(start + 2); t.Add(start); t.Add(start + 2); t.Add(start + 3);
            }
            var m = new Mesh { name = "GM_unitcube" };
            m.SetVertices(v);
            m.SetUVs(0, u);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>The 12 edges of a cube as thin boxes: the black outline around the block you point at.</summary>
        public static Mesh Outline(float size, float thickness)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            float h = size * 0.5f;
            void Bar(Vector3 center, Vector3 extents)
            {
                for (int f = 0; f < 6; f++)
                {
                    int start = v.Count;
                    for (int i = 0; i < 4; i++)
                    {
                        var c = VoxelWorld.FaceCorner(f, i) * 2f - Vector3.one;
                        v.Add(center + Vector3.Scale(c, extents));
                    }
                    t.Add(start); t.Add(start + 1); t.Add(start + 2); t.Add(start); t.Add(start + 2); t.Add(start + 3);
                }
            }
            float e = thickness * 0.5f;
            for (int a = -1; a <= 1; a += 2)
                for (int b = -1; b <= 1; b += 2)
                {
                    Bar(new Vector3(0, a * h, b * h), new Vector3(h + e, e, e));
                    Bar(new Vector3(a * h, 0, b * h), new Vector3(e, h + e, e));
                    Bar(new Vector3(a * h, b * h, 0), new Vector3(e, e, h + e));
                }
            var m = new Mesh { name = "GM_outline" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        public static GameObject Object(string name, Mesh mesh, Material mat, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        public static TextMesh Text(string name, Transform parent, Vector3 localPos, float charSize, Color color, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = charSize;
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = font.material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return tm;
        }
    }
}
