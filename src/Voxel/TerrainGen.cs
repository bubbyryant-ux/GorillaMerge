using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// Endless Minecraft-style terrain, one 16x16 column at a time. Everything here is pure maths on
    /// the seed, so columns can be made on background threads and always come out the same.
    /// Biomes: ocean, beach, plains, forest, birch forest, taiga, snowy plains, desert, mountains.
    /// Which blocks are used comes from the pack's "world" section, so other games' packs work too.
    /// </summary>
    public sealed class TerrainGen
    {
        public const int Height = 128;
        public readonly int Sea;
        public readonly Vector3Int Spawn;
        public readonly Vector3Int PortalBase;

        private readonly Noise _cont, _hills, _temp, _humid, _erosion, _cave1, _cave2, _cave3, _detail;
        private readonly int _seed;
        public int Seed => _seed;
        private readonly bool _biomes;

        // Block ids
        private readonly ushort _grass, _dirt, _stone, _water, _sand, _gravel, _sandstone, _bedrock, _deepslate,
            _snowGrass, _snow, _lava, _oakLog, _oakLeaves, _birchLog, _birchLeaves, _spruceLog, _spruceLeaves,
            _cactus, _shortGrass, _obsidian, _portal, _podzol;
        private readonly ushort[] _flowers;
        private readonly List<(ushort id, ushort deep, float chance, int minY, int maxY, int size)> _ores = new List<(ushort, ushort, float, int, int, int)>();

        private enum Biome { Ocean, Beach, Plains, Forest, BirchForest, Taiga, SnowyPlains, Desert, Mountains }

        public TerrainGen(Pack pack, int seed)
        {
            _seed = seed;
            _cont = new Noise(seed);
            _hills = new Noise(seed + 1);
            _temp = new Noise(seed + 2);
            _humid = new Noise(seed + 3);
            _erosion = new Noise(seed + 4);
            _cave1 = new Noise(seed + 5);
            _cave2 = new Noise(seed + 6);
            _cave3 = new Noise(seed + 7);
            _detail = new Noise(seed + 8);

            var g = pack.Gen ?? new JObject();
            ushort B(string key, string fallback) => pack.BlockIndex((string)g[key] ?? fallback);
            Sea = (int?)g["seaLevel"] ?? 62;
            _biomes = (bool?)g["biomes"] ?? true;
            _grass = B("surface", "grass_block");
            _dirt = B("under", "dirt");
            _stone = B("stone", "stone");
            _water = B("water", "water");
            _sand = B("beach", "sand");
            _gravel = B("lakeBed", "gravel");
            _sandstone = B("sandstone", "sandstone");
            _bedrock = B("bedrock", "bedrock");
            _deepslate = B("deepslate", "deepslate");
            _snowGrass = B("snowyGrass", "snowy_grass_block");
            _snow = B("snow", "snow");
            _lava = B("lava", "lava");
            _podzol = B("podzol", "podzol");
            _cactus = B("cactus", "cactus");
            _shortGrass = B("shortGrass", "short_grass");
            _obsidian = B("portalFrame", "obsidian");
            _portal = B("portal", "nether_portal");
            _oakLog = B("oakLog", "oak_log");
            _oakLeaves = B("oakLeaves", "oak_leaves");
            _birchLog = B("birchLog", "birch_log");
            _birchLeaves = B("birchLeaves", "birch_leaves");
            _spruceLog = B("spruceLog", "spruce_log");
            _spruceLeaves = B("spruceLeaves", "spruce_leaves");
            // Generic packs only name one tree; use it everywhere.
            if (g["trees"] is JArray trees && trees.Count > 0)
            {
                ushort log = pack.BlockIndex((string)trees[0]["log"]), leaves = pack.BlockIndex((string)trees[0]["leaves"]);
                if (_oakLog == 0) { _oakLog = log; _oakLeaves = leaves; }
            }
            if (_birchLog == 0) { _birchLog = _oakLog; _birchLeaves = _oakLeaves; }
            if (_spruceLog == 0) { _spruceLog = _oakLog; _spruceLeaves = _oakLeaves; }
            if (_snowGrass == 0) _snowGrass = _grass;
            if (_deepslate == 0) _deepslate = _stone;
            if (_sandstone == 0) _sandstone = _sand != 0 ? _sand : _stone;
            if (_podzol == 0) _podzol = _grass;

            var flowers = new List<ushort>();
            if (g["flowers"] is JArray fl) foreach (var f in fl) { var id = pack.BlockIndex((string)f); if (id != 0) flowers.Add(id); }
            _flowers = flowers.ToArray();

            if (g["ores"] is JArray ores)
                foreach (var o in ores)
                {
                    ushort id = pack.BlockIndex((string)o["block"]);
                    if (id == 0) continue;
                    _ores.Add((id, pack.BlockIndex((string)o["deep"]) is ushort d && d != 0 ? d : id,
                        (float?)o["chance"] ?? 0.01f, (int?)o["minY"] ?? 1, (int?)o["maxY"] ?? Height, (int?)o["size"] ?? 2));
                }

            Spawn = FindSpawn();
            PortalBase = new Vector3Int(Spawn.x + 4, 0, Spawn.z - 2);
            int pg = 0;
            for (int dx = 0; dx < 4; dx++) pg = Math.Max(pg, SurfaceHeight(PortalBase.x + dx, PortalBase.z));
            PortalBase.y = Math.Max(pg, Sea) + 1;
        }

        // ---------------- height and biome (pure functions of x,z) ----------------

        private void Climate(int x, int z, out double cont, out double temp, out double humid, out double mount)
        {
            cont = _cont.Fbm(x / 700.0, z / 700.0, 4);
            temp = _temp.Fbm(x / 520.0, z / 520.0, 2);
            humid = _humid.Fbm(x / 430.0, z / 430.0, 2);
            mount = _erosion.Fbm(x / 340.0, z / 340.0, 3);
        }

        private static double Smooth(double a, double b, double v)
        {
            double t = Math.Max(0, Math.Min(1, (v - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        public int SurfaceHeight(int x, int z)
        {
            Climate(x, z, out var cont, out _, out var humid, out var mount);
            double land = Smooth(-0.22, -0.04, cont);
            double hills = _hills.Fbm(x / 95.0, z / 95.0, 4);
            double detail = _detail.Fbm(x / 24.0, z / 24.0, 2);
            double amp = 6 + 6 * Smooth(0.0, 0.4, humid) + 38 * Smooth(0.18, 0.5, mount);
            double landH = Sea + 3 + cont * 10 + hills * amp + detail * 2 + 26 * Smooth(0.25, 0.6, mount) * Smooth(-0.1, 0.4, hills + 0.2);
            double oceanH = Sea - 12 + cont * 12 + detail * 2;
            double h = oceanH + (landH - oceanH) * land;
            return Math.Max(6, Math.Min(Height - 8, (int)Math.Round(h)));
        }

        private Biome BiomeAt(int x, int z, int h)
        {
            Climate(x, z, out var cont, out var temp, out var humid, out var mount);
            if (h < Sea - 1) return Biome.Ocean;
            if (!_biomes) return Biome.Plains;
            if (mount > 0.3 && h > Sea + 22) return Biome.Mountains;
            if (temp > 0.28 && humid < 0.05) return Biome.Desert;
            if (h <= Sea + 1) return Biome.Beach;
            if (temp < -0.3) return humid > 0 ? Biome.Taiga : Biome.SnowyPlains;
            if (humid > 0.18) return temp > 0.12 ? Biome.BirchForest : Biome.Forest;
            return Biome.Plains;
        }

        private Vector3Int FindSpawn()
        {
            // Spiral out from 0,0 to the first grassy land.
            for (int r = 0; r < 4000; r += 8)
                for (int a = 0; a < 16; a++)
                {
                    int x = (int)(Math.Cos(a * Math.PI / 8) * r), z = (int)(Math.Sin(a * Math.PI / 8) * r);
                    int h = SurfaceHeight(x, z);
                    var b = BiomeAt(x, z, h);
                    if (h > Sea + 1 && b != Biome.Ocean && b != Biome.Mountains && b != Biome.Desert) return new Vector3Int(x, h + 1, z);
                }
            return new Vector3Int(0, SurfaceHeight(0, 0) + 1, 0);
        }

        // ---------------- columns ----------------

        private static int Idx(int x, int y, int z) => (y * 16 + z) * 16 + x;

        /// <summary>
        /// Fills 8 sections (16x16x16 each, y 0..127) for chunk column cx,cz. Sections left null are all air.
        /// heights gets the highest solid block per x,z. Safe to call from any thread.
        /// </summary>
        public ushort[][] GenerateColumn(int cx, int cz, short[] heights)
        {
            var sec = new ushort[Height / 16][];
            int x0 = cx * 16, z0 = cz * 16;
            void Put(int lx, int y, int lz, ushort id)
            {
                if (y < 0 || y >= Height || (uint)lx > 15 || (uint)lz > 15) return;
                var s = sec[y >> 4] ?? (sec[y >> 4] = new ushort[4096]);
                s[Idx(lx, y & 15, lz)] = id;
            }
            ushort GetL(int lx, int y, int lz)
            {
                if (y < 0 || y >= Height) return 0;
                var s = sec[y >> 4];
                return s == null ? (ushort)0 : s[Idx(lx, y & 15, lz)];
            }

            var surf = new int[256];
            var biome = new Biome[256];
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int x = x0 + lx, z = z0 + lz;
                    int h = SurfaceHeight(x, z);
                    var b = BiomeAt(x, z, h);
                    surf[lz * 16 + lx] = h;
                    biome[lz * 16 + lx] = b;
                    int deepLine = 14 + (int)(Noise.Hash(x, 0, z, _seed) * 4);
                    for (int y = 0; y <= h; y++)
                    {
                        ushort id;
                        if (y == 0 || (y < 4 && Noise.Hash(x, y, z, _seed + 1) < 0.5)) id = _bedrock;
                        else if (y >= h - 3)
                        {
                            bool top = y == h;
                            switch (b)
                            {
                                case Biome.Ocean: id = h < Sea - 6 ? _gravel : _sand; break;
                                case Biome.Beach: id = _sand; break;
                                case Biome.Desert: id = y >= h - 2 ? _sand : _sandstone; break;
                                case Biome.Mountains:
                                    id = h > 100 ? (top ? _snow : _stone) : (top ? _grass : (h > 88 ? _stone : _dirt));
                                    break;
                                case Biome.SnowyPlains: id = top ? _snowGrass : _dirt; break;
                                case Biome.Taiga: id = top ? (Noise.Hash(x, h, z, _seed + 2) < 0.25 ? _podzol : _grass) : _dirt; break;
                                default: id = top ? _grass : _dirt; break;
                            }
                        }
                        else id = y < deepLine ? _deepslate : _stone;
                        Put(lx, y, lz, id);
                    }
                    for (int y = h + 1; y <= Sea; y++) Put(lx, y, lz, _water);
                }

            // Caves (cheese caves and long tunnels), lava lakes at the bottom, then ores.
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int x = x0 + lx, z = z0 + lz, h = surf[lz * 16 + lx];
                    bool wet = h <= Sea + 1;
                    int top = wet ? Math.Min(h - 5, Sea - 8) : h + 1;
                    for (int y = 5; y < top; y++)
                    {
                        double c = _cave1.Get(x / 42.0, y / 22.0, z / 42.0);
                        bool carve = c > 0.42 && y < h - 6;
                        if (!carve)
                        {
                            double a = _cave2.Get(x / 34.0, y / 26.0, z / 34.0), b2 = _cave3.Get(x / 34.0, y / 26.0, z / 34.0);
                            carve = Math.Abs(a) < 0.055 && Math.Abs(b2) < 0.055;
                        }
                        if (!carve) continue;
                        ushort cur = GetL(lx, y, lz);
                        if (cur == _bedrock || cur == _water) continue;
                        Put(lx, y, lz, y <= 10 ? _lava : (ushort)0);
                    }
                    // Grass above a cave opening would float; dirt is fine.
                    for (int y = 1; y < h - 1; y++)
                    {
                        ushort cur = GetL(lx, y, lz);
                        if (cur != _stone && cur != _deepslate) continue;
                        foreach (var ore in _ores)
                        {
                            if (y < ore.minY || y > ore.maxY) continue;
                            int s = Math.Max(1, ore.size);
                            if (Noise.Hash(x / s - (x < 0 ? 1 : 0), y / s, z / s - (z < 0 ? 1 : 0), _seed + ore.id * 31) >= ore.chance * s * s) continue;
                            if (Noise.Hash(x, y, z, _seed + ore.id) > 0.7) continue;
                            Put(lx, y, lz, cur == _deepslate ? ore.deep : ore.id);
                            break;
                        }
                    }
                }

            // Plants on top.
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int x = x0 + lx, z = z0 + lz, h = surf[lz * 16 + lx];
                    ushort top = GetL(lx, h, lz);
                    if (top != _grass || GetL(lx, h + 1, lz) != 0) continue;
                    var b = biome[lz * 16 + lx];
                    double r = Noise.Hash(x, h, z, _seed + 9);
                    double grassChance = b == Biome.Plains ? 0.3 : b == Biome.Forest || b == Biome.BirchForest ? 0.18 : b == Biome.Taiga ? 0.12 : 0.1;
                    if (_flowers.Length > 0 && r < (b == Biome.Plains ? 0.025 : 0.012))
                        Put(lx, h + 1, lz, _flowers[(int)(Noise.Hash(x, 1, z, _seed) * _flowers.Length) % _flowers.Length]);
                    else if (_shortGrass != 0 && r < grassChance) Put(lx, h + 1, lz, _shortGrass);
                }

            // Trees: one possible tree per 5x5 cell, so trees from neighbouring columns reach in here too.
            for (int gz = (z0 - 3) / 5 - 1; gz <= (z0 + 19) / 5 + 1; gz++)
                for (int gx = (x0 - 3) / 5 - 1; gx <= (x0 + 19) / 5 + 1; gx++)
                {
                    int tx = gx * 5 + (int)(Noise.Hash(gx, 7, gz, _seed) * 5);
                    int tz = gz * 5 + (int)(Noise.Hash(gx, 8, gz, _seed) * 5);
                    if (tx < x0 - 3 || tx > x0 + 18 || tz < z0 - 3 || tz > z0 + 18) continue;
                    int th = SurfaceHeight(tx, tz);
                    var b = BiomeAt(tx, tz, th);
                    double density = b switch
                    {
                        Biome.Forest => 0.75, Biome.BirchForest => 0.75, Biome.Taiga => 0.6, Biome.Plains => 0.06,
                        Biome.SnowyPlains => 0.05, Biome.Desert => 0.12, Biome.Mountains => 0.08, _ => 0,
                    };
                    if (th <= Sea || Noise.Hash(gx, 9, gz, _seed) >= density) continue;
                    if (Math.Abs(tx - Spawn.x) < 3 && Math.Abs(tz - Spawn.z) < 3) continue;
                    double kind = Noise.Hash(gx, 10, gz, _seed);
                    int lx = tx - x0, lz = tz - z0;
                    switch (b)
                    {
                        case Biome.Desert:
                            if (_cactus == 0) break;
                            int ch = 1 + (int)(kind * 3);
                            for (int y = 1; y <= ch; y++) Put(lx, th + y, lz, _cactus);
                            break;
                        case Biome.Taiga:
                        case Biome.SnowyPlains:
                            Spruce(Put, lx, th + 1, lz, 6 + (int)(kind * 4));
                            break;
                        case Biome.BirchForest:
                            Round(Put, GetL, lx, th + 1, lz, 5 + (int)(kind * 2), _birchLog, _birchLeaves, gx, gz);
                            break;
                        case Biome.Mountains:
                            if (th < 95) Spruce(Put, lx, th + 1, lz, 5 + (int)(kind * 3));
                            break;
                        default:
                            if (kind < 0.2) Round(Put, GetL, lx, th + 1, lz, 5 + (int)(kind * 10), _birchLog, _birchLeaves, gx, gz);
                            else Round(Put, GetL, lx, th + 1, lz, 4 + (int)(kind * 3), _oakLog, _oakLeaves, gx, gz);
                            break;
                    }
                    // A tree in the middle of tall grass: clear the grass under the trunk.
                    if (lx >= 0 && lx < 16 && lz >= 0 && lz < 16 && GetL(lx, th, lz) == _grass) Put(lx, th, lz, _dirt);
                }

            // The portal home, next to spawn.
            if (_portal != 0 && _obsidian != 0)
                for (int dx = -1; dx < 5; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dy = -1; dy < 7; dy++)
                        {
                            int lx = PortalBase.x + dx - x0, lz = PortalBase.z + dz - z0, y = PortalBase.y + dy;
                            if (lx < 0 || lx > 15 || lz < 0 || lz > 15) continue;
                            ushort id = 0;
                            if (dz == 0 && dx >= 0 && dx < 4 && dy >= -1 && dy < 5)
                                id = dy == -1 ? _obsidian : (dx == 0 || dx == 3 || dy == 0 || dy == 4) ? _obsidian : _portal;
                            else if (dy == -1) id = GetL(lx, y, lz) == 0 ? _dirt : GetL(lx, y, lz);
                            Put(lx, y, lz, id);
                        }

            for (int i = 0; i < 256; i++)
            {
                int lx = i & 15, lz = i >> 4;
                short top = 0;
                for (int y = Height - 1; y > 0; y--)
                {
                    ushort id = GetL(lx, y, lz);
                    if (id != 0 && BlockRegistry.IsOpaqueId(id)) { top = (short)y; break; }
                }
                heights[i] = top;
            }
            return sec;
        }

        private void Round(Action<int, int, int, ushort> put, Func<int, int, int, ushort> get, int x, int y, int z, int h, ushort log, ushort leaves, int gx, int gz)
        {
            for (int dy = h - 3; dy <= h; dy++)
            {
                int r = dy >= h - 1 ? 1 : 2;
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        bool corner = Math.Abs(dx) == r && Math.Abs(dz) == r;
                        if (corner && (dy == h || Noise.Hash(gx * 7 + dx, dy, gz * 7 + dz, _seed) < 0.5)) continue;
                        if ((uint)(x + dx) <= 15 && (uint)(z + dz) <= 15 && get(x + dx, y + dy, z + dz) == 0) put(x + dx, y + dy, z + dz, leaves);
                    }
            }
            for (int dy = 0; dy < h; dy++) put(x, y + dy, z, log);
        }

        private void Spruce(Action<int, int, int, ushort> put, int x, int y, int z, int h)
        {
            // Cone of leaves: wide at the bottom, a point at the top.
            for (int dy = 2; dy <= h; dy++)
            {
                int fromTop = h - dy;
                int r = fromTop == 0 ? 0 : fromTop % 2 == 1 ? 1 : Math.Min(3, 1 + fromTop / 3);
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                        if (Math.Abs(dx) + Math.Abs(dz) <= r + (r > 1 ? 1 : 0)) put(x + dx, y + dy, z + dz, _spruceLeaves);
            }
            put(x, y + h + 1, z, _spruceLeaves);
            for (int dy = 0; dy < h; dy++) put(x, y + dy, z, _spruceLog);
        }
    }
}
