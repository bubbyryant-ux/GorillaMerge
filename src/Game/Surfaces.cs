using System;
using System.Collections.Generic;
using GorillaLocomotion;

namespace GorillaMerge
{
    /// <summary>
    /// Picks which of Gorilla Tag's own surface materials a block feels like, so tapping stone
    /// sounds like stone and ice is slippery. Matched by name, because the list changes between updates.
    /// </summary>
    public static class Surfaces
    {
        private static readonly List<string> Names = new List<string>();
        private static readonly Dictionary<string, int> Cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Names first are exact Gorilla Tag material names seen in the Oct 2026 build, then fallbacks.
            ["stone"] = new[] { "rockwall2", "rockwall3", "rock", "stone", "concrete" },
            ["wood"] = new[] { "woodensupport", "wood", "plank", "bark", "log" },
            ["grass"] = new[] { "MetroGrass", "grass", "leaf", "bush" },
            ["gravel"] = new[] { "pitground", "gravel", "dirt", "ground" },
            ["dirt"] = new[] { "pitground", "dirt", "ground", "mud" },
            ["sand"] = new[] { "BeachSand", "sand", "pitground" },
            ["glass"] = new[] { "window", "glass", "crystal" },
            ["ice"] = new[] { "iceground", "ice" },
            ["snow"] = new[] { "pitgroundwinter", "snow" },
            ["cloth"] = new[] { "ShagCarpetB", "carpet", "cloth", "fabric", "pillow" },
            ["metal"] = new[] { "metal", "iron", "steel" },
        };

        public static void Init(GTPlayer gp)
        {
            Names.Clear();
            Cache.Clear();
            try
            {
                foreach (var m in gp.materialData) Names.Add(m.matName ?? "");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read Gorilla Tag surface list: " + ex.Message);
            }
            Plugin.Log.LogInfo("Gorilla Tag surfaces: " + string.Join(", ", Names.ConvertAll(n => Names.IndexOf(n) + "=" + n)));
        }

        public static int Resolve(string keyword)
        {
            if (string.IsNullOrEmpty(keyword) || Names.Count == 0) return 0;
            if (Cache.TryGetValue(keyword, out int found)) return found;
            if (!Synonyms.TryGetValue(keyword, out var words)) words = new[] { keyword };
            int result = 0;
            // Exact names first, then names that contain the word.
            foreach (var w in words)
            {
                int i = Names.FindIndex(n => string.Equals(n, w, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { result = i; goto done; }
            }
            foreach (var w in words)
            {
                int i = Names.FindIndex(n => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                if (i >= 0) { result = i; goto done; }
            }
            done:
            Cache[keyword] = result;
            Plugin.Log.LogInfo($"Surface '{keyword}' -> {result} ({(result < Names.Count ? Names[result] : "?")})");
            return result;
        }
    }
}
