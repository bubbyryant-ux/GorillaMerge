using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    public enum PackType { Voxel, Map }

    public class SoundSet
    {
        public string Break, Place, Step;
    }

    /// <summary>
    /// One game merged into Gorilla Tag, read from Packs/&lt;folder&gt;/pack.json.
    /// See Packs/README.md for the format.
    /// </summary>
    public class Pack
    {
        public string Id;
        public string Name;
        public string Dir;
        public PackType Type;
        public JObject Json;

        public Color Sky = new Color(0.47f, 0.65f, 1f);
        public Color Fog = new Color(0.75f, 0.85f, 1f);
        public string Clouds;
        public string Sun;
        public string MusicSpec;

        // Voxel packs
        public readonly List<BlockDef> Blocks = new List<BlockDef>();
        public readonly List<BlockDef> Items = new List<BlockDef>();
        public readonly Dictionary<string, SoundSet> SoundGroups = new Dictionary<string, SoundSet>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public JObject Gen;

        // Map packs
        public string Model;
        public float Scale = 1f;
        public Vector3? Spawn;
        public string Surface = "stone";

        public BlockDef Block(string localId)
        {
            if (string.IsNullOrEmpty(localId)) return null;
            return BlockRegistry.Get(Id + ":" + localId);
        }

        public ushort BlockIndex(string localId)
        {
            var b = Block(localId);
            return b != null ? b.Index : (ushort)0;
        }

        public static Pack Load(string dir) => Parse(File.ReadAllText(Path.Combine(dir, "pack.json")), dir, Path.GetFileName(dir));

        private static Pack Parse(string text, string dir, string id)
        {
            var json = JObject.Parse(text);
            var p = new Pack
            {
                Dir = dir,
                Json = json,
                Id = id,
                Name = (string)json["name"] ?? id,
                Type = string.Equals((string)json["type"], "map", StringComparison.OrdinalIgnoreCase) ? PackType.Map : PackType.Voxel,
            };
            p.Sky = GameAssets.ParseColor((string)json["sky"], p.Sky);
            p.Fog = GameAssets.ParseColor((string)json["fog"], Color.Lerp(p.Sky, Color.white, 0.4f));
            p.Clouds = (string)json["clouds"];
            p.Sun = (string)json["sun"];
            p.MusicSpec = (string)json["music"];

            if (json["sounds"] is JObject sounds)
            {
                foreach (var s in sounds.Properties())
                {
                    if (s.Value is JObject o)
                        p.SoundGroups[s.Name] = new SoundSet { Break = (string)o["break"], Place = (string)o["place"] ?? (string)o["break"], Step = (string)o["step"] ?? (string)o["break"] };
                    else
                        p.SoundGroups[s.Name] = new SoundSet { Break = (string)s.Value, Place = (string)s.Value, Step = (string)s.Value };
                }
            }
            if (json["events"] is JObject events)
                foreach (var e in events.Properties()) p.Events[e.Name] = (string)e.Value;

            if (p.Type == PackType.Map)
            {
                p.Model = (string)json["model"];
                p.Scale = (float?)json["scale"] ?? 1f;
                p.Surface = (string)json["surface"] ?? "stone";
                if (json["spawn"] is JArray sp && sp.Count == 3)
                    p.Spawn = new Vector3((float)sp[0], (float)sp[1], (float)sp[2]);
            }
            else
            {
                p.Gen = json["world"] as JObject;
            }
            return p;
        }

        /// <summary>Every pack folder (with a pack.json) inside the Packs directory.</summary>
        public static List<Pack> LoadAll(string packsDir)
        {
            var list = new List<Pack>();
            string[] dirs = Directory.Exists(packsDir) ? Directory.GetDirectories(packsDir) : new string[0];
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                if (!File.Exists(Path.Combine(dir, "pack.json"))) continue;
                try
                {
                    var p = Load(dir);
                    list.Add(p);
                    Plugin.Log.LogInfo($"Pack '{p.Name}' ({p.Type}) from {dir}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Pack in {dir} is broken: {ex.Message}");
                }
            }
            // The Minecraft pack is built into the DLL, so the download is just GorillaMerge.dll.
            // A Packs\Minecraft folder (for tinkering with it) takes its place.
            if (!list.Exists(p => p.Id == "Minecraft"))
            {
                using (var s = typeof(Pack).Assembly.GetManifestResourceStream("GorillaMerge.Packs.Minecraft.pack.json"))
                    if (s != null)
                        using (var r = new StreamReader(s))
                        {
                            list.Add(Parse(r.ReadToEnd(), Path.Combine(packsDir, "Minecraft"), "Minecraft"));
                            Plugin.Log.LogInfo("Pack 'Minecraft' (built in)");
                        }
            }
            // Minecraft first, it is the main one.
            list.Sort((a, b) => (a.Id == "Minecraft" ? 0 : 1).CompareTo(b.Id == "Minecraft" ? 0 : 1));
            return list;
        }
    }
}
