using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaMerge
{
    /// <summary>Marks a collider as part of a voxel world, so hand touches can find the block.</summary>
    public class VoxelCollider : MonoBehaviour
    {
        public VoxelWorld World;
    }

    /// <summary>
    /// A grid of blocks in 16x16x16 chunks. A world with a TerrainGen is endless: columns of chunks are
    /// generated on background threads as you move, meshed (with lighting) on background threads, and
    /// unloaded when far away. Chunks you changed are remembered and saved. A world without a generator
    /// (your builds in Gorilla Tag's own maps) just holds what you placed.
    /// </summary>
    public class VoxelWorld
    {
        public const int CS = 16;

        private class Chunk
        {
            public Vector3Int C;
            public ushort[] D = new ushort[CS * CS * CS];
            public int Count;
            public bool Modified;
            public GameObject Go;
            public Mesh Opaque, Trans;
            public byte[] OpaqueL, TransL;
            public float ColoredFor = -1f;
            public readonly Dictionary<int, (GameObject go, MeshCollider col, Mesh mesh)> Cols = new Dictionary<int, (GameObject, MeshCollider, Mesh)>();
        }

        private class Column
        {
            public bool Generated, Generating, Meshing;
            public short[] Heights;
            public int Version, MeshedVersion = -1;
            public bool Priority;
        }

        private class GenResult
        {
            public Vector2Int C;
            public ushort[][] Sections;
            public short[] Heights;
        }

        public readonly string Id;
        public readonly Pack Pack;
        public readonly float S;
        public Vector3 Origin;
        public readonly GameObject Root;
        public readonly TerrainGen Gen;
        public Vector3Int SpawnBlock;
        public bool Unsaved;
        public float TimeOfDay = 0.25f; // saved with the world (0 = sunrise, 0.25 = noon, 0.75 = midnight)
        /// <summary>Called after Set changes a block (not for generation, loading or ApplyRemote). Used to share edits with friends.</summary>
        public Action<Vector3Int, ushort> Changed;

        private readonly Dictionary<Vector3Int, Chunk> _chunks = new Dictionary<Vector3Int, Chunk>();
        private readonly Dictionary<Vector2Int, Column> _cols = new Dictionary<Vector2Int, Column>();
        private readonly Dictionary<Vector3Int, ushort[]> _saved = new Dictionary<Vector3Int, ushort[]>();
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();
        private readonly ConcurrentQueue<GenResult> _genDone = new ConcurrentQueue<GenResult>();
        private readonly ConcurrentQueue<ColumnMeshResult> _meshDone = new ConcurrentQueue<ColumnMeshResult>();
        private readonly int _layer;
        private int _jobs;
        private bool _destroyed;
        private float _daylight = 1f, _colorDaylight = -1f;
        private readonly List<Chunk> _recolorQueue = new List<Chunk>();
        private Vector2Int _viewerCol;

        public VoxelWorld(string id, Pack pack, Vector3 origin, float blockSize, int layer, TerrainGen gen = null)
        {
            Id = id;
            Pack = pack;
            Origin = origin;
            S = blockSize;
            _layer = layer;
            Gen = gen;
            Root = new GameObject("GM_World_" + id);
            Root.transform.position = origin;
            UnityEngine.Object.DontDestroyOnLoad(Root);
        }

        public bool Infinite => Gen != null;
        public int ChunkCount => _chunks.Count;
        public int LoadedColumns { get { int n = 0; foreach (var c in _cols.Values) if (c.Generated) n++; return n; } }
        public int PendingJobs => _jobs;

        // ---------- Blocks ----------

        private static int Idx(int x, int y, int z) => (y * CS + z) * CS + x;

        public ushort Get(int x, int y, int z)
        {
            return _chunks.TryGetValue(new Vector3Int(x >> 4, y >> 4, z >> 4), out var c) ? c.D[Idx(x & 15, y & 15, z & 15)] : (ushort)0;
        }

        public ushort Get(Vector3Int p) => Get(p.x, p.y, p.z);

        public BlockDef Def(Vector3Int p) => BlockRegistry.All[Get(p)];

        /// <summary>A block even where the world isn't loaded, if it's in a changed chunk (air otherwise).</summary>
        public ushort GetAnywhere(Vector3Int p)
        {
            if (IsLoaded(p)) return Get(p);
            return _saved.TryGetValue(new Vector3Int(p.x >> 4, p.y >> 4, p.z >> 4), out var D) ? D[Idx(p.x & 15, p.y & 15, p.z & 15)] : (ushort)0;
        }

        public bool InBounds(Vector3Int p)
        {
            if (Gen != null) return p.y > 0 && p.y < TerrainGen.Height && IsLoaded(p);
            return p.y > -2048 && p.y < 2048;
        }

        public bool IsLoaded(Vector3Int p)
        {
            if (Gen == null) return true;
            return _cols.TryGetValue(new Vector2Int(p.x >> 4, p.z >> 4), out var c) && c.Generated;
        }

        /// <summary>Changes a block (as the player): redraws soon and remembers the change.</summary>
        public bool Set(Vector3Int p, ushort id)
        {
            if (!SetQuiet(p, id)) return false;
            Changed?.Invoke(p, id);
            return true;
        }

        /// <summary>
        /// A block a friend changed. Where your world is loaded it changes like Set; where it isn't, the chunk is
        /// generated now and the change is kept with your other changes, so it's there when you get close.
        /// </summary>
        public bool ApplyRemote(Vector3Int p, ushort id)
        {
            if (Gen == null || IsLoaded(p)) return SetQuiet(p, id);
            if (p.y <= 0 || p.y >= TerrainGen.Height) return false;
            var cc = new Vector3Int(p.x >> 4, p.y >> 4, p.z >> 4);
            if (!_saved.TryGetValue(cc, out var D))
            {
                var secs = Gen.GenerateColumn(cc.x, cc.z, new short[CS * CS]);
                D = cc.y < secs.Length && secs[cc.y] != null ? (ushort[])secs[cc.y].Clone() : new ushort[CS * CS * CS];
                _saved[cc] = D;
            }
            D[Idx(p.x & 15, p.y & 15, p.z & 15)] = id;
            Unsaved = true;
            return true;
        }

        private bool SetQuiet(Vector3Int p, ushort id)
        {
            if (!InBounds(p)) return false;
            ushort old = Get(p);
            if (!SetRaw(p.x, p.y, p.z, id)) return false;
            Unsaved = true;
            var cc = new Vector3Int(p.x >> 4, p.y >> 4, p.z >> 4);
            if (_chunks.TryGetValue(cc, out var ch)) ch.Modified = true;
            else _saved[cc] = new ushort[CS * CS * CS]; // the chunk became empty: remember that it's all air now

            var col = new Vector2Int(p.x >> 4, p.z >> 4);
            UpdateHeight(col, p.x & 15, p.z & 15);
            bool lightChange = BlockRegistry.BlocksLightT[old] != BlockRegistry.BlocksLightT[id] || BlockRegistry.EmitT[old] != BlockRegistry.EmitT[id]
                               || BlockRegistry.LeavesT[old] != BlockRegistry.LeavesT[id];
            MarkDirty(col, true);
            int lx = p.x & 15, lz = p.z & 15;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    bool touches = (dx == -1 && lx == 0) || (dx == 1 && lx == 15) || dx == 0;
                    touches &= (dz == -1 && lz == 0) || (dz == 1 && lz == 15) || dz == 0;
                    if (touches || (lightChange && BlockRegistry.VertexLighting)) MarkDirty(col + new Vector2Int(dx, dz), touches);
                }
            return true;
        }

        /// <summary>Changes a block without any bookkeeping (world generation, loading).</summary>
        public bool SetRaw(int x, int y, int z, ushort id)
        {
            var cc = new Vector3Int(x >> 4, y >> 4, z >> 4);
            if (!_chunks.TryGetValue(cc, out var c))
            {
                if (id == 0) return false;
                c = new Chunk { C = cc };
                _chunks[cc] = c;
                if (Gen == null) EnsureColumn(new Vector2Int(cc.x, cc.z));
            }
            int i = Idx(x & 15, y & 15, z & 15);
            ushort old = c.D[i];
            if (old == id) return false;
            c.D[i] = id;
            if (old == 0) c.Count++;
            else if (id == 0) c.Count--;
            if (Gen == null) MarkDirty(new Vector2Int(cc.x, cc.z), false);
            return true;
        }

        private Column EnsureColumn(Vector2Int cc)
        {
            if (!_cols.TryGetValue(cc, out var col))
            {
                col = new Column { Generated = Gen == null };
                _cols[cc] = col;
            }
            return col;
        }

        private void MarkDirty(Vector2Int cc, bool priority)
        {
            if (!_cols.TryGetValue(cc, out var col)) return;
            col.Version++;
            if (priority) col.Priority = true;
            _dirty.Add(cc);
        }

        private void UpdateHeight(Vector2Int cc, int lx, int lz)
        {
            if (!_cols.TryGetValue(cc, out var col) || col.Heights == null) return;
            short top = 0;
            for (int y = TerrainGen.Height - 1; y > 0; y--)
            {
                ushort id = Get(cc.x * 16 + lx, y, cc.y * 16 + lz);
                if (id != 0 && BlockRegistry.IsOpaqueId(id)) { top = (short)y; break; }
            }
            col.Heights[lz * 16 + lx] = top;
        }

        public Vector3Int ToBlock(Vector3 world)
        {
            var p = (world - Origin) / S;
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        }

        public Vector3 Center(Vector3Int b) => Origin + ((Vector3)b + new Vector3(0.5f, 0.5f, 0.5f)) * S;
        public Vector3 Corner(Vector3Int b) => Origin + (Vector3)b * S;
        public Bounds BlockBounds(Vector3Int b) => new Bounds(Center(b), Vector3.one * S);

        public bool IsSolidAt(Vector3 world)
        {
            var id = Get(ToBlock(world));
            return id != 0 && BlockRegistry.All[id].Solid;
        }

        /// <summary>Highest solid block in a column (block y), or int.MinValue.</summary>
        public int TopSolid(int x, int z, int fromY, int toY)
        {
            for (int y = fromY; y >= toY; y--)
            {
                var id = Get(x, y, z);
                if (id != 0 && BlockRegistry.All[id].Solid) return y;
            }
            return int.MinValue;
        }

        /// <summary>Highest light-blocking block in a column (from the height map), or -1 if unknown.</summary>
        public int HeightAt(int x, int z)
        {
            if (_cols.TryGetValue(new Vector2Int(x >> 4, z >> 4), out var col) && col.Heights != null) return col.Heights[(z & 15) * 16 + (x & 15)];
            return -1;
        }

        /// <summary>Walks the grid along a ray and returns the first targetable block and the face it entered through.</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, float maxDist, out Vector3Int hit, out Vector3Int normal)
        {
            hit = default;
            normal = default;
            if (dir.sqrMagnitude < 1e-8f) return false;
            dir.Normalize();
            Vector3 p = (origin - Origin) / S;
            float max = maxDist / S;
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            int sx = dir.x > 0 ? 1 : -1, sy = dir.y > 0 ? 1 : -1, sz = dir.z > 0 ? 1 : -1;
            float dx = dir.x != 0 ? Mathf.Abs(1f / dir.x) : float.PositiveInfinity;
            float dy = dir.y != 0 ? Mathf.Abs(1f / dir.y) : float.PositiveInfinity;
            float dz = dir.z != 0 ? Mathf.Abs(1f / dir.z) : float.PositiveInfinity;
            float tx = dir.x != 0 ? (dir.x > 0 ? x + 1 - p.x : p.x - x) * dx : float.PositiveInfinity;
            float ty = dir.y != 0 ? (dir.y > 0 ? y + 1 - p.y : p.y - y) * dy : float.PositiveInfinity;
            float tz = dir.z != 0 ? (dir.z > 0 ? z + 1 - p.z : p.z - z) * dz : float.PositiveInfinity;
            float t = 0f;
            var n = Vector3Int.zero;
            for (int guard = 0; guard < 512 && t <= max; guard++)
            {
                ushort b = Get(x, y, z);
                if (b != 0 && BlockRegistry.All[b].Targetable)
                {
                    hit = new Vector3Int(x, y, z);
                    normal = n;
                    return true;
                }
                if (tx < ty && tx < tz) { x += sx; t = tx; tx += dx; n = new Vector3Int(-sx, 0, 0); }
                else if (ty < tz) { y += sy; t = ty; ty += dy; n = new Vector3Int(0, -sy, 0); }
                else { z += sz; t = tz; tz += dz; n = new Vector3Int(0, 0, -sz); }
            }
            return false;
        }

        // Corners of each face (east, west, top, bottom, south, north), ordered so Unity sees them from outside.
        private static readonly Vector3[][] FaceCorners =
        {
            new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) },
            new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) },
            new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) },
            new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1) },
            new[] { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) },
        };

        public static Vector3 FaceCorner(int face, int corner) => FaceCorners[face][corner];

        // ---------- Streaming and meshing ----------

        private static int MaxJobs => Mathf.Clamp(Environment.ProcessorCount - 2, 1, 6);

        /// <summary>Call every frame while the world is visible.</summary>
        public void Tick(Vector3 viewer, int radius, float daylight, float budgetMs = 4f)
        {
            if (_destroyed) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _daylight = daylight;
            var vb = ToBlock(viewer);
            _viewerCol = new Vector2Int(vb.x >> 4, vb.z >> 4);

            // 1. Columns that finished generating.
            while (_genDone.TryDequeue(out var g)) AddGenerated(g);

            // 2. Finished meshes (most expensive part on this thread: uploading and colliders).
            while (sw.Elapsed.TotalMilliseconds < budgetMs && _meshDone.TryDequeue(out var m)) ApplyMesh(m);

            // 3. Ask for more work.
            if (Gen != null) RequestColumns(radius);
            ScheduleMeshes(radius);

            // 4. Forget far columns.
            if (Gen != null && Time.frameCount % 30 == 0) Unload(radius + 3);

            // 5. Day turning into night: re-tint a few chunks per frame.
            Recolor(sw, budgetMs + 1.5f);
        }

        private void RequestColumns(int radius)
        {
            if (_jobs >= MaxJobs) return;
            // Nearest missing column first.
            Vector2Int best = default;
            int bestD = int.MaxValue;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int d = dx * dx + dz * dz;
                    if (d > radius * radius || d >= bestD) continue;
                    var cc = _viewerCol + new Vector2Int(dx, dz);
                    if (_cols.TryGetValue(cc, out var col) && (col.Generated || col.Generating)) continue;
                    best = cc;
                    bestD = d;
                }
            if (bestD == int.MaxValue) return;
            var c = EnsureColumn(best);
            c.Generating = true;
            Interlocked.Increment(ref _jobs);
            var gen = Gen;
            var key = best;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var heights = new short[256];
                    var secs = gen.GenerateColumn(key.x, key.y, heights);
                    _genDone.Enqueue(new GenResult { C = key, Sections = secs, Heights = heights });
                }
                catch (Exception ex) { Plugin.Log.LogError("World generation failed: " + ex); }
                finally { Interlocked.Decrement(ref _jobs); }
            });
            // Several per frame while the area around you is still filling in.
            if (_jobs < MaxJobs) RequestColumns(radius);
        }

        private void AddGenerated(GenResult g)
        {
            if (_destroyed || !_cols.TryGetValue(g.C, out var col)) return;
            col.Generating = false;
            col.Generated = true;
            col.Heights = g.Heights;
            bool overridden = false;
            for (int sy = 0; sy < g.Sections.Length; sy++)
            {
                var cc = new Vector3Int(g.C.x, sy, g.C.y);
                var data = g.Sections[sy];
                if (_saved.TryGetValue(cc, out var saved)) { data = saved; overridden = true; }
                if (data == null) continue;
                int count = 0;
                foreach (var v in data) if (v != 0) count++;
                if (count == 0) continue;
                _chunks[cc] = new Chunk { C = cc, D = saved != null ? (ushort[])saved.Clone() : data, Count = count, Modified = saved != null };
            }
            if (overridden)
                for (int i = 0; i < 256; i++) UpdateHeight(g.C, i & 15, i >> 4);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) MarkDirty(g.C + new Vector2Int(dx, dz), false);
        }

        private bool NeighboursReady(Vector2Int cc)
        {
            if (Gen == null) return true;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!_cols.TryGetValue(cc + new Vector2Int(dx, dz), out var n) || !n.Generated) return false;
            return true;
        }

        private readonly List<Vector2Int> _scratch = new List<Vector2Int>();

        private void ScheduleMeshes(int radius)
        {
            if (_dirty.Count == 0 || _jobs >= MaxJobs) return;
            _scratch.Clear();
            foreach (var cc in _dirty) _scratch.Add(cc);
            // Columns you just changed first, then the closest.
            _scratch.Sort((a, b) =>
            {
                bool pa = _cols.TryGetValue(a, out var ca) && ca.Priority, pb = _cols.TryGetValue(b, out var cb) && cb.Priority;
                if (pa != pb) return pa ? -1 : 1;
                return (a - _viewerCol).sqrMagnitude.CompareTo((b - _viewerCol).sqrMagnitude);
            });
            foreach (var cc in _scratch)
            {
                if (_jobs >= MaxJobs) break;
                if (!_cols.TryGetValue(cc, out var col) || !col.Generated) { _dirty.Remove(cc); continue; }
                if (col.Meshing) continue;
                if (Gen != null && (cc - _viewerCol).sqrMagnitude > (radius + 1) * (radius + 1)) { _dirty.Remove(cc); continue; }
                if (!NeighboursReady(cc)) continue;
                _dirty.Remove(cc);
                col.Priority = false;
                var snap = Snapshot(cc, col);
                if (snap == null) { ClearColumnMeshes(cc); continue; }
                col.Meshing = true;
                Interlocked.Increment(ref _jobs);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _meshDone.Enqueue(Mesher.Build(snap)); }
                    catch (Exception ex) { Plugin.Log.LogError("Meshing failed: " + ex); _meshDone.Enqueue(new ColumnMeshResult { Cx = snap.Cx, Cz = snap.Cz, Version = -1, SyMin = 1, SyMax = 0 }); }
                    finally { Interlocked.Decrement(ref _jobs); }
                });
            }
        }

        private ColumnSnapshot Snapshot(Vector2Int cc, Column col)
        {
            int syMin, syMax;
            if (Gen != null) { syMin = 0; syMax = TerrainGen.Height / 16 - 1; }
            else
            {
                syMin = int.MaxValue; syMax = int.MinValue;
                foreach (var k in _chunks.Keys)
                    if (k.x == cc.x && k.z == cc.y) { syMin = Math.Min(syMin, k.y); syMax = Math.Max(syMax, k.y); }
                if (syMin == int.MaxValue) return null;
            }
            int dMin = Gen != null ? syMin : syMin - 1, dMax = Gen != null ? syMax : syMax + 1;
            int span = dMax - dMin + 1;
            var s = new ColumnSnapshot
            {
                Cx = cc.x, Cz = cc.y, Version = col.Version, SyMin = syMin, SyMax = syMax, DataSyMin = dMin, DataSyMax = dMax,
                Sections = new ushort[9 * span][], FullSky = Gen == null, S = S,
            };
            if (Gen != null) s.Heights = new short[9][];
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ni = (dz + 1) * 3 + dx + 1;
                    for (int sy = dMin; sy <= dMax; sy++)
                        if (_chunks.TryGetValue(new Vector3Int(cc.x + dx, sy, cc.y + dz), out var ch)) s.Sections[ni * span + sy - dMin] = ch.D;
                    if (Gen != null && _cols.TryGetValue(cc + new Vector2Int(dx, dz), out var nc)) s.Heights[ni] = nc.Heights;
                }
            return s;
        }

        private void ApplyMesh(ColumnMeshResult r)
        {
            var cc = new Vector2Int(r.Cx, r.Cz);
            if (!_cols.TryGetValue(cc, out var col)) return;
            col.Meshing = false;
            if (r.Version < 0) { _dirty.Add(cc); return; }
            col.MeshedVersion = r.Version;

            var present = new HashSet<int>();
            foreach (var sm in r.Sections)
            {
                present.Add(sm.Sy);
                var key = new Vector3Int(r.Cx, sm.Sy, r.Cz);
                if (!_chunks.TryGetValue(key, out var ch)) continue;
                if (ch.Go == null)
                {
                    ch.Go = new GameObject($"chunk {key.x},{key.y},{key.z}") { layer = _layer };
                    ch.Go.transform.SetParent(Root.transform, false);
                    ch.Go.transform.localPosition = (Vector3)(key * CS) * S;
                }
                ch.Opaque = Upload(ch, "o", ch.Opaque, sm.Opaque, BlockRegistry.OpaqueMat, out ch.OpaqueL);
                ch.Trans = Upload(ch, "t", ch.Trans, sm.Trans, BlockRegistry.TransparentMat, out ch.TransL);
                ch.ColoredFor = -1f;
                Recolor(ch);

                foreach (var kv in sm.Cols)
                {
                    ch.Cols.TryGetValue(kv.Key, out var part);
                    if (part.go == null)
                    {
                        var go = new GameObject("col " + kv.Key) { layer = _layer };
                        go.transform.SetParent(ch.Go.transform, false);
                        var mesh = new Mesh { name = "GM_col", indexFormat = IndexFormat.UInt32 };
                        var c = go.AddComponent<MeshCollider>();
                        go.AddComponent<GorillaSurfaceOverride>().overrideIndex = kv.Key;
                        go.AddComponent<VoxelCollider>().World = this;
                        part = (go, c, mesh);
                        ch.Cols[kv.Key] = part;
                    }
                    part.go.SetActive(true);
                    part.mesh.Clear();
                    part.mesh.SetVertices(kv.Value.V);
                    part.mesh.SetTriangles(kv.Value.T, 0, false);
                    part.mesh.RecalculateBounds();
                    part.col.sharedMesh = null;
                    part.col.sharedMesh = part.mesh;
                }
                foreach (var kv in ch.Cols)
                    if (!sm.Cols.ContainsKey(kv.Key) && kv.Value.go != null) kv.Value.go.SetActive(false);
            }
            // Sections that are now empty (everything broken out) lose their meshes.
            for (int sy = r.SyMin; sy <= r.SyMax; sy++)
                if (!present.Contains(sy) && _chunks.TryGetValue(new Vector3Int(r.Cx, sy, r.Cz), out var ch) && ch.Go != null)
                    DestroyChunkObjects(ch);
            ClearColumnMeshes(cc);
        }

        /// <summary>Drops chunks that became completely empty (remembering that, so they don't grow back).</summary>
        private void ClearColumnMeshes(Vector2Int cc)
        {
            foreach (var k in new List<Vector3Int>(_chunks.Keys))
            {
                if (k.x != cc.x || k.z != cc.y || _chunks[k].Count > 0) continue;
                if (Gen != null && _chunks[k].Modified) _saved[k] = new ushort[CS * CS * CS];
                DestroyChunkObjects(_chunks[k]);
                _chunks.Remove(k);
            }
        }

        private Mesh Upload(Chunk c, string childName, Mesh mesh, MeshData data, Material mat, out byte[] light)
        {
            light = null;
            var child = c.Go.transform.Find(childName);
            if (data.Empty)
            {
                if (child != null) child.gameObject.SetActive(false);
                return mesh;
            }
            if (child == null)
            {
                var go = new GameObject(childName) { layer = _layer };
                go.transform.SetParent(c.Go.transform, false);
                go.AddComponent<MeshFilter>();
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                child = go.transform;
            }
            child.gameObject.SetActive(true);
            if (mesh == null)
            {
                mesh = new Mesh { name = "GM_chunk", indexFormat = IndexFormat.UInt32 };
                mesh.MarkDynamic();
            }
            mesh.Clear();
            mesh.SetVertices(data.V);
            mesh.SetUVs(0, data.U);
            mesh.SetTriangles(data.T, 0, false);
            mesh.RecalculateBounds();
            light = data.L.ToArray();
            child.GetComponent<MeshFilter>().sharedMesh = mesh;
            return mesh;
        }

        // ---------- Colours (lighting) ----------

        private static readonly List<Color32> _colors = new List<Color32>();

        /// <summary>How bright a light level looks, like Minecraft's light curve with its gamma halfway.</summary>
        public static float Brightness(float level)
        {
            float f = Mathf.Clamp01(level / 15f);
            float vanilla = f / (4f - 3f * f);
            float bright = 1f - Mathf.Pow(1f - f, 3f);
            return Mathf.Lerp(0.035f, 1f, Mathf.Lerp(vanilla, bright, 0.55f));
        }

        private void Recolor(Chunk ch)
        {
            if (!BlockRegistry.VertexLighting) { ch.ColoredFor = _daylight; return; }
            Paint(ch.Opaque, ch.OpaqueL);
            Paint(ch.Trans, ch.TransL);
            ch.ColoredFor = _daylight;
        }

        private void Paint(Mesh mesh, byte[] L)
        {
            if (mesh == null || L == null) return;
            int n = L.Length / 3;
            _colors.Clear();
            float dayLevel = Mathf.Lerp(4f, 15f, _daylight);
            // Sky light is cool at night and white by day; torchlight is warm.
            var skyTint = Color.Lerp(new Color(0.62f, 0.68f, 1f), Color.white, _daylight);
            var torchTint = new Color(1f, 0.9f, 0.72f);
            for (int i = 0; i < n; i++)
            {
                float sky = L[i * 3] / 16f, blk = L[i * 3 + 1] / 16f, shade = L[i * 3 + 2] / 255f;
                float sb = Brightness(sky * dayLevel / 15f), bb = Brightness(blk);
                float r = Mathf.Max(sb * skyTint.r, bb * torchTint.r) * shade;
                float g = Mathf.Max(sb * skyTint.g, bb * torchTint.g) * shade;
                float b = Mathf.Max(sb * skyTint.b, bb * torchTint.b) * shade;
                _colors.Add(new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255));
            }
            if (mesh.vertexCount == n) mesh.SetColors(_colors);
        }

        private void Recolor(System.Diagnostics.Stopwatch sw, float budgetMs)
        {
            if (!BlockRegistry.VertexLighting) return;
            if (Mathf.Abs(_daylight - _colorDaylight) > 0.015f)
            {
                _colorDaylight = _daylight;
                _recolorQueue.Clear();
                foreach (var c in _chunks.Values) if (c.Go != null) _recolorQueue.Add(c);
                // Nearest first so the change starts around you.
                var vc = _viewerCol;
                _recolorQueue.Sort((a, b) => (new Vector2Int(a.C.x, a.C.z) - vc).sqrMagnitude.CompareTo((new Vector2Int(b.C.x, b.C.z) - vc).sqrMagnitude));
            }
            int i = 0;
            while (_recolorQueue.Count > 0 && sw.Elapsed.TotalMilliseconds < budgetMs && i < 40)
            {
                var c = _recolorQueue[_recolorQueue.Count - 1];
                _recolorQueue.RemoveAt(_recolorQueue.Count - 1);
                if (c.Go != null && Mathf.Abs(c.ColoredFor - _daylight) > 0.001f) Recolor(c);
                i++;
            }
        }

        /// <summary>Light level (0-15) at a block, for mob spawning and things you hold. Sky light uses the height map.</summary>
        public int SkyOpen(Vector3Int p)
        {
            int h = HeightAt(p.x, p.z);
            return h < 0 || p.y > h ? 15 : 0;
        }

        /// <summary>Every block matching a test inside chunks you changed (crops only grow where you planted).</summary>
        public List<Vector3Int> FindModified(Func<ushort, bool> test)
        {
            var list = new List<Vector3Int>();
            foreach (var c in _chunks.Values)
            {
                if (!c.Modified && Gen != null) continue;
                for (int i = 0; i < c.D.Length; i++)
                    if (c.D[i] != 0 && test(c.D[i]))
                        list.Add(new Vector3Int(c.C.x * CS + (i & 15), c.C.y * CS + (i >> 8), c.C.z * CS + ((i >> 4) & 15)));
            }
            return list;
        }

        // ---------- Unloading, visibility, saving ----------

        private void Unload(int keepRadius)
        {
            var drop = new List<Vector2Int>();
            foreach (var kv in _cols)
                if ((kv.Key - _viewerCol).sqrMagnitude > keepRadius * keepRadius && !kv.Value.Generating && !kv.Value.Meshing) drop.Add(kv.Key);
            if (drop.Count == 0) return;
            var dropSet = new HashSet<Vector2Int>(drop);
            foreach (var k in new List<Vector3Int>(_chunks.Keys))
            {
                if (!dropSet.Contains(new Vector2Int(k.x, k.z))) continue;
                var ch = _chunks[k];
                if (ch.Modified) _saved[k] = (ushort[])ch.D.Clone();
                DestroyChunkObjects(ch);
                _chunks.Remove(k);
            }
            foreach (var d in drop) { _cols.Remove(d); _dirty.Remove(d); }
        }

        private static void DestroyChunkObjects(Chunk c)
        {
            if (c.Go != null) UnityEngine.Object.Destroy(c.Go);
            if (c.Opaque != null) UnityEngine.Object.Destroy(c.Opaque);
            if (c.Trans != null) UnityEngine.Object.Destroy(c.Trans);
            foreach (var p in c.Cols.Values) if (p.mesh != null) UnityEngine.Object.Destroy(p.mesh);
            c.Go = null;
            c.Opaque = c.Trans = null;
            c.OpaqueL = c.TransL = null;
            c.Cols.Clear();
        }

        public void SetVisible(bool on)
        {
            if (Root != null && Root.activeSelf != on) Root.SetActive(on);
        }

        public void Clear()
        {
            foreach (var c in _chunks.Values) DestroyChunkObjects(c);
            _chunks.Clear();
            _saved.Clear();
            _dirty.Clear();
            _cols.Clear();
            Unsaved = true;
        }

        public void Destroy()
        {
            _destroyed = true;
            Clear();
            if (Root != null) UnityEngine.Object.Destroy(Root);
        }

        // GMW2 file: spawn, time of day, block-name palette, then chunks (run-length encoded), gzipped.
        // Endless worlds only store chunks you changed; the rest is regenerated from the seed.

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                using (var fs = File.Create(tmp)) Write(fs);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Unsaved = false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Saving {Id} failed: {ex.Message}");
            }
        }

        /// <summary>The same thing a save file holds, in memory (to send your world to a friend).</summary>
        public byte[] SaveBytes()
        {
            using (var ms = new MemoryStream()) { Write(ms); return ms.ToArray(); }
        }

        private void Write(Stream stream)
        {
            var data = new Dictionary<Vector3Int, ushort[]>(_saved);
            foreach (var c in _chunks.Values)
                if (Gen == null || c.Modified) data[c.C] = c.D;

            var palette = new List<string> { "air" };
            var lookup = new Dictionary<ushort, ushort> { [0] = 0 };
            foreach (var d in data.Values)
                foreach (var id in d)
                    if (!lookup.ContainsKey(id)) { lookup[id] = (ushort)palette.Count; palette.Add(BlockRegistry.All[id].Key); }

            using (var gz = new GZipStream(stream, System.IO.Compression.CompressionLevel.Fastest, true))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(0x32574D47); // "GMW2"
                w.Write(SpawnBlock.x); w.Write(SpawnBlock.y); w.Write(SpawnBlock.z);
                w.Write(TimeOfDay);
                w.Write(palette.Count);
                foreach (var name in palette) w.Write(name);
                w.Write(data.Count);
                foreach (var kv in data)
                {
                    w.Write(kv.Key.x); w.Write(kv.Key.y); w.Write(kv.Key.z);
                    var D = kv.Value;
                    int i = 0;
                    while (i < D.Length)
                    {
                        ushort v = D[i];
                        int run = 1;
                        while (i + run < D.Length && D[i + run] == v && run < 65535) run++;
                        w.Write((ushort)run);
                        w.Write(lookup[v]);
                        i += run;
                    }
                }
            }
        }

        public bool Load(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var fs = File.OpenRead(path)) return Read(fs);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Loading {path} failed: {ex.Message}");
                Clear();
                return false;
            }
        }

        /// <summary>Loads a world a friend sent. It came over the network, so it's checked carefully.</summary>
        public bool LoadBytes(byte[] bytes)
        {
            try
            {
                using (var ms = new MemoryStream(bytes, false)) return Read(ms);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"A shared world couldn't be read: {ex.Message}");
                Clear();
                return false;
            }
        }

        private bool Read(Stream stream)
        {
            using (var gz = new GZipStream(stream, CompressionMode.Decompress, true))
            using (var r = new BinaryReader(gz))
            {
                int magic = r.ReadInt32();
                if (magic != 0x32574D47 && magic != 0x31574D47) return false;
                SpawnBlock = new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                if (magic == 0x32574D47) TimeOfDay = r.ReadSingle();
                else if (Gen != null) return false; // old island saves don't fit the endless world
                int pc = r.ReadInt32();
                if (pc < 1 || pc > 65536) return false;
                var map = new ushort[pc];
                for (int i = 0; i < pc; i++)
                {
                    string key = r.ReadString();
                    var def = key == "air" ? null : BlockRegistry.Get(key);
                    map[i] = def != null ? def.Index : (ushort)0;
                }
                int chunks = r.ReadInt32();
                if (chunks < 0 || chunks > 200000) return false;
                for (int n = 0; n < chunks; n++)
                {
                    var c = new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    var D = new ushort[CS * CS * CS];
                    int i = 0;
                    while (i < D.Length)
                    {
                        int run = r.ReadUInt16();
                        if (run == 0) throw new InvalidDataException("empty run");
                        ushort v = map[r.ReadUInt16()];
                        for (int k = 0; k < run && i < D.Length; k++, i++) D[i] = v;
                    }
                    if (Gen != null) { _saved[c] = D; continue; }
                    int count = 0;
                    foreach (var v in D) if (v != 0) count++;
                    if (count == 0) continue;
                    _chunks[c] = new Chunk { C = c, D = D, Count = count, Modified = true };
                    EnsureColumn(new Vector2Int(c.x, c.z));
                    MarkDirty(new Vector2Int(c.x, c.z), false);
                }
            }
            Unsaved = false;
            return true;
        }
    }
}
