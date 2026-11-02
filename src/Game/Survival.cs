using System;
using System.Collections.Generic;
using System.IO;
using GorillaLocomotion;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// Minecraft survival rules for you as a gorilla: 10 hearts, 10 drumsticks of hunger, items that drop
    /// when you break blocks, tools that make mining faster, eating, dying and respawning.
    /// </summary>
    public class Survival
    {
        public readonly Inventory Inv = new Inventory();
        public float Health = 20f, Food = 20f, Saturation = 5f;
        public bool Dead;
        public float HurtFlash;     // 1 right after you get hurt, fades to 0
        public string DeathMessage;
        public float EatProgress;   // 0..1 while you hold food to your mouth
        public float BowCharge;     // 0..1 while you draw a bow

        private float _exhaustion, _regen, _starve, _hurtCooldown, _respawnAt, _eatSound;
        private Vector3 _lastPos;
        private readonly MergeManager _mgr;

        public Survival(MergeManager mgr) { _mgr = mgr; }

        // ---------------- mining ----------------

        public static bool CanHarvest(BlockDef b, ItemStack held)
        {
            if (b.ToolLevel <= 0) return true;
            return !held.Empty && held.Item.ToolType == b.Tool && held.Item.ToolLevel >= b.ToolLevel;
        }

        /// <summary>Minecraft's mining time: hardness x 1.5 with the right tool tier (x 5 without), divided by tool speed.</summary>
        public static float BreakSeconds(BlockDef b, ItemStack held)
        {
            if (b.Unbreakable) return float.PositiveInfinity;
            float speed = 1f;
            if (!held.Empty && held.Item.ToolType != null && held.Item.ToolType == b.Tool) speed = held.Item.ToolSpeed;
            return b.Hardness * (CanHarvest(b, held) ? 1.5f : 5f) / speed;
        }

        /// <summary>What a broken block gives you.</summary>
        public static List<ItemStack> DropsFor(BlockDef b, ItemStack held)
        {
            var list = new List<ItemStack>();
            if (!CanHarvest(b, held)) return list;
            var d = b.Drops;
            if (d == null) { var self = Items.ForBlock(b); if (self != null) list.Add(new ItemStack(self, 1)); return list; }
            if (d.Type == JTokenType.String)
            {
                string id = (string)d;
                if (id == "none") return list;
                var item = Items.Local(b.Pack, id);
                if (item != null) list.Add(new ItemStack(item, 1));
                return list;
            }
            if (d is JArray arr)
                foreach (var e in arr)
                {
                    var item = Items.Local(b.Pack, (string)e["item"]);
                    if (item == null || UnityEngine.Random.value > ((float?)e["chance"] ?? 1f)) continue;
                    int min = (int?)e["min"] ?? 1, max = (int?)e["max"] ?? min;
                    list.Add(new ItemStack(item, UnityEngine.Random.Range(min, max + 1)));
                }
            return list;
        }

        // ---------------- health and hunger ----------------

        public void Tick(float dt, VoxelWorld w)
        {
            var gp = GTPlayer.Instance;
            if (gp == null) return;
            HurtFlash = Mathf.Max(0f, HurtFlash - dt * 2f);
            _hurtCooldown -= dt;
            if (Dead)
            {
                if (Time.time >= _respawnAt) Respawn(w);
                return;
            }

            // Moving around makes you hungry (Gorilla Tag movement is a lot of moving, so it's gentle).
            var pos = gp.headCollider.transform.position;
            float moved = Vector3.Distance(pos, _lastPos);
            if (moved < 5f) _exhaustion += moved * 0.02f;
            _lastPos = pos;
            _exhaustion += dt * 0.004f;
            if (_exhaustion >= 4f)
            {
                _exhaustion -= 4f;
                if (Saturation > 0) Saturation = Mathf.Max(0, Saturation - 1);
                else Food = Mathf.Max(0, Food - 1);
            }

            if (Food >= 18 && Health < 20)
            {
                _regen += dt;
                if (_regen >= 4f) { _regen = 0; Health = Mathf.Min(20, Health + 1); _exhaustion += 6f; }
            }
            else _regen = 0;
            if (Food <= 0)
            {
                _starve += dt;
                if (_starve >= 4f) { _starve = 0; if (Health > 1) Damage(1, "starved to death"); }
            }

            // Lava burns, the void below the world kills.
            var head = w.ToBlock(pos);
            var body = w.ToBlock(gp.bodyCollider.bounds.center);
            if (w.Def(head).Render == RenderKind.Lava || w.Def(body).Render == RenderKind.Lava) Damage(4, "tried to swim in lava");
            if (head.y < -24) Damage(4, "fell out of the world");
        }

        public void Damage(float amount, string cause, Vector3? from = null)
        {
            if (Dead || _hurtCooldown > 0f || amount <= 0) return;
            _hurtCooldown = 0.5f;
            // Armor soaks up damage like Minecraft (up to 80% at 20 points) and wears down.
            bool armorHelps = cause != "starved to death" && cause != "fell out of the world";
            if (armorHelps && Inv.ArmorPoints > 0)
            {
                amount *= 1f - Mathf.Min(20, Inv.ArmorPoints) / 25f;
                for (int i = 0; i < 4; i++)
                {
                    ref var a = ref Inv.Armor[i];
                    if (a.Empty || a.Item.Durability <= 0) continue;
                    a.Wear++;
                    if (a.Wear >= a.Item.Durability)
                    {
                        a = ItemStack.None;
                        _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "break", GTPlayer.Instance.headCollider.transform.position, 0.8f);
                    }
                }
            }
            Health -= amount;
            HurtFlash = 1f;
            var gp = GTPlayer.Instance;
            _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "hurt", gp.headCollider.transform.position, 0.9f);
            try { GorillaTagger.Instance.StartVibration(true, 0.6f, 0.15f); GorillaTagger.Instance.StartVibration(false, 0.6f, 0.15f); } catch { }
            if (from.HasValue)
            {
                var dir = gp.headCollider.transform.position - from.Value;
                dir.y = 0;
                dir = dir.normalized + Vector3.up * 0.6f;
                gp.ApplyKnockback(dir.normalized, 3.5f, true);
            }
            if (Health <= 0) Die(cause);
        }

        private void Die(string cause)
        {
            Health = 0;
            Dead = true;
            DeathMessage = "You " + cause;
            _respawnAt = Time.time + 4f;
            var w = _mgr.ActiveWorld;
            var at = GTPlayer.Instance.headCollider.transform.position;
            for (int i = 0; i < 36; i++)
                if (!Inv.Slots[i].Empty) ItemEntity.Spawn(_mgr, w, at, Inv.Slots[i], UnityEngine.Random.insideUnitSphere * 1.5f + Vector3.up);
            for (int i = 0; i < 4; i++)
                if (!Inv.Armor[i].Empty) ItemEntity.Spawn(_mgr, w, at, Inv.Armor[i], UnityEngine.Random.insideUnitSphere * 1.5f + Vector3.up);
            Inv.Clear();
            Plugin.Log.LogInfo("Died: " + cause);
        }

        private void Respawn(VoxelWorld w)
        {
            Dead = false;
            Health = 20;
            Food = 20;
            Saturation = 5;
            _exhaustion = 0;
            _mgr.RespawnPlayer();
        }

        public bool Eat(ItemDef food)
        {
            if (food == null || !food.IsFood || Food >= 20) return false;
            Food = Mathf.Min(20, Food + food.Food);
            Saturation = Mathf.Min(Food, Saturation + food.Saturation);
            _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "burp", GTPlayer.Instance.headCollider.transform.position, 0.6f);
            return true;
        }

        /// <summary>Hold food at your mouth (or hold the trigger with it) to eat, crunching like Minecraft.</summary>
        public void TickEating(bool eating, float dt)
        {
            var held = Inv.Held;
            if (!eating || held.Empty || !held.Item.IsFood || Food >= 20 || Dead) { EatProgress = 0; return; }
            EatProgress += dt / 1.6f;
            if (Time.time >= _eatSound)
            {
                _eatSound = Time.time + 0.22f;
                var mouth = GTPlayer.Instance.headCollider.transform.position;
                _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "eat", mouth, 0.5f, UnityEngine.Random.Range(0.8f, 1.2f));
                _mgr.Fx.BurstItem(held.Item, mouth + GTPlayer.Instance.headCollider.transform.forward * 0.1f - Vector3.up * 0.08f, 3);
            }
            if (EatProgress >= 1f)
            {
                EatProgress = 0;
                if (Eat(held.Item)) Inv.UseHeld();
            }
        }

        public JObject ToJson() => new JObject
        {
            ["inventory"] = Inv.ToJson(), ["health"] = Health, ["food"] = Food, ["saturation"] = Saturation,
        };

        public void FromJson(JObject o)
        {
            if (o == null) return;
            Inv.FromJson(o["inventory"] as JObject);
            Health = (float?)o["health"] ?? 20;
            Food = (float?)o["food"] ?? 20;
            Saturation = (float?)o["saturation"] ?? 5;
            if (Health <= 0) Health = 20;
        }
    }

    /// <summary>A dropped item: spins, bobs, falls, and jumps into your inventory when you come close.</summary>
    public class ItemEntity : MonoBehaviour
    {
        public static readonly List<ItemEntity> Live = new List<ItemEntity>();
        public ItemStack Stack;
        public VoxelWorld World;
        private Vector3 _vel;
        private float _age, _spin;
        private MergeManager _mgr;
        private Material _mat;

        public static ItemEntity Spawn(MergeManager mgr, VoxelWorld w, Vector3 pos, ItemStack stack, Vector3 vel)
        {
            if (stack.Empty) return null;
            float s = w.S;
            GameObject go;
            Material mat;
            if (stack.Item.Block != null)
            {
                mat = Materials.VertexLit(BlockRegistry.Atlas);
                go = MeshUtil.Object("GM_drop", MeshUtil.BlockCube(stack.Item.Block, s * 0.25f), mat);
            }
            else
            {
                mat = Materials.VertexLitTransparent(Items.Atlas, Color.white);
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
                go = MeshUtil.Object("GM_drop", ItemQuad(stack.Item, s * 0.45f), mat);
            }
            go.transform.position = pos;
            var e = go.AddComponent<ItemEntity>();
            e.Stack = stack;
            e.World = w;
            e._vel = vel * s;
            e._mgr = mgr;
            e._mat = mat;
            e._spin = UnityEngine.Random.Range(0f, 360f);
            Live.Add(e);
            return e;
        }

        public static Mesh ItemQuad(ItemDef d, float size)
        {
            var r = d.AtlasUV;
            float h = size / 2f;
            var m = new Mesh { name = "GM_itemquad" };
            m.vertices = new[] { new Vector3(-h, -h, 0), new Vector3(-h, h, 0), new Vector3(h, h, 0), new Vector3(h, -h, 0) };
            m.uv = new[] { new Vector2(r.x, r.y), new Vector2(r.x, r.w), new Vector2(r.z, r.w), new Vector2(r.z, r.y) };
            m.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        private void OnDestroy() => Live.Remove(this);

        private void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_age > 300f || World == null) { Destroy(gameObject); return; }
            if (!World.Root.activeInHierarchy) { foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false; return; }
            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;

            float s = World.S;
            _vel += Physics.gravity * dt * (s / 0.5f) * 0.6f;
            var next = transform.position + _vel * dt;
            if (World.IsSolidAt(next - Vector3.up * s * 0.15f))
            {
                _vel = new Vector3(_vel.x * 0.5f, 0, _vel.z * 0.5f);
                next = transform.position;
            }
            if (World.IsSolidAt(next)) { next = transform.position + Vector3.up * s * dt * 2f; _vel = Vector3.zero; }
            transform.position = next;
            _spin += dt * 90f;
            transform.rotation = Quaternion.Euler(0, _spin, 0);
            var light = _mgr.LightAt(World, transform.position);
            Materials.SetColor(_mat, new Color(light, light, light, 1f));

            // Picked up by your head or hands.
            if (_age < 0.6f || _mgr.Player == null || _mgr.Player.Dead) return;
            var gp = GTPlayer.Instance;
            float reach = Mathf.Max(0.55f, s * 1.6f);
            var p = transform.position;
            if ((gp.headCollider.transform.position - p).sqrMagnitude < reach * reach * 1.6f ||
                (gp.GetHandPosition(true) - p).sqrMagnitude < reach * reach ||
                (gp.GetHandPosition(false) - p).sqrMagnitude < reach * reach ||
                gp.bodyCollider.bounds.SqrDistance(p) < reach * reach * 0.5f)
            {
                int left = _mgr.Player.Inv.Add(Stack.Item, Stack.Count);
                if (left < Stack.Count) _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "pickup", p, 0.4f, UnityEngine.Random.Range(1.4f, 2f));
                if (left <= 0) Destroy(gameObject);
                else Stack.Count = left;
            }
        }
    }

    /// <summary>A furnace in the world: cooks its input using fuel even while its menu is closed.</summary>
    public class Furnace
    {
        public ItemStack In, Fuel, Out;
        public float Burn, BurnMax, Cook;

        public bool Lit => Burn > 0;

        public void Tick(float dt)
        {
            var recipe = In.Empty ? null : Recipes.SmeltFor(In.Item);
            bool canCook = recipe != null && (Out.Empty || (Out.Item == recipe.Result && Out.Count < Out.Item.MaxStack));
            if (Burn > 0) Burn -= dt;
            if (Burn <= 0 && canCook && !Fuel.Empty && Fuel.Item.Fuel > 0)
            {
                BurnMax = Burn = Fuel.Item.Fuel / 20f;
                Fuel.Count--;
                if (Fuel.Count <= 0) Fuel = ItemStack.None;
            }
            if (Burn > 0 && canCook)
            {
                Cook += dt;
                if (Cook >= recipe.Seconds)
                {
                    Cook = 0;
                    In.Count--;
                    if (In.Count <= 0) In = ItemStack.None;
                    if (Out.Empty) Out = new ItemStack(recipe.Result, 1);
                    else Out.Count++;
                }
            }
            else Cook = Mathf.Max(0, Cook - dt * 2f);
        }

        static JObject S(ItemStack s) => s.Empty ? null : new JObject { ["item"] = s.Item.Key, ["count"] = s.Count };
        static ItemStack L(JToken t) { var d = t is JObject o ? Items.Get((string)o["item"]) : null; return d == null ? ItemStack.None : new ItemStack(d, (int?)t["count"] ?? 1); }

        public JObject ToJson() => new JObject { ["in"] = S(In), ["fuel"] = S(Fuel), ["out"] = S(Out), ["burn"] = Burn, ["burnMax"] = BurnMax, ["cook"] = Cook };

        public static Furnace FromJson(JObject o) => new Furnace
        {
            In = L(o["in"]), Fuel = L(o["fuel"]), Out = L(o["out"]),
            Burn = (float?)o["burn"] ?? 0, BurnMax = (float?)o["burnMax"] ?? 0, Cook = (float?)o["cook"] ?? 0,
        };
    }
}
