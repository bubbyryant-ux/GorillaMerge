using System;
using System.Collections.Generic;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>Block data a background thread needs to mesh one 16-wide column of chunks.</summary>
    internal sealed class ColumnSnapshot
    {
        public int Cx, Cz, Version;
        public int SyMin, SyMax;            // sections of the centre column to mesh
        public int DataSyMin, DataSyMax;    // sections present in the data (one more above and below)
        public ushort[][] Sections;         // [((dz+1)*3 + dx+1) * span + (sy - DataSyMin)], null = air
        public short[][] Heights;           // [(dz+1)*3 + dx+1] highest light-blocking block per x,z; null = always full sky
        public bool FullSky;
        public float S;
    }

    internal sealed class MeshData
    {
        public readonly List<Vector3> V = new List<Vector3>();
        public readonly List<Vector2> U = new List<Vector2>();
        public readonly List<int> T = new List<int>();
        public readonly List<byte> L = new List<byte>(); // per vertex: sky*16, block*16, shade*255
        public bool Empty => T.Count == 0;
    }

    internal sealed class SectionMesh
    {
        public int Sy;
        public readonly MeshData Opaque = new MeshData(), Trans = new MeshData();
        public readonly Dictionary<int, MeshData> Cols = new Dictionary<int, MeshData>();
    }

    internal sealed class ColumnMeshResult
    {
        public int Cx, Cz, Version;
        public readonly List<SectionMesh> Sections = new List<SectionMesh>();
        public int SyMin, SyMax;
    }

    /// <summary>
    /// Turns blocks into meshes with Minecraft-style lighting. Runs on worker threads, touches no Unity objects.
    /// Light: sunlight falls straight down at full strength and spreads sideways and into caves losing 1 per
    /// block; torches and glowstone spread the same way. Each corner of each face averages the light of the
    /// 4 cells touching it and gets darker next to walls (ambient occlusion), like Minecraft's smooth lighting.
    /// </summary>
    internal static class Mesher
    {
        private const int B = 15;           // light border around the centre column
        private const int W = 16 + 2 * B;   // light grid width

        private static readonly Vector3Int[] Dirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0), new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };
        private static readonly float[] FaceShade = { 0.6f, 0.6f, 1f, 0.5f, 0.8f, 0.8f };
        private static readonly float[] AoFactor = { 1f, 0.8f, 0.65f, 0.5f };

        [ThreadStatic] private static byte[] _sky, _blk;
        [ThreadStatic] private static int[] _queue;

        public static ColumnMeshResult Build(ColumnSnapshot s)
        {
            var r = new ColumnMeshResult { Cx = s.Cx, Cz = s.Cz, Version = s.Version, SyMin = s.SyMin, SyMax = s.SyMax };
            int span = s.DataSyMax - s.DataSyMin + 1;
            int yBase = s.DataSyMin * 16, yCount = span * 16;

            ushort Get(int lx, int y, int lz)
            {
                int dx = lx < 0 ? -1 : lx > 15 ? 1 : 0, dz = lz < 0 ? -1 : lz > 15 ? 1 : 0;
                if (lx < -16 || lx > 31 || lz < -16 || lz > 31) return 0;
                int sy = (y >> 4) - s.DataSyMin;
                if (sy < 0 || sy >= span) return 0;
                var sec = s.Sections[((dz + 1) * 3 + dx + 1) * span + sy];
                return sec == null ? (ushort)0 : sec[((y & 15) * 16 + (lz - dz * 16)) * 16 + (lx - dx * 16)];
            }

            // ---------- light ----------
            int lightTop = yBase + yCount - 1;
            bool lit = BlockRegistry.VertexLighting;
            if (lit)
            {
                int n = W * W * yCount;
                if (_sky == null || _sky.Length < n) { _sky = new byte[n]; _blk = new byte[n]; _queue = new int[n]; }
                Array.Clear(_blk, 0, n);
                var blocks = BlockRegistry.BlocksLightT;
                var leaves = BlockRegistry.LeavesT;
                int LI(int x, int y, int z) => ((y - yBase) * W + (z + B)) * W + (x + B);

                if (s.FullSky) for (int i = 0; i < n; i++) _sky[i] = 15;
                else
                {
                    // Sunlight straight down from the top of the data.
                    for (int z = -B; z < 16 + B; z++)
                        for (int x = -B; x < 16 + B; x++)
                        {
                            int level = 15;
                            for (int y = lightTop; y >= yBase; y--)
                            {
                                ushort id = Get(x, y, z);
                                if (id != 0)
                                {
                                    if (blocks[id]) level = 0;
                                    else if (leaves[id]) level = Math.Max(0, level - 1);
                                }
                                _sky[LI(x, y, z)] = (byte)level;
                            }
                        }
                    Spread(_sky, blocks, leaves, Get, yBase, yCount, seedAll: true);
                }

                // Torches, glowstone, lava.
                var emit = BlockRegistry.EmitT;
                bool any = false;
                for (int y = yBase; y < yBase + yCount; y++)
                    for (int z = -B; z < 16 + B; z++)
                        for (int x = -B; x < 16 + B; x++)
                        {
                            ushort id = Get(x, y, z);
                            if (id != 0 && emit[id] > 0) { _blk[LI(x, y, z)] = emit[id]; any = true; }
                        }
                if (any) Spread(_blk, blocks, leaves, Get, yBase, yCount, seedAll: false);
            }

            float LightSky(int x, int y, int z)
            {
                if (!lit) return 15;
                if (y > lightTop) return 15;
                if (y < yBase || x < -B || x >= 16 + B || z < -B || z >= 16 + B) return 0;
                return _sky[((y - yBase) * W + (z + B)) * W + (x + B)];
            }
            float LightBlk(int x, int y, int z)
            {
                if (!lit || y > lightTop || y < yBase || x < -B || x >= 16 + B || z < -B || z >= 16 + B) return 0;
                return _blk[((y - yBase) * W + (z + B)) * W + (x + B)];
            }

            // ---------- meshing ----------
            var opaque = BlockRegistry.OpaqueT;
            var solid = BlockRegistry.SolidT;
            var kind = BlockRegistry.KindT;
            var surf = BlockRegistry.SurfT;
            var uvs = lit ? BlockRegistry.UvT : BlockRegistry.UvShadedT;
            float S = s.S;

            for (int sy = s.SyMin; sy <= s.SyMax; sy++)
            {
                int dataSy = sy - s.DataSyMin;
                var sec = s.Sections[4 * span + dataSy];
                if (sec == null) continue;
                var sm = new SectionMesh { Sy = sy };
                int y0 = sy * 16;
                for (int ly = 0; ly < 16; ly++)
                    for (int z = 0; z < 16; z++)
                        for (int x = 0; x < 16; x++)
                        {
                            ushort id = sec[(ly * 16 + z) * 16 + x];
                            if (id == 0) continue;
                            int y = y0 + ly;
                            var k = (RenderKind)kind[id];

                            if (k == RenderKind.Plant)
                            {
                                Cross(sm.Trans, x, ly, z, uvs[id * 6], S, LightSky(x, y, z), LightBlk(x, y, z));
                                continue;
                            }
                            if (k == RenderKind.Torch)
                            {
                                Torch(sm.Opaque, x, ly, z, uvs[id * 6], S, LightSky(x, y, z), Math.Max(LightBlk(x, y, z), 14));
                                continue;
                            }

                            bool liquid = k == RenderKind.Water || k == RenderKind.Lava;
                            var buf = opaque[id] || k == RenderKind.Lava ? sm.Opaque : sm.Trans;
                            float topCut = liquid && Get(x, y + 1, z) != id ? 0.125f : 0f;
                            for (int f = 0; f < 6; f++)
                            {
                                var d = Dirs[f];
                                ushort nid = Get(x + d.x, y + d.y, z + d.z);
                                bool visible = nid == 0 || (!opaque[nid] && (opaque[id] || nid != id));
                                if (liquid && nid != 0 && kind[nid] != (byte)RenderKind.Water && kind[nid] != (byte)RenderKind.Lava && !opaque[nid]) visible = true;
                                if (visible) Face(buf, Get, opaque, x, y, z, ly, f, uvs[id * 6 + f], S, lit, topCut, LightSky, LightBlk);

                                if (solid[id] && (nid == 0 || !solid[nid]))
                                {
                                    if (!sm.Cols.TryGetValue(surf[id], out var cb)) sm.Cols[surf[id]] = cb = new MeshData();
                                    CollisionFace(cb, x, ly, z, f, S);
                                }
                            }
                        }
                if (!sm.Opaque.Empty || !sm.Trans.Empty || sm.Cols.Count > 0) r.Sections.Add(sm);
            }
            return r;
        }

        private static void Spread(byte[] light, bool[] blocks, bool[] leaves, Func<int, int, int, ushort> get, int yBase, int yCount, bool seedAll)
        {
            int n = W * W * yCount, head = 0, tail = 0;
            var q = _queue;
            // Seed with every lit cell that could light a darker neighbour.
            for (int i = 0; i < n; i++)
            {
                byte l = light[i];
                if (l < 2) continue;
                int x = i % W, rest = i / W, z = rest % W, y = rest / W;
                bool edge = false;
                if (x > 0 && light[i - 1] < l - 1) edge = true;
                else if (x < W - 1 && light[i + 1] < l - 1) edge = true;
                else if (z > 0 && light[i - W] < l - 1) edge = true;
                else if (z < W - 1 && light[i + W] < l - 1) edge = true;
                else if (y > 0 && light[i - W * W] < l - 1) edge = true;
                else if (y < yCount - 1 && light[i + W * W] < l - 1) edge = true;
                if (edge) q[tail++] = i;
            }
            while (head < tail)
            {
                int i = q[head++];
                int l = light[i];
                if (l < 2) continue;
                int x = i % W, rest = i / W, z = rest % W, y = rest / W;
                for (int dir = 0; dir < 6; dir++)
                {
                    int nx = x, ny = y, nz = z;
                    switch (dir)
                    {
                        case 0: nx++; break;
                        case 1: nx--; break;
                        case 2: ny++; break;
                        case 3: ny--; break;
                        case 4: nz++; break;
                        default: nz--; break;
                    }
                    if (nx < 0 || nx >= W || nz < 0 || nz >= W || ny < 0 || ny >= yCount) continue;
                    ushort id = get(nx - B, ny + yBase, nz - B);
                    if (id != 0 && blocks[id]) continue;
                    int nl = l - 1 - (id != 0 && leaves[id] ? 1 : 0);
                    int ni = (ny * W + nz) * W + nx;
                    if (nl <= light[ni]) continue;
                    light[ni] = (byte)nl;
                    if (tail < q.Length) q[tail++] = ni;
                }
            }
        }

        private static readonly Vector3[][] Corners =
        {
            new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) },
            new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) },
            new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) },
            new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1) },
            new[] { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) },
        };

        private static void Face(MeshData m, Func<int, int, int, ushort> get, bool[] opaque, int x, int y, int z, int ly, int f, Vector4 uv,
            float S, bool lit, float topCut, Func<int, int, int, float> sky, Func<int, int, int, float> blk)
        {
            var d = Dirs[f];
            int start = m.V.Count;
            var c4 = Corners[f];
            int ao0 = 0, ao1 = 0, ao2 = 0, ao3 = 0;
            for (int i = 0; i < 4; i++)
            {
                var k = c4[i];
                float vy = k.y;
                if (topCut > 0 && k.y > 0.5f) vy = 1f - topCut;
                m.V.Add(new Vector3((x + k.x) * S, (ly + vy) * S, (z + k.z) * S));

                byte skyB = 240, blkB = 0, shade = (byte)(FaceShade[f] * 255);
                if (lit)
                {
                    // The two directions along the face that point toward this corner.
                    int fx = x + d.x, fy = y + d.y, fz = z + d.z;
                    int ax, ay, az, bx, by, bz;
                    if (d.x != 0) { ax = 0; ay = k.y > 0.5f ? 1 : -1; az = 0; bx = 0; by = 0; bz = k.z > 0.5f ? 1 : -1; }
                    else if (d.y != 0) { ax = k.x > 0.5f ? 1 : -1; ay = 0; az = 0; bx = 0; by = 0; bz = k.z > 0.5f ? 1 : -1; }
                    else { ax = k.x > 0.5f ? 1 : -1; ay = 0; az = 0; bx = 0; by = k.y > 0.5f ? 1 : -1; bz = 0; }
                    bool s1 = opaque[get(fx + ax, fy + ay, fz + az)];
                    bool s2 = opaque[get(fx + bx, fy + by, fz + bz)];
                    bool cr = opaque[get(fx + ax + bx, fy + ay + by, fz + az + bz)];
                    int ao = s1 && s2 ? 3 : (s1 ? 1 : 0) + (s2 ? 1 : 0) + (cr ? 1 : 0);
                    float ss = sky(fx, fy, fz), bs = blk(fx, fy, fz);
                    int cnt = 1;
                    if (!s1) { ss += sky(fx + ax, fy + ay, fz + az); bs += blk(fx + ax, fy + ay, fz + az); cnt++; }
                    if (!s2) { ss += sky(fx + bx, fy + by, fz + bz); bs += blk(fx + bx, fy + by, fz + bz); cnt++; }
                    if (!cr && !(s1 && s2)) { ss += sky(fx + ax + bx, fy + ay + by, fz + az + bz); bs += blk(fx + ax + bx, fy + ay + by, fz + az + bz); cnt++; }
                    skyB = (byte)Math.Min(240, ss / cnt * 16f);
                    blkB = (byte)Math.Min(240, bs / cnt * 16f);
                    shade = (byte)(FaceShade[f] * AoFactor[ao] * 255);
                    switch (i) { case 0: ao0 = ao; break; case 1: ao1 = ao; break; case 2: ao2 = ao; break; default: ao3 = ao; break; }
                }
                m.L.Add(skyB); m.L.Add(blkB); m.L.Add(shade);
            }
            float vTop = topCut > 0 ? uv.y + (uv.w - uv.y) * (1f - topCut) : uv.w;
            m.U.Add(new Vector2(uv.x, uv.y));
            m.U.Add(new Vector2(uv.x, f == 2 || f == 3 ? uv.w : vTop));
            m.U.Add(new Vector2(uv.z, f == 2 || f == 3 ? uv.w : vTop));
            m.U.Add(new Vector2(uv.z, uv.y));
            // Split the quad along the brighter diagonal so corner shadows don't look stretched.
            if (ao0 + ao2 > ao1 + ao3)
            {
                m.T.Add(start + 1); m.T.Add(start + 2); m.T.Add(start + 3);
                m.T.Add(start + 1); m.T.Add(start + 3); m.T.Add(start);
            }
            else
            {
                m.T.Add(start); m.T.Add(start + 1); m.T.Add(start + 2);
                m.T.Add(start); m.T.Add(start + 2); m.T.Add(start + 3);
            }
        }

        private static void CollisionFace(MeshData m, int x, int ly, int z, int f, float S)
        {
            int start = m.V.Count;
            foreach (var k in Corners[f]) m.V.Add(new Vector3((x + k.x) * S, (ly + k.y) * S, (z + k.z) * S));
            m.T.Add(start); m.T.Add(start + 1); m.T.Add(start + 2);
            m.T.Add(start); m.T.Add(start + 2); m.T.Add(start + 3);
        }

        private static void Quad(MeshData m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, byte sky, byte blk, byte shade)
        {
            int start = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.V.Add(d);
            m.U.Add(ua); m.U.Add(ub); m.U.Add(uc); m.U.Add(ud);
            for (int i = 0; i < 4; i++) { m.L.Add(sky); m.L.Add(blk); m.L.Add(shade); }
            m.T.Add(start); m.T.Add(start + 1); m.T.Add(start + 2);
            m.T.Add(start); m.T.Add(start + 2); m.T.Add(start + 3);
        }

        // Grass and flowers: two crossed pictures, drawn from both sides.
        private static void Cross(MeshData m, int x, int ly, int z, Vector4 uv, float S, float sky, float blk)
        {
            byte sb = (byte)Math.Min(240, sky * 16), bb = (byte)Math.Min(240, blk * 16);
            float o = 0.15f;
            var p = new Vector3(x, ly, z);
            Vector3 A(float ax, float ay, float az) => (p + new Vector3(ax, ay, az)) * S;
            Vector2 u0 = new Vector2(uv.x, uv.y), u1 = new Vector2(uv.x, uv.w), u2 = new Vector2(uv.z, uv.w), u3 = new Vector2(uv.z, uv.y);
            Quad(m, A(o, 0, o), A(o, 1, o), A(1 - o, 1, 1 - o), A(1 - o, 0, 1 - o), u0, u1, u2, u3, sb, bb, 255);
            Quad(m, A(1 - o, 0, 1 - o), A(1 - o, 1, 1 - o), A(o, 1, o), A(o, 0, o), u0, u1, u2, u3, sb, bb, 255);
            Quad(m, A(o, 0, 1 - o), A(o, 1, 1 - o), A(1 - o, 1, o), A(1 - o, 0, o), u0, u1, u2, u3, sb, bb, 255);
            Quad(m, A(1 - o, 0, o), A(1 - o, 1, o), A(o, 1, 1 - o), A(o, 0, 1 - o), u0, u1, u2, u3, sb, bb, 255);
        }

        // Torch: a thin 2x10 pixel stick from the middle of the torch picture.
        private static void Torch(MeshData m, int x, int ly, int z, Vector4 uv, float S, float sky, float blk)
        {
            byte sb = (byte)Math.Min(240, sky * 16), bb = (byte)Math.Min(240, blk * 16);
            float uw = uv.z - uv.x, vh = uv.w - uv.y;
            float u0 = uv.x + uw * 7 / 16f, u1 = uv.x + uw * 9 / 16f, v0 = uv.y, v1 = uv.y + vh * 10 / 16f;
            float a = 7 / 16f, b = 9 / 16f, h = 10 / 16f;
            var p = new Vector3(x, ly, z);
            Vector3 P(float px, float py, float pz) => (p + new Vector3(px, py, pz)) * S;
            Vector2 U(float uu, float vv) => new Vector2(uu, vv);
            Quad(m, P(b, 0, a), P(b, h, a), P(b, h, b), P(b, 0, b), U(u0, v0), U(u0, v1), U(u1, v1), U(u1, v0), sb, bb, 200);
            Quad(m, P(a, 0, b), P(a, h, b), P(a, h, a), P(a, 0, a), U(u0, v0), U(u0, v1), U(u1, v1), U(u1, v0), sb, bb, 200);
            Quad(m, P(b, 0, b), P(b, h, b), P(a, h, b), P(a, 0, b), U(u0, v0), U(u0, v1), U(u1, v1), U(u1, v0), sb, bb, 230);
            Quad(m, P(a, 0, a), P(a, h, a), P(b, h, a), P(b, 0, a), U(u0, v0), U(u0, v1), U(u1, v1), U(u1, v0), sb, bb, 230);
            float tv0 = uv.y + vh * 8 / 16f, tv1 = uv.y + vh * 10 / 16f;
            Quad(m, P(a, h, a), P(a, h, b), P(b, h, b), P(b, h, a), U(u0, tv0), U(u0, tv1), U(u1, tv1), U(u1, tv0), sb, bb, 255);
        }
    }
}
