using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// Reads pictures and sounds for packs. Nothing from another game is shipped with the mod:
    ///   "mc:block/stone"        a texture inside your Minecraft .jar (sounds: "mc:dig/grass" = dig/grass1..N)
    ///   "file:textures/a.png"   a file next to the pack's pack.json (absolute paths work too)
    ///   "zip:C:/x/y.zip!a/b.png" a file inside any zip/jar
    ///   "#RRGGBB"               a plain coloured tile, used when nothing else is available
    /// </summary>
    public static class GameAssets
    {
        private static ZipArchive _jar;
        private static readonly Dictionary<string, ZipArchiveEntry> JarEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> McSounds = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ZipArchive> OtherZips = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);

        public static bool MinecraftFound => _jar != null;
        public static string MinecraftVersion { get; private set; }

        public static string MinecraftDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        /// <summary>Finds the newest vanilla release jar (or the one asked for) and the sound index that goes with it.</summary>
        public static void InitMinecraft(string wantedVersion)
        {
            try
            {
                string versions = Path.Combine(MinecraftDir, "versions");
                if (!Directory.Exists(versions))
                {
                    Plugin.Log.LogWarning("No Minecraft install found at " + MinecraftDir + " (Minecraft pack will use plain colours).");
                    return;
                }

                var found = new List<(string time, string jar, JObject info)>();
                foreach (var folder in Directory.GetDirectories(versions))
                {
                    string name = Path.GetFileName(folder);
                    string jar = Path.Combine(folder, name + ".jar");
                    string json = Path.Combine(folder, name + ".json");
                    if (!File.Exists(jar) || !File.Exists(json)) continue;
                    JObject info;
                    try { info = JObject.Parse(File.ReadAllText(json)); } catch { continue; }
                    if (info["inheritsFrom"] != null) continue; // Fabric/Forge profiles just borrow a real version
                    if (!string.IsNullOrEmpty(wantedVersion))
                    {
                        if (!string.Equals(name, wantedVersion, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else if ((string)info["type"] != "release") continue;
                    found.Add(((string)info["releaseTime"] ?? "", jar, info));
                }
                found.Sort((a, b) => string.CompareOrdinal(b.time, a.time));

                foreach (var (_, jarPath, info) in found)
                {
                    ZipArchive zip = null;
                    try
                    {
                        zip = new ZipArchive(File.OpenRead(jarPath), ZipArchiveMode.Read);
                        // Versions before 1.13 keep their textures somewhere else.
                        if (zip.GetEntry("assets/minecraft/textures/block/stone.png") == null) { zip.Dispose(); continue; }
                    }
                    catch { zip?.Dispose(); continue; }

                    _jar = zip;
                    foreach (var e in zip.Entries)
                        if (e.FullName.StartsWith("assets/minecraft/", StringComparison.Ordinal) || e.FullName.StartsWith("data/minecraft/", StringComparison.Ordinal))
                            JarEntries[e.FullName] = e;
                    MinecraftVersion = (string)info["id"] ?? Path.GetFileNameWithoutExtension(jarPath);
                    LoadSoundIndex(info);
                    Plugin.Log.LogInfo($"Minecraft {MinecraftVersion} found: {JarEntries.Count} files, {McSounds.Count} sounds.");
                    return;
                }
                Plugin.Log.LogWarning("No usable Minecraft release jar (1.13+) found. Minecraft pack will use plain colours.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading Minecraft failed: " + ex);
            }
        }

        // Sounds live in .minecraft/assets/objects under scrambled names; the index says which is which.
        private static void LoadSoundIndex(JObject info)
        {
            string id = (string)info["assetIndex"]?["id"];
            if (id == null) return;
            string indexPath = Path.Combine(MinecraftDir, "assets", "indexes", id + ".json");
            if (!File.Exists(indexPath)) return;
            var objects = JObject.Parse(File.ReadAllText(indexPath))["objects"] as JObject;
            if (objects == null) return;
            string objDir = Path.Combine(MinecraftDir, "assets", "objects");
            foreach (var p in objects.Properties())
            {
                if (!p.Name.StartsWith("minecraft/sounds/", StringComparison.Ordinal) || !p.Name.EndsWith(".ogg", StringComparison.Ordinal)) continue;
                string hash = (string)p.Value["hash"];
                if (hash == null) continue;
                McSounds[p.Name.Substring(17, p.Name.Length - 21)] = Path.Combine(objDir, hash.Substring(0, 2), hash);
            }
        }

        public static byte[] ReadJar(string path)
        {
            if (_jar == null) return null;
            if (!JarEntries.TryGetValue("assets/minecraft/" + path, out var e)) return null;
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>A file anywhere in the jar by its full path ("data/minecraft/recipe/torch.json"), or null.</summary>
        public static string ReadJarText(string fullPath)
        {
            if (_jar == null || !JarEntries.TryGetValue(fullPath, out var e)) return null;
            using (var s = e.Open())
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        /// <summary>Every jar path starting with a prefix.</summary>
        public static List<string> ListJar(string prefix)
        {
            var list = new List<string>();
            foreach (var k in JarEntries.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) list.Add(k);
            return list;
        }

        /// <summary>Raw bytes for a texture spec, or null.</summary>
        public static byte[] ReadBytes(string spec, string packDir)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            try
            {
                if (spec.StartsWith("mc:", StringComparison.Ordinal))
                {
                    // Allow fallbacks like "mc:block/short_grass|block/grass" for renamed textures.
                    foreach (var option in spec.Substring(3).Split('|'))
                    {
                        var b = ReadJar("textures/" + option + ".png");
                        if (b != null) return b;
                    }
                    return null;
                }
                if (spec.StartsWith("zip:", StringComparison.Ordinal))
                {
                    int bang = spec.IndexOf('!');
                    if (bang < 0) return null;
                    string zipPath = ResolvePath(spec.Substring(4, bang - 4), packDir);
                    if (!OtherZips.TryGetValue(zipPath, out var zip))
                    {
                        zip = File.Exists(zipPath) ? new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read) : null;
                        OtherZips[zipPath] = zip;
                    }
                    var entry = zip?.GetEntry(spec.Substring(bang + 1));
                    if (entry == null) return null;
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
                }
                string file = ResolvePath(spec.StartsWith("file:", StringComparison.Ordinal) ? spec.Substring(5) : spec, packDir);
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not read '{spec}': {ex.Message}");
                return null;
            }
        }

        public static string ResolvePath(string path, string packDir)
        {
            path = path.Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(path) || packDir == null ? path : Path.Combine(packDir, path);
        }

        /// <summary>A texture from a spec, or null. Animated strips (taller than wide) keep only the first frame.</summary>
        public static Texture2D LoadTexture(string spec, string packDir, bool firstFrameOnly = true)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (spec.StartsWith("#", StringComparison.Ordinal)) return null;
            var bytes = ReadBytes(spec, packDir);
            if (bytes == null) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes, false)) { UnityEngine.Object.Destroy(tex); return null; }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (firstFrameOnly && tex.height > tex.width && tex.height % tex.width == 0)
            {
                // Unity textures start at the bottom row, the first frame is the top square.
                int w = tex.width;
                var px = tex.GetPixels(0, tex.height - w, w, w);
                var frame = new Texture2D(w, w, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                frame.SetPixels(px);
                frame.Apply(false);
                UnityEngine.Object.Destroy(tex);
                return frame;
            }
            return tex;
        }

        /// <summary>Files for a sound spec. "mc:dig/grass" gives dig/grass1, dig/grass2, ...</summary>
        public static List<string> SoundFiles(string spec, string packDir)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(spec)) return files;
            foreach (var one in spec.Split('|'))
            {
                if (one.StartsWith("mc:", StringComparison.Ordinal))
                {
                    string name = one.Substring(3);
                    for (int n = 1; n < 40; n++)
                    {
                        if (McSounds.TryGetValue(name + n, out var p)) { if (File.Exists(p)) files.Add(p); }
                        else if (n > 1) break;
                    }
                    if (files.Count == 0 && McSounds.TryGetValue(name, out var single) && File.Exists(single)) files.Add(single);
                }
                else if (one.StartsWith("mc-music:", StringComparison.Ordinal))
                {
                    string prefix = "music/" + one.Substring(9) + "/";
                    foreach (var kv in McSounds)
                        if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.IndexOf('/', prefix.Length) < 0 && File.Exists(kv.Value))
                            files.Add(kv.Value);
                    files.Sort(StringComparer.Ordinal);
                }
                else
                {
                    string path = ResolvePath(one.StartsWith("file:", StringComparison.Ordinal) ? one.Substring(5) : one, packDir);
                    if (File.Exists(path)) files.Add(path);
                    else if (Directory.Exists(path))
                        foreach (var f in Directory.GetFiles(path))
                            if (IsAudioFile(f)) files.Add(f);
                }
            }
            return files;
        }

        public static bool IsAudioFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".ogg" || ext == ".wav" || ext == ".mp3";
        }

        public static AudioType AudioTypeFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".wav": return AudioType.WAV;
                case ".mp3": return AudioType.MPEG;
                default: return AudioType.OGGVORBIS; // Minecraft's hashed files have no extension but are Ogg Vorbis
            }
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            if (!hex.StartsWith("#", StringComparison.Ordinal)) hex = "#" + hex;
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
        }
    }
}
