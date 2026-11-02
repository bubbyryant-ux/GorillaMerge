using System;

namespace GorillaMerge
{
    /// <summary>
    /// Ken Perlin's improved noise, seeded. Pure C# so world generation can run on background threads
    /// (Unity's Mathf.PerlinNoise may only be used from the main thread).
    /// </summary>
    public sealed class Noise
    {
        private readonly int[] _p = new int[512];

        public Noise(int seed)
        {
            var perm = new int[256];
            for (int i = 0; i < 256; i++) perm[i] = i;
            var rng = new Random(seed);
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int i = 0; i < 512; i++) _p[i] = perm[i & 255];
        }

        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static double Lerp(double a, double b, double t) => a + t * (b - a);

        private static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : h == 12 || h == 14 ? x : z;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>3D noise, roughly -1..1.</summary>
        public double Get(double x, double y, double z)
        {
            int X = (int)Math.Floor(x) & 255, Y = (int)Math.Floor(y) & 255, Z = (int)Math.Floor(z) & 255;
            x -= Math.Floor(x); y -= Math.Floor(y); z -= Math.Floor(z);
            double u = Fade(x), v = Fade(y), w = Fade(z);
            int A = _p[X] + Y, AA = _p[A] + Z, AB = _p[A + 1] + Z, B = _p[X + 1] + Y, BA = _p[B] + Z, BB = _p[B + 1] + Z;
            return Lerp(
                Lerp(Lerp(Grad(_p[AA], x, y, z), Grad(_p[BA], x - 1, y, z), u),
                     Lerp(Grad(_p[AB], x, y - 1, z), Grad(_p[BB], x - 1, y - 1, z), u), v),
                Lerp(Lerp(Grad(_p[AA + 1], x, y, z - 1), Grad(_p[BA + 1], x - 1, y, z - 1), u),
                     Lerp(Grad(_p[AB + 1], x, y - 1, z - 1), Grad(_p[BB + 1], x - 1, y - 1, z - 1), u), v), w);
        }

        public double Get(double x, double z) => Get(x, 0.5, z);

        /// <summary>Layers of noise added together (bumpy hills on top of big hills). Roughly -1..1.</summary>
        public double Fbm(double x, double z, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0, f = 1;
            for (int i = 0; i < octaves; i++)
            {
                sum += Get(x * f + i * 17.3, 0.5 + i * 3.1, z * f - i * 11.7) * amp;
                norm += amp;
                amp *= gain;
                f *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>A repeatable random number 0..1 for a block position (used for trees, ores, bedrock).</summary>
        public static double Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (double)0x1000000;
            }
        }
    }
}
