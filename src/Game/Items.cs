using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    public class ItemDef
    {
        public int Id;
        public string Key, LocalId, Name, Mc;   // Mc: "minecraft:stick", used by recipes
        public Pack Pack;
        public BlockDef Block;                  // placeable blocks
        public string Texture;
        public int MaxStack = 64;
        public int Food;                        // hunger points it restores
        public float Saturation;
        public string ToolType;                 // pickaxe, axe, shovel, sword
        public int ToolLevel;                   // 1 wood, 2 stone, 3 iron, 4 diamond
        public float ToolSpeed = 1f;
        public float Damage = 1f;
        public int Durability;
        public int Fuel;                        // furnace burn time in ticks (20 a second)
        public int Armor;                       // armor points when worn
        public string Slot;                     // head, chest, legs, feet
        public string Plants;                   // seeds: the crop block they plant on farmland
        public string Use;                      // bow, shears, bonemeal
        public Vector4 AtlasUV;                 // in Items.Atlas (flat items)

        public bool IsBlock => Block != null;
        public bool IsFood => Food > 0;
    }

    public struct ItemStack
    {
        public ItemDef Item;
        public int Count;
        public int Wear;                        // durability used up (tools)

        public ItemStack(ItemDef item, int count) { Item = item; Count = count; Wear = 0; }
        public bool Empty => Item == null || Count <= 0;
        public static readonly ItemStack None = default;
    }

    /// <summary>
    /// Every item: one per placeable block, plus the pack's own items (sticks, coal, tools, food).
    /// Flat item pictures are packed into one atlas so the HUD and menus can draw them in one go.
    /// </summary>
    public static class Items
    {
        public static readonly List<ItemDef> All = new List<ItemDef>();
        private static readonly Dictionary<string, ItemDef> ByKey = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ItemDef> ByMc = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ushort, ItemDef> ByBlock = new Dictionary<ushort, ItemDef>();
        public static Texture2D Atlas;

        public static ItemDef Get(string key) => key != null && ByKey.TryGetValue(key, out var d) ? d : null;
        public static ItemDef GetMc(string mc) => mc != null && ByMc.TryGetValue(mc, out var d) ? d : null;
        public static ItemDef ForBlock(BlockDef b) => b != null && ByBlock.TryGetValue(b.Index, out var d) ? d : null;

        public static ItemDef Local(Pack pack, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Get(pack.Id + ":" + id) ?? GetMc(id.Contains(":") ? id : "minecraft:" + id);
        }

        private static ItemDef Add(ItemDef d)
        {
            d.Id = All.Count;
            All.Add(d);
            ByKey[d.Key] = d;
            if (d.Mc != null && !ByMc.ContainsKey(d.Mc)) ByMc[d.Mc] = d;
            return d;
        }

        public static void AddPack(Pack pack)
        {
            bool mc = pack.Json["recipes"] != null && (string)pack.Json["recipes"] == "minecraft";
            foreach (var b in pack.Blocks)
            {
                if (!b.Item) continue;
                var d = Add(new ItemDef { Key = b.Key, LocalId = b.LocalId, Name = b.Name, Pack = pack, Block = b, Mc = mc ? "minecraft:" + b.LocalId : null });
                ByBlock[b.Index] = d;
            }
            if (pack.Json["items"] is JArray items)
                foreach (var t in items)
                {
                    if (!(t is JObject o) || (string)o["id"] == null) continue;
                    string id = (string)o["id"];
                    Add(new ItemDef
                    {
                        Key = pack.Id + ":" + id, LocalId = id, Name = (string)o["name"] ?? id, Pack = pack,
                        Mc = mc ? "minecraft:" + id : null,
                        Texture = (string)o["texture"] ?? (mc ? "mc:item/" + id : null),
                        MaxStack = (int?)o["stack"] ?? 64,
                        Food = (int?)o["food"] ?? 0,
                        Saturation = (float?)o["saturation"] ?? 0f,
                        ToolType = (string)o["tool"],
                        ToolLevel = (int?)o["level"] ?? 0,
                        ToolSpeed = (float?)o["speed"] ?? 1f,
                        Damage = (float?)o["damage"] ?? 1f,
                        Durability = (int?)o["durability"] ?? 0,
                        Fuel = (int?)o["fuel"] ?? 0,
                        Armor = (int?)o["armor"] ?? 0,
                        Slot = (string)o["slot"],
                        Plants = (string)o["plants"],
                        Use = (string)o["use"],
                    });
                }
            // Fuel for blocks (logs, planks...) from the pack's "fuel" map: { "oak_log": 300, ... }
            if (pack.Json["fuel"] is JObject fuel)
                foreach (var p in fuel.Properties())
                {
                    var d = Local(pack, p.Name);
                    if (d != null) d.Fuel = (int)p.Value;
                }
            foreach (var d in All) if (d.ToolType != null && d.Durability > 0) d.MaxStack = 1;
        }

        public static void BuildAtlas()
        {
            var flat = All.FindAll(d => d.Block == null);
            int n = Math.Max(1, flat.Count), cols = 16, rows = (n + cols - 1) / cols;
            int w = cols * 16, h = Mathf.NextPowerOfTwo(rows * 16);
            var px = new Color32[w * h];
            for (int i = 0; i < flat.Count; i++)
            {
                var d = flat[i];
                int ox = (i % cols) * 16, oy = (i / cols) * 16;
                var tex = GameAssets.LoadTexture(d.Texture, d.Pack.Dir);
                Color32[] src = null;
                if (tex != null)
                {
                    if (tex.width != 16 || tex.height != 16)
                    {
                        var rt = RenderTexture.GetTemporary(16, 16);
                        Graphics.Blit(tex, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var small = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                        small.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
                        small.Apply();
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(tex);
                        tex = small;
                    }
                    src = tex.GetPixels32();
                    UnityEngine.Object.Destroy(tex);
                }
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        px[(oy + y) * w + ox + x] = src != null ? src[y * 16 + x] : (x + y) % 4 == 0 ? new Color32(200, 60, 200, 255) : new Color32(0, 0, 0, 0);
                float e = 0.01f;
                d.AtlasUV = new Vector4((ox + e) / w, (oy + e) / h, (ox + 16 - e) / w, (oy + 16 - e) / h);
            }
            Atlas = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "GorillaMergeItems" };
            Atlas.SetPixels32(px);
            Atlas.Apply(false);
            Plugin.Log.LogInfo($"Items: {All.Count} ({flat.Count} flat pictures).");
        }
    }

    public class Recipe
    {
        public int W, H;
        public ItemDef[][] Cells;               // W*H, null = must be empty
        public List<ItemDef[]> Shapeless;       // for shapeless recipes
        public ItemDef Result;
        public int Count = 1;
    }

    public class SmeltRecipe
    {
        public HashSet<ItemDef> Input;
        public ItemDef Result;
        public float Seconds = 10f;
    }

    /// <summary>Minecraft's own crafting and smelting recipes, read from the jar, for the items we have.</summary>
    public static class Recipes
    {
        public static readonly List<Recipe> Crafting = new List<Recipe>();
        public static readonly List<SmeltRecipe> Smelting = new List<SmeltRecipe>();
        private static readonly Dictionary<string, List<string>> Tags = new Dictionary<string, List<string>>();

        private static List<string> Tag(string name, int depth = 0)
        {
            if (Tags.TryGetValue(name, out var cached)) return cached;
            var list = new List<string>();
            Tags[name] = list;
            string id = name.StartsWith("minecraft:") ? name.Substring(10) : name;
            var text = GameAssets.ReadJarText("data/minecraft/tags/item/" + id + ".json");
            if (text == null || depth > 8) return list;
            try
            {
                foreach (var v in (JArray)JObject.Parse(text)["values"])
                {
                    string s = v is JObject o ? (string)o["id"] : (string)v;
                    if (s == null) continue;
                    if (s.StartsWith("#")) list.AddRange(Tag(s.Substring(1), depth + 1));
                    else list.Add(s);
                }
            }
            catch { }
            return list;
        }

        // An ingredient can be "minecraft:x", "#minecraft:tag", a list of those, or {"item"/"tag": ...}.
        private static ItemDef[] Ingredient(JToken t)
        {
            var ids = new List<string>();
            void Add(JToken x)
            {
                if (x == null) return;
                if (x is JArray arr) { foreach (var a in arr) Add(a); return; }
                if (x is JObject o) { if (o["item"] != null) ids.Add((string)o["item"]); if (o["tag"] != null) ids.AddRange(Tag((string)o["tag"])); return; }
                string s = (string)x;
                if (s.StartsWith("#")) ids.AddRange(Tag(s.Substring(1)));
                else ids.Add(s);
            }
            Add(t);
            var defs = new List<ItemDef>();
            foreach (var id in ids) { var d = Items.GetMc(id); if (d != null && !defs.Contains(d)) defs.Add(d); }
            return defs.Count > 0 ? defs.ToArray() : null;
        }

        public static void LoadMinecraft()
        {
            if (!GameAssets.MinecraftFound) return;
            int skipped = 0;
            foreach (var path in GameAssets.ListJar("data/minecraft/recipe/"))
            {
                if (!path.EndsWith(".json")) continue;
                try
                {
                    var o = JObject.Parse(GameAssets.ReadJarText(path));
                    string type = (string)o["type"];
                    var res = o["result"];
                    var result = Items.GetMc(res is JObject ro ? (string)ro["id"] ?? (string)ro["item"] : (string)res);
                    int count = res is JObject rc ? (int?)rc["count"] ?? 1 : 1;
                    if (result == null) { skipped++; continue; }
                    if (type == "minecraft:crafting_shaped")
                    {
                        var pattern = new List<string>();
                        foreach (var row in (JArray)o["pattern"]) pattern.Add((string)row);
                        var key = (JObject)o["key"];
                        var r = new Recipe { W = 0, H = pattern.Count, Result = result, Count = count };
                        foreach (var row in pattern) r.W = Math.Max(r.W, row.Length);
                        r.Cells = new ItemDef[r.W * r.H][];
                        bool ok = true;
                        for (int y = 0; y < r.H && ok; y++)
                            for (int x = 0; x < r.W; x++)
                            {
                                char ch = x < pattern[y].Length ? pattern[y][x] : ' ';
                                if (ch == ' ') continue;
                                var ing = Ingredient(key[ch.ToString()]);
                                if (ing == null) { ok = false; break; }
                                r.Cells[y * r.W + x] = ing;
                            }
                        if (ok) Crafting.Add(r); else skipped++;
                    }
                    else if (type == "minecraft:crafting_shapeless")
                    {
                        var r = new Recipe { Shapeless = new List<ItemDef[]>(), Result = result, Count = count };
                        bool ok = true;
                        foreach (var ing in (JArray)o["ingredients"])
                        {
                            var d = Ingredient(ing);
                            if (d == null) { ok = false; break; }
                            r.Shapeless.Add(d);
                        }
                        if (ok) Crafting.Add(r); else skipped++;
                    }
                    else if (type == "minecraft:smelting")
                    {
                        var ing = Ingredient(o["ingredient"]);
                        if (ing == null) { skipped++; continue; }
                        Smelting.Add(new SmeltRecipe { Input = new HashSet<ItemDef>(ing), Result = result, Seconds = ((int?)o["cookingtime"] ?? 200) / 20f });
                    }
                }
                catch { skipped++; }
            }
            Plugin.Log.LogInfo($"Recipes: {Crafting.Count} crafting, {Smelting.Count} smelting (skipped {skipped} needing items we don't have).");
        }

        /// <summary>What the grid makes (size gridW x gridW), or null.</summary>
        public static Recipe Match(ItemStack[] grid, int gridW)
        {
            int minX = gridW, minY = gridW, maxX = -1, maxY = -1, filled = 0;
            for (int y = 0; y < gridW; y++)
                for (int x = 0; x < gridW; x++)
                    if (!grid[y * gridW + x].Empty)
                    {
                        filled++;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
            if (filled == 0) return null;
            int w = maxX - minX + 1, h = maxY - minY + 1;
            foreach (var r in Crafting)
            {
                if (r.Shapeless != null)
                {
                    if (r.Shapeless.Count != filled) continue;
                    var used = new bool[r.Shapeless.Count];
                    bool ok = true;
                    for (int i = 0; i < grid.Length && ok; i++)
                    {
                        if (grid[i].Empty) continue;
                        int found = -1;
                        for (int k = 0; k < r.Shapeless.Count; k++)
                            if (!used[k] && Array.IndexOf(r.Shapeless[k], grid[i].Item) >= 0) { found = k; break; }
                        if (found < 0) ok = false; else used[found] = true;
                    }
                    if (ok) return r;
                    continue;
                }
                if (r.W != w || r.H != h) continue;
                for (int mirror = 0; mirror < 2; mirror++)
                {
                    bool ok = true;
                    for (int y = 0; y < h && ok; y++)
                        for (int x = 0; x < w && ok; x++)
                        {
                            var cell = r.Cells[y * w + (mirror == 1 ? w - 1 - x : x)];
                            var st = grid[(minY + y) * gridW + minX + x];
                            if (cell == null) ok = st.Empty;
                            else ok = !st.Empty && Array.IndexOf(cell, st.Item) >= 0;
                        }
                    if (ok) return r;
                }
            }
            return null;
        }

        public static SmeltRecipe SmeltFor(ItemDef d)
        {
            if (d == null) return null;
            foreach (var s in Smelting) if (s.Input.Contains(d)) return s;
            return null;
        }
    }

    /// <summary>The player's 36 slots (0-8 is the hotbar), like Minecraft's inventory.</summary>
    public class Inventory
    {
        public readonly ItemStack[] Slots = new ItemStack[36];
        public readonly ItemStack[] Armor = new ItemStack[4]; // head, chest, legs, feet
        public int Selected;

        public static int SlotIndex(string slot) => slot == "head" ? 0 : slot == "chest" ? 1 : slot == "legs" ? 2 : slot == "feet" ? 3 : -1;

        public int ArmorPoints
        {
            get { int n = 0; foreach (var a in Armor) if (!a.Empty) n += a.Item.Armor; return n; }
        }

        public int CountOf(ItemDef d)
        {
            int n = 0;
            foreach (var s in Slots) if (!s.Empty && s.Item == d) n += s.Count;
            return n;
        }

        /// <summary>Takes one of an item from anywhere in the inventory.</summary>
        public bool TakeOne(ItemDef d)
        {
            for (int i = 0; i < 36; i++)
            {
                if (Slots[i].Empty || Slots[i].Item != d) continue;
                Slots[i].Count--;
                if (Slots[i].Count <= 0) Slots[i] = ItemStack.None;
                return true;
            }
            return false;
        }

        public ItemStack Held => Slots[Selected];

        /// <summary>Adds items, filling stacks first. Returns how many didn't fit.</summary>
        public int Add(ItemDef item, int count)
        {
            if (item == null || count <= 0) return 0;
            int[] order = new int[36];
            for (int i = 0; i < 36; i++) order[i] = i;
            for (int pass = 0; pass < 2 && count > 0; pass++)
                foreach (int i in order)
                {
                    ref var s = ref Slots[i];
                    if (pass == 0 && (s.Empty || s.Item != item || s.Count >= item.MaxStack)) continue;
                    if (pass == 1 && !s.Empty) continue;
                    if (pass == 1) s = new ItemStack(item, 0);
                    int put = Math.Min(count, item.MaxStack - s.Count);
                    s.Count += put;
                    count -= put;
                    if (count <= 0) break;
                }
            return count;
        }

        public void UseHeld(int n = 1)
        {
            ref var s = ref Slots[Selected];
            s.Count -= n;
            if (s.Count <= 0) s = ItemStack.None;
        }

        /// <summary>Wears down the held tool; it breaks when it runs out.</summary>
        public bool WearHeld()
        {
            ref var s = ref Slots[Selected];
            if (s.Empty || s.Item.Durability <= 0) return false;
            s.Wear++;
            if (s.Wear >= s.Item.Durability) { s = ItemStack.None; return true; }
            return false;
        }

        public void Clear()
        {
            for (int i = 0; i < 36; i++) Slots[i] = ItemStack.None;
            for (int i = 0; i < 4; i++) Armor[i] = ItemStack.None;
        }

        public JObject ToJson()
        {
            var arr = new JArray();
            for (int i = 0; i < 36; i++)
            {
                var s = Slots[i];
                arr.Add(s.Empty ? null : new JObject { ["item"] = s.Item.Key, ["count"] = s.Count, ["wear"] = s.Wear });
            }
            var armor = new JArray();
            foreach (var s in Armor) armor.Add(s.Empty ? null : new JObject { ["item"] = s.Item.Key, ["count"] = 1, ["wear"] = s.Wear });
            return new JObject { ["slots"] = arr, ["armor"] = armor, ["selected"] = Selected };
        }

        public void FromJson(JObject o)
        {
            Clear();
            if (o == null) return;
            Selected = Mathf.Clamp((int?)o["selected"] ?? 0, 0, 8);
            if (o["armor"] is JArray ar)
                for (int i = 0; i < Math.Min(4, ar.Count); i++)
                {
                    if (!(ar[i] is JObject a)) continue;
                    var d = Items.Get((string)a["item"]);
                    if (d != null) Armor[i] = new ItemStack(d, 1) { Wear = (int?)a["wear"] ?? 0 };
                }
            if (!(o["slots"] is JArray arr)) return;
            for (int i = 0; i < Math.Min(36, arr.Count); i++)
            {
                if (!(arr[i] is JObject s)) continue;
                var d = Items.Get((string)s["item"]);
                if (d != null) Slots[i] = new ItemStack(d, (int?)s["count"] ?? 1) { Wear = (int?)s["wear"] ?? 0 };
            }
        }
    }
}
