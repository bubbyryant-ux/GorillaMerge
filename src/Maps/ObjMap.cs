using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaMerge
{
    /// <summary>
    /// A "map" pack: any 3D level saved as .obj (with its .mtl and pictures) becomes a world you can
    /// climb with Gorilla Tag movement. This is the "put Gorilla Tag into any game" part: export a
    /// level (or build one) to .obj, drop it in a pack folder, and it shows up in the wrist menu.
    /// </summary>
    public class ObjMap
    {
        public GameObject Root;
        public Vector3 SpawnPoint;
        public Bounds Bounds;

        private class Group
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> U = new List<Vector2>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> T = new List<int>();
        }

        private class Mtl
        {
            public Color Kd = Color.white;
            public string MapKd;
            public float Alpha = 1f;
        }

        public static ObjMap Load(Pack pack, Vector3 origin, int layer, int surfaceIndex)
        {
            string file = GameAssets.ResolvePath(pack.Model ?? "", pack.Dir);
            if (!File.Exists(file)) throw new FileNotFoundException("Map model not found", file);
            string dir = Path.GetDirectoryName(file);
            var inv = CultureInfo.InvariantCulture;
            float scale = pack.Scale;

            var pos = new List<Vector3>();
            var uvs = new List<Vector2>();
            var nrm = new List<Vector3>();
            var mtls = new Dictionary<string, Mtl>(StringComparer.Ordinal);
            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            string current = "";
            var sun = new Vector3(0.4f, 0.8f, 0.3f).normalized;
            var face = new List<(int v, int t, int n)>();

            foreach (var rawLine in File.ReadLines(file))
            {
                var line = rawLine.Trim();
                if (line.Length < 2 || line[0] == '#') continue;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "v":
                        // OBJ is right-handed, Unity is left-handed: flip X (and the triangle order below).
                        pos.Add(new Vector3(-float.Parse(parts[1], inv), float.Parse(parts[2], inv), float.Parse(parts[3], inv)) * scale);
                        break;
                    case "vt":
                        uvs.Add(new Vector2(float.Parse(parts[1], inv), parts.Length > 2 ? float.Parse(parts[2], inv) : 0f));
                        break;
                    case "vn":
                        nrm.Add(new Vector3(-float.Parse(parts[1], inv), float.Parse(parts[2], inv), float.Parse(parts[3], inv)));
                        break;
                    case "mtllib":
                        LoadMtl(Path.Combine(dir, line.Substring(7).Trim()), mtls);
                        break;
                    case "usemtl":
                        current = parts.Length > 1 ? line.Substring(6).Trim() : "";
                        break;
                    case "f":
                    {
                        face.Clear();
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var idx = parts[i].Split('/');
                            int v = Index(idx[0], pos.Count);
                            int t = idx.Length > 1 && idx[1].Length > 0 ? Index(idx[1], uvs.Count) : -1;
                            int n = idx.Length > 2 && idx[2].Length > 0 ? Index(idx[2], nrm.Count) : -1;
                            face.Add((v, t, n));
                        }
                        if (face.Count < 3) break;
                        if (!groups.TryGetValue(current, out var g)) groups[current] = g = new Group();
                        var fn = Vector3.Cross(pos[face[2].v] - pos[face[0].v], pos[face[1].v] - pos[face[0].v]).normalized;
                        for (int i = 1; i + 1 < face.Count; i++)
                        {
                            // Reversed order because X was flipped.
                            foreach (var c in new[] { face[0], face[i + 1], face[i] })
                            {
                                g.T.Add(g.V.Count);
                                g.V.Add(pos[c.v]);
                                g.U.Add(c.t >= 0 ? uvs[c.t] : Vector2.zero);
                                var n = c.n >= 0 ? nrm[c.n] : fn;
                                float light = 0.58f + 0.42f * Mathf.Max(0f, Vector3.Dot(n, sun)) + 0.08f * n.y;
                                g.C.Add(new Color(light, light, light, 1f));
                            }
                        }
                        break;
                    }
                }
            }

            var map = new ObjMap { Root = new GameObject("GM_Map_" + pack.Id) };
            UnityEngine.Object.DontDestroyOnLoad(map.Root);
            map.Root.transform.position = origin;
            var texCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            bool first = true;
            var bounds = new Bounds();
            foreach (var kv in groups)
            {
                if (kv.Value.T.Count == 0) continue;
                mtls.TryGetValue(kv.Key, out var mtl);
                mtl = mtl ?? new Mtl();
                Texture2D tex = null;
                if (!string.IsNullOrEmpty(mtl.MapKd))
                {
                    string texPath = Path.IsPathRooted(mtl.MapKd) ? mtl.MapKd : Path.Combine(dir, mtl.MapKd);
                    if (!texCache.TryGetValue(texPath, out tex))
                    {
                        tex = GameAssets.LoadTexture(texPath, null, false);
                        if (tex != null)
                        {
                            tex.wrapMode = TextureWrapMode.Repeat;
                            tex.filterMode = FilterMode.Bilinear;
                        }
                        texCache[texPath] = tex;
                    }
                }
                var mat = Materials.VertexLit(tex);
                var tint = mtl.Kd;
                tint.a = mtl.Alpha;
                Materials.SetColor(mat, tex != null ? new Color(1, 1, 1, mtl.Alpha) : tint);

                var mesh = new Mesh { name = "GM_map " + kv.Key, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(kv.Value.V);
                mesh.SetUVs(0, kv.Value.U);
                mesh.SetColors(kv.Value.C);
                mesh.SetTriangles(kv.Value.T, 0);
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();

                var go = MeshUtil.Object(kv.Key.Length > 0 ? kv.Key : "default", mesh, mat, map.Root.transform);
                go.layer = layer;
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;
                go.AddComponent<GorillaSurfaceOverride>().overrideIndex = surfaceIndex;

                var b = mesh.bounds;
                if (first) { bounds = b; first = false; }
                else bounds.Encapsulate(b);
            }
            map.Bounds = new Bounds(bounds.center + origin, bounds.size);

            if (pack.Spawn.HasValue) map.SpawnPoint = origin + pack.Spawn.Value;
            else
            {
                // Drop a ray from above the middle of the map to find a floor to stand on.
                Physics.SyncTransforms();
                var top = map.Bounds.center + Vector3.up * (map.Bounds.extents.y + 5f);
                map.SpawnPoint = Physics.Raycast(top, Vector3.down, out var hit, map.Bounds.size.y + 10f, 1 << layer)
                    ? hit.point + Vector3.up * 0.2f
                    : map.Bounds.center;
            }
            Plugin.Log.LogInfo($"Map '{pack.Name}': {groups.Count} materials, size {map.Bounds.size}.");
            return map;
        }

        private static int Index(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;
        }

        private static void LoadMtl(string path, Dictionary<string, Mtl> mtls)
        {
            if (!File.Exists(path)) return;
            var inv = CultureInfo.InvariantCulture;
            Mtl cur = null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "newmtl": mtls[line.Substring(6).Trim()] = cur = new Mtl(); break;
                    case "Kd":
                        if (cur != null && parts.Length >= 4)
                            cur.Kd = new Color(float.Parse(parts[1], inv), float.Parse(parts[2], inv), float.Parse(parts[3], inv));
                        break;
                    case "d": if (cur != null) cur.Alpha = float.Parse(parts[1], inv); break;
                    case "Tr": if (cur != null) cur.Alpha = 1f - float.Parse(parts[1], inv); break;
                    // Texture options like "-s 1 1 1" come first; the file name is the last word.
                    case "map_Kd": if (cur != null) cur.MapKd = parts[parts.Length - 1]; break;
                }
            }
        }

        public void SetVisible(bool on)
        {
            if (Root != null && Root.activeSelf != on) Root.SetActive(on);
        }
    }
}
