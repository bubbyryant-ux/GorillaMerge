using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using GorillaLocomotion;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GorillaMerge
{
    /// <summary>
    /// Runs everything: loads the packs, keeps the worlds, moves you between Gorilla Tag and the pack worlds,
    /// runs survival (health, items, furnaces, mobs, day and night) and turns it all off in public lobbies.
    /// </summary>
    public class MergeManager : MonoBehaviour
    {
        public static MergeManager Instance { get; private set; }

        public SoundBank Sounds;
        public Particles Fx;
        public Cracks Cracks;
        public Hud Hud;
        public ContainerGui Gui;
        public readonly List<Pack> Packs = new List<Pack>();
        public readonly List<Pack> VoxelPacks = new List<Pack>();
        public Pack BuildPack;
        public Pack UiPack => BuildPack;
        public Pack SurvivalPack => CurrentPack != null && CurrentPack.Type == PackType.Voxel ? CurrentPack : BuildPack;
        public Pack CurrentPack { get; private set; }
        public VoxelWorld BuildWorld;
        public bool BuildMode = true;
        public float Daylight { get; private set; } = 1f;

        /// <summary>
        /// Your survival state for the pack you're playing with. It goes with you: blocks you collect in the
        /// Minecraft world are the ones you can build with back in Gorilla Tag's own maps.
        /// </summary>
        public Survival Player { get { var p = ItemPack; return p != null ? EnsurePlayer(p) : null; } }
        public bool Survival { get { var p = ItemPack; return p != null && _survivalMode.TryGetValue(p, out bool on) && on && Player != null; } }
        public bool InPackWorld => CurrentPack != null && CurrentPack.Type == PackType.Voxel;

        private readonly Dictionary<Pack, VoxelWorld> _voxelWorlds = new Dictionary<Pack, VoxelWorld>();
        private readonly Dictionary<Pack, ObjMap> _maps = new Dictionary<Pack, ObjMap>();
        private readonly Dictionary<Pack, Sky> _skies = new Dictionary<Pack, Sky>();
        private readonly Dictionary<Pack, int> _selected = new Dictionary<Pack, int>();
        private readonly Dictionary<Pack, Survival> _players = new Dictionary<Pack, Survival>();
        private readonly Dictionary<Pack, bool> _survivalMode = new Dictionary<Pack, bool>();
        private readonly Dictionary<string, Dictionary<Vector3Int, Furnace>> _furnaces = new Dictionary<string, Dictionary<Vector3Int, Furnace>>(); // by world id
        private readonly Dictionary<Pack, List<MobDef>> _mobs = new Dictionary<Pack, List<MobDef>>();
        private readonly MobSpawner _spawner = new MobSpawner();
        private HandBuilder _left, _right;
        private HeldItemView _held;
        private readonly Dictionary<VoxelWorld, HashSet<Vector3Int>> _crops = new Dictionary<VoxelWorld, HashSet<Vector3Int>>();
        private float _cropTick;
        private WristUI _ui;
        private int _layer;
        private Vector3 _anchor, _returnPos;
        private Quaternion _returnRot = Quaternion.identity;
        private bool _ready, _wasAllowed, _chordWas, _loading;
        private float _travelReadyAt, _saveAt;
        private VoxelWorld _loadingWorld;

        private VoxelWorld _punchWorld;
        private Vector3Int _punchBlock;
        private float _punchProgress, _punchTime;

        /// <summary>Playing in one world with friends who have the mod (modded lobbies only).</summary>
        public Multiplayer Net;
        private readonly HashSet<VoxelWorld> _friendWorlds = new HashSet<VoxelWorld>(); // copies of a friend's world, never saved
        private string _session = "";
        private float _timeSyncAt;

        private static string SaveDir => Path.Combine(Paths.ConfigPath, "GorillaMerge");
        private static string PacksDir => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "Packs");
        private static float S => Mathf.Clamp(Plugin.Instance.BlockSize.Value, 0.1f, 2f);

        private void Awake() => Instance = this;

        private IEnumerator Start()
        {
            while (GTPlayer.Instance == null || GorillaTagger.Instance == null || ControllerInputPoller.instance == null)
                yield return null;
            yield return new WaitForSeconds(1f);
            try
            {
                Init();
                _ready = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("GorillaMerge failed to start: " + ex);
                yield break;
            }
            if (Plugin.Instance.AutoTest.Value) StartCoroutine(AutoTest());
        }

        private void Init()
        {
            var gp = GTPlayer.Instance;
            _layer = LocomotionLayer(gp);
            _anchor = gp.transform.position;
            Surfaces.Init(gp);
            GameAssets.InitMinecraft(Plugin.Instance.MinecraftVersion.Value);
            McFont.Init();

            Packs.AddRange(Pack.LoadAll(PacksDir));
            foreach (var p in Packs)
            {
                if (p.Type != PackType.Voxel) continue;
                BlockRegistry.AddPack(p);
                if (p.Items.Count > 0) VoxelPacks.Add(p);
            }
            foreach (var b in BlockRegistry.All) b.SurfaceIndex = Surfaces.Resolve(b.Surface);
            BlockRegistry.BuildAtlas();
            foreach (var p in VoxelPacks) Items.AddPack(p);
            Items.BuildAtlas();
            Recipes.LoadMinecraft();
            foreach (var p in VoxelPacks) _mobs[p] = MobDef.Load(p);
            BuildPack = VoxelPacks.Find(p => string.Equals(p.Id, Plugin.Instance.BuildPack.Value, StringComparison.OrdinalIgnoreCase))
                        ?? (VoxelPacks.Count > 0 ? VoxelPacks[0] : null);

            Sounds = gameObject.AddComponent<SoundBank>();
            Fx = gameObject.AddComponent<Particles>();
            Cracks = new Cracks(transform);
            foreach (var p in Packs) Sounds.PreloadPack(p);
            foreach (var list in _mobs.Values)
                foreach (var m in list) foreach (var snd in m.Sounds()) Sounds.Preload(snd, m.Pack.Dir);

            BuildWorld = new VoxelWorld("build", null, Vector3.zero, S, _layer);
            BuildWorld.Load(Path.Combine(SaveDir, "build.gmw"));
            LoadFurnaces();
            Net = new Multiplayer(this);

            _left = new HandBuilder(true, transform);
            _right = new HandBuilder(false, transform);
            _ui = new WristUI();
            _held = new HeldItemView();
            Hud = new Hud();
            Gui = new ContainerGui();
            _saveAt = Time.time + 60f;
            Plugin.Log.LogInfo($"Ready: {Packs.Count} pack(s), building with {(BuildPack != null ? BuildPack.Name : "nothing")}, layer {_layer} ({LayerMask.LayerToName(_layer)}), lighting {(BlockRegistry.VertexLighting ? "smooth" : "flat")}.");
        }

        private static int LocomotionLayer(GTPlayer gp)
        {
            int mask = gp.locomotionEnabledLayers.value;
            if (mask == 0) mask = GTPlayer.LocomotionEnabledLayers.value;
            if ((mask & 1) != 0) return 0;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        // ---------- Every frame ----------

        private void Update()
        {
            if (!_ready) return;
            var gp = GTPlayer.Instance;
            if (gp == null) return;
            float dt = Time.deltaTime;
            var head = gp.headCollider.transform.position;

            bool allowed = Plugin.Allowed;
            if (!allowed && CurrentPack != null) Travel(null, true);
            if (!allowed && Gui.Open) Gui.Close();
            if (allowed != _wasAllowed)
            {
                _wasAllowed = allowed;
                Plugin.Log.LogInfo(allowed ? "Building allowed (offline or modded lobby)." : "Public lobby: GorillaMerge is off.");
            }

            // Friends: join the owner's world, and drop our copy when the owner changes or everyone leaves.
            Net.Tick();
            if (Net.SessionKey != _session)
            {
                _session = Net.SessionKey;
                OnFriendsChanged();
            }
            if (Net.IsOwner && Time.time >= _timeSyncAt)
            {
                _timeSyncAt = Time.time + 10f;
                if (CurrentPack != null && _voxelWorlds.TryGetValue(CurrentPack, out var ownWorld) && !_friendWorlds.Contains(ownWorld))
                    Net.SendTime(CurrentPack.Id, ownWorld.TimeOfDay);
            }

            BuildWorld.SetVisible(allowed && (CurrentPack == null || CurrentPack.Type == PackType.Map));
            foreach (var kv in _voxelWorlds) kv.Value.SetVisible(allowed && CurrentPack == kv.Key);
            foreach (var kv in _maps) kv.Value.SetVisible(allowed && CurrentPack == kv.Key);
            foreach (var kv in _skies) kv.Value.SetVisible(allowed && CurrentPack == kv.Key);

            // Time of day and the sky.
            var world = ActiveWorld;
            Daylight = 1f;
            if (allowed && CurrentPack != null && _skies.TryGetValue(CurrentPack, out var sky))
            {
                float t = 0.25f;
                if (world != BuildWorld)
                {
                    world.TimeOfDay += dt / Mathf.Max(30f, Plugin.Instance.DayLength.Value * 60f);
                    t = world.TimeOfDay;
                }
                sky.Tick(head, t - Mathf.Floor(t), Mathf.FloorToInt(t));
                Daylight = sky.Daylight;
            }

            // Stream and mesh the worlds.
            int radius = Mathf.Clamp(Plugin.Instance.RenderDistance.Value, 2, 16);
            if (allowed)
            {
                if (world == BuildWorld || CurrentPack == null) BuildWorld.Tick(head, radius, 1f);
                else world.Tick(head, radius, Daylight);
            }
            if (_loadingWorld != null && _loadingWorld != world) _loadingWorld.Tick(_loadingWorld.Center(_loadingWorld.SpawnBlock), Mathf.Min(radius, 4), 1f, 6f);

            // Hands.
            var items = ActiveItems;
            int sel = Selected;
            bool canBuild = allowed && BuildMode && world != null && (Survival || (items != null && items.Count > 0));
            if (canBuild)
            {
                var selected = items != null && items.Count > 0 ? items[Mathf.Clamp(sel, 0, items.Count - 1)] : null;
                _left.Tick(this, world, selected, dt);
                _right.Tick(this, world, selected, dt);
            }
            else { _left.Hide(); _right.Hide(); }

            // Survival, furnaces, mobs.
            if (allowed && Survival)
            {
                if (InPackWorld) Player.Tick(dt, world);
                foreach (var map in _furnaces.Values) foreach (var f in map.Values) f.Tick(dt);
                if (InPackWorld && Plugin.Instance.Mobs.Value && _mobs.TryGetValue(CurrentPack, out var defs) && !Player.Dead) _spawner.Tick(this, world, defs, Daylight);
            }
            if ((!allowed || !InPackWorld || !Survival) && Mob.Live.Count > 0)
                foreach (var m in new List<Mob>(Mob.Live)) Destroy(m.gameObject);

            // The item in your hand, and crops growing.
            if (allowed && BuildMode && !Gui.Open && !(Player != null && Player.Dead))
            {
                ItemDef heldItem = Survival ? Player.Inv.Held.Item : (items != null && items.Count > 0 ? Items.ForBlock(items[Mathf.Clamp(sel, 0, items.Count - 1)]) : null);
                _held.Tick(this, heldItem, InPackWorld ? LightAt(world, gp.GetHandPosition(Plugin.Instance.SwapHands.Value)) : 1f);
            }
            else _held.Hide();
            if (allowed && Time.time >= _cropTick) { _cropTick = Time.time + 1f; GrowCrops(world); }

            // Menus and HUD.
            Gui.Tick(this);
            var mode = allowed ? WristUI.Mode.Build
                : Plugin.InRoom && Plugin.Instance.ShowWristInPublic.Value ? WristUI.Mode.PublicNotice : WristUI.Mode.Hidden;
            _ui.Tick(this, mode, items, sel);
            if (allowed && BuildMode && !Gui.Open) Hud.Tick(Survival, Player, items, sel);
            else Hud.Hide();

            if (allowed)
            {
                DesktopKeys();
                var ip = ControllerInputPoller.instance;
                bool chord = ip != null && ip.rightControllerPrimaryButton && ip.rightControllerSecondaryButton;
                if (chord && !_chordWas) Travel(CurrentPack == null && VoxelPacks.Count > 0 ? VoxelPacks[0] : null);
                _chordWas = chord;
            }

            if (allowed && CurrentPack != null) CheckVoid(head);
            if (allowed) CheckPortal();
            Sounds.UpdateMusic(allowed ? CurrentPack : null);

            if (_punchWorld != null && Time.time - _punchTime > 3f) { _punchWorld = null; Cracks.Hide(); }
            if (Time.time >= _saveAt) { _saveAt = Time.time + 60f; SaveAll(); }
        }

        private void DesktopKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f8Key.wasPressedThisFrame) _ui.MenuOpen = !_ui.MenuOpen;
            if (kb.f9Key.wasPressedThisFrame) Travel(CurrentPack == null && VoxelPacks.Count > 0 ? VoxelPacks[0] : null);
        }

        // ---------- Items ----------

        public VoxelWorld ActiveWorld => CurrentPack != null && _voxelWorlds.TryGetValue(CurrentPack, out var w) ? w : BuildWorld;
        private Pack ItemPack => CurrentPack != null && CurrentPack.Type == PackType.Voxel ? CurrentPack : BuildPack;
        public IList<BlockDef> ActiveItems => ItemPack?.Items;

        public int Selected
        {
            get { var p = ItemPack; return p != null && _selected.TryGetValue(p, out int i) ? i : 0; }
            set
            {
                var p = ItemPack;
                if (p == null || p.Items.Count == 0) return;
                _selected[p] = ((value % p.Items.Count) + p.Items.Count) % p.Items.Count;
            }
        }

        public void CycleItem(int delta)
        {
            if (Survival) Player.Inv.Selected = ((Player.Inv.Selected + delta) % 9 + 9) % 9;
            else Selected = Selected + delta;
            try { GorillaTagger.Instance.StartVibration(delta < 0, 0.1f, 0.03f); } catch { }
        }

        public void NextBuildPack()
        {
            if (VoxelPacks.Count == 0) return;
            BuildPack = VoxelPacks[(VoxelPacks.IndexOf(BuildPack) + 1) % VoxelPacks.Count];
        }

        public void ToggleGameMode()
        {
            var p = ItemPack;
            if (p == null) return;
            _survivalMode[p] = !Survival;
            Plugin.Log.LogInfo("Game mode: " + (Survival ? "Survival" : "Creative"));
        }

        public void OpenInventory() { if (Survival) Gui.Show(ContainerGui.Kind.Inventory, Player); }
        public void OpenCrafting() { if (Survival) Gui.Show(ContainerGui.Kind.Crafting, Player); }

        public void OpenFurnace(VoxelWorld w, Vector3Int p)
        {
            if (!Survival) return;
            if (!_furnaces.TryGetValue(w.Id, out var map)) _furnaces[w.Id] = map = new Dictionary<Vector3Int, Furnace>();
            if (!map.TryGetValue(p, out var f)) map[p] = f = new Furnace();
            Gui.Show(ContainerGui.Kind.Furnace, Player, f);
        }

        // ---------- Farming and armor ----------

        public void Till(VoxelWorld w, Vector3Int p)
        {
            var farmland = w.Def(p).Pack?.Block("farmland");
            if (farmland == null || !w.Set(p, farmland.Index)) return;
            Sounds.PlayEvent(SurvivalPack, "till", w.Center(p), 0.8f);
        }

        public bool Plant(VoxelWorld w, Vector3Int p, ItemDef seeds)
        {
            var crop = seeds.Pack?.Block(seeds.Plants);
            if (crop == null || !w.Set(p, crop.Index)) return false;
            Sounds.PlayBlock(crop, "place", w.Center(p), 0.7f);
            Crops(w).Add(p);
            return true;
        }

        public bool BoneMeal(VoxelWorld w, Vector3Int p)
        {
            var def = w.Def(p);
            if (def.Special != "crop" || def.Next == null) return false;
            int steps = UnityEngine.Random.Range(2, 6);
            for (int i = 0; i < steps && def.Next != null; i++) def = def.Pack.Block(def.Next) ?? def;
            w.Set(p, def.Index);
            Sounds.PlayEvent(SurvivalPack, "bonemeal", w.Center(p), 0.7f);
            Fx.Burst(def, w.Center(p), w.S, 6);
            return true;
        }

        private HashSet<Vector3Int> Crops(VoxelWorld w)
        {
            if (!_crops.TryGetValue(w, out var set))
            {
                // First look at this world: find crops already planted (they're only in chunks you changed).
                set = new HashSet<Vector3Int>(w.FindModified(id => BlockRegistry.All[id].Special == "crop"));
                _crops[w] = set;
            }
            return set;
        }

        /// <summary>Every crop has a small chance each second to grow a stage (about 6 minutes to fully grown).</summary>
        private void GrowCrops(VoxelWorld w)
        {
            // In a friend's world their game grows the crops and sends us each change.
            if (w == null || _friendWorlds.Contains(w)) return;
            var set = Crops(w);
            if (set.Count == 0) return;
            List<Vector3Int> gone = null;
            foreach (var p in set)
            {
                var def = w.Def(p);
                if (def.Special != "crop") { (gone ??= new List<Vector3Int>()).Add(p); continue; }
                if (def.Next == null || UnityEngine.Random.value > 1f / 45f) continue;
                var next = def.Pack.Block(def.Next);
                if (next != null) w.Set(p, next.Index);
            }
            if (gone != null) foreach (var p in gone) set.Remove(p);
        }

        public void EquipHeldArmor()
        {
            var inv = Player.Inv;
            var held = inv.Held;
            int slot = Inventory.SlotIndex(held.Item.Slot);
            if (slot < 0) return;
            var old = inv.Armor[slot];
            inv.Armor[slot] = held;
            inv.Slots[inv.Selected] = old;
            Sounds.PlayEvent(SurvivalPack, "equip", GTPlayer.Instance.headCollider.transform.position, 0.8f);
        }

        // ---------- Blocks ----------

        public bool CanPlace(VoxelWorld w, Vector3Int cell)
        {
            if (!w.InBounds(cell)) return false;
            var existing = w.Def(cell);
            if (existing.Index != 0 && !existing.Liquid && existing.Render != RenderKind.Plant) return false;
            var gp = GTPlayer.Instance;
            var box = w.BlockBounds(cell);
            var head = gp.headCollider;
            float r = head.radius * head.transform.lossyScale.x;
            if (box.SqrDistance(head.transform.position) < r * r) return false;
            foreach (var m in Mob.Live) if (m.Box.Intersects(box)) return false;
            return !gp.bodyCollider.bounds.Intersects(box);
        }

        public bool PlaceBlock(VoxelWorld w, Vector3Int cell, BlockDef def)
        {
            if (!w.Set(cell, def.Index)) return false;
            Sounds.PlayBlock(def, "place", w.Center(cell));
            try { GorillaTagger.Instance.StartVibration(Plugin.Instance.SwapHands.Value, 0.15f, 0.04f); } catch { }
            return true;
        }

        /// <summary>Breaks a block. In survival you get its drops (if your tool can harvest it).</summary>
        public void BreakBlock(VoxelWorld w, Vector3Int cell, bool byPlayer)
        {
            var def = w.Def(cell);
            if (def.Index == 0 || def.Unbreakable) return;
            var center = w.Center(cell);
            w.Set(cell, 0);
            if (def.IsTnt && (!byPlayer || !Survival))
            {
                PrimedTnt.Spawn(w, def, cell, 4f);
                Sounds.PlayEvent(def.Pack, "fuse", center);
                return;
            }
            Fx.Burst(def, center, w.S, 12);
            Sounds.PlayBlock(def, "break", center);

            // Plants and torches resting on this block pop off too.
            var above = cell + Vector3Int.up;
            var up = w.Def(above);
            if (up.Render == RenderKind.Plant || up.Render == RenderKind.Torch) BreakBlock(w, above, byPlayer);

            if (!Survival) return;
            var held = Player.Inv.Held;
            if (byPlayer)
                foreach (var drop in global::GorillaMerge.Survival.DropsFor(def, held))
                    ItemEntity.Spawn(this, w, center, drop, new Vector3(UnityEngine.Random.Range(-1f, 1f), 2.5f, UnityEngine.Random.Range(-1f, 1f)));
            if (byPlayer && !held.Empty && held.Item.ToolType != null && held.Item.ToolType == def.Tool) Player.Inv.WearHeld();
            if (def.Special == "furnace" && _furnaces.TryGetValue(w.Id, out var map) && map.TryGetValue(cell, out var f))
            {
                foreach (var st in new[] { f.In, f.Fuel, f.Out }) ItemEntity.Spawn(this, w, center, st, Vector3.up * 2f);
                map.Remove(cell);
            }
        }

        /// <summary>A fist punch: each one is worth a bit of mining time.</summary>
        public void Punch(VoxelWorld w, Vector3Int cell, bool leftHand)
        {
            var def = w.Def(cell);
            if (def.Index == 0 || def.Unbreakable) return;
            if (_punchWorld != w || _punchBlock != cell) { _punchWorld = w; _punchBlock = cell; _punchProgress = 0; }
            float seconds = Survival ? global::GorillaMerge.Survival.BreakSeconds(def, leftHand == Plugin.Instance.SwapHands.Value ? Player.Inv.Held : ItemStack.None) : def.BreakTime;
            _punchProgress += Plugin.Instance.PunchPower.Value / Mathf.Max(0.05f, seconds);
            _punchTime = Time.time;
            try { GorillaTagger.Instance.StartVibration(leftHand, 0.3f, 0.05f); } catch { }
            if (_punchProgress >= 1f || (def.IsTnt && !Survival))
            {
                _punchWorld = null;
                Cracks.Hide();
                BreakBlock(w, cell, true);
            }
            else
            {
                Cracks.Show(w, cell, _punchProgress);
                Fx.Burst(def, w.Center(cell), w.S, 3);
            }
        }

        /// <summary>TNT and creepers: removes blocks in a ball, sets off other TNT, hurts and throws you.</summary>
        public void Explode(VoxelWorld w, Vector3 center, float radiusBlocks, Pack pack)
        {
            var c = w.ToBlock(center);
            int r = Mathf.CeilToInt(radiusBlocks);
            for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                    {
                        float d = Mathf.Sqrt(x * x + y * y + z * z);
                        if (d > radiusBlocks + UnityEngine.Random.Range(-0.6f, 0.3f)) continue;
                        var p = c + new Vector3Int(x, y, z);
                        var def = w.Def(p);
                        if (def.Index == 0 || def.Unbreakable || def.Hardness >= 40f || def.IsPortal || def.Liquid) continue;
                        w.Set(p, 0);
                        if (def.IsTnt) PrimedTnt.Spawn(w, def, p, UnityEngine.Random.Range(0.5f, 1.5f));
                        else if (Survival && UnityEngine.Random.value < 0.3f)
                            foreach (var drop in global::GorillaMerge.Survival.DropsFor(def, ItemStack.None))
                                ItemEntity.Spawn(this, w, w.Center(p), drop, UnityEngine.Random.insideUnitSphere * 2f);
                        else if (UnityEngine.Random.value < 0.15f) Fx.Burst(def, w.Center(p), w.S, 4);
                    }
            Sounds.PlayEvent(pack, "explode", center, 1f, UnityEngine.Random.Range(0.85f, 1.05f));
            Fx.Puff(center, w.S * radiusBlocks);
            foreach (var m in new List<Mob>(Mob.Live))
                if (Vector3.Distance(m.Box.center, center) < radiusBlocks * w.S * 2f) m.Hurt(12f, center, false);

            var gp = GTPlayer.Instance;
            if (gp == null) return;
            var head = gp.headCollider.transform.position;
            float reach = radiusBlocks * w.S * 2.2f;
            float dist = Vector3.Distance(head, center);
            if (dist < reach)
            {
                var dir = (head - center).normalized;
                dir.y = Mathf.Abs(dir.y) + 0.5f;
                float speed = Mathf.Lerp(16f, 4f, dist / reach) * Mathf.Sqrt(w.S / 0.5f);
                gp.ApplyKnockback(dir.normalized, speed, true);
                try { GorillaTagger.Instance.StartVibration(true, 0.8f, 0.3f); GorillaTagger.Instance.StartVibration(false, 0.8f, 0.3f); } catch { }
                if (Survival && InPackWorld) Player.Damage(Mathf.Lerp(18f, 2f, dist / reach), "blew up", center);
            }
        }

        /// <summary>Roughly how lit a spot is (0..1), for mobs and dropped items.</summary>
        public float LightAt(VoxelWorld w, Vector3 pos)
        {
            if (!BlockRegistry.VertexLighting) return 1f;
            var b = w.ToBlock(pos);
            float dayLevel = Mathf.Lerp(4f, 15f, Daylight);
            float sky = w.SkyOpen(b) >= 15 ? dayLevel : (w.HeightAt(b.x, b.z) - b.y < 4 ? dayLevel - 4 : 0f);
            float blk = 0;
            for (int dy = -3; dy <= 3; dy++)
                for (int dz = -5; dz <= 5; dz++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        int e = BlockRegistry.EmitT[w.Get(b.x + dx, b.y + dy, b.z + dz)];
                        if (e > 0) blk = Mathf.Max(blk, e - Mathf.Abs(dx) - Mathf.Abs(dy) - Mathf.Abs(dz));
                    }
            return Mathf.Max(0.12f, VoxelWorld.Brightness(Mathf.Max(sky, blk)));
        }

        // ---------- Travel ----------

        public void Travel(Pack target, bool force = false)
        {
            if (_loading && !force) return;
            if (!force && Time.time < _travelReadyAt) return;
            if (target == CurrentPack) return;
            if (!force && target != null && !Plugin.Allowed) return;
            if (target != null && target.Type == PackType.Voxel && !_voxelReady.Contains(target)) { StartCoroutine(TravelToVoxel(target)); return; }
            DoTravel(target);
        }

        private readonly HashSet<Pack> _voxelReady = new HashSet<Pack>();

        // Endless worlds: generate the area around spawn first (Minecraft's "Loading terrain"), then go.
        private IEnumerator TravelToVoxel(Pack target)
        {
            _loading = true;
            VoxelWorld w = null;
            if (Net.Active && !Net.IsOwner && !_voxelWorlds.ContainsKey(target))
            {
                // A friend owns the world in this room: ask their game for a copy of it.
                Plugin.Log.LogInfo($"Joining {Net.OwnerName}'s world...");
                Net.RequestWorld(target);
                float wait = Time.time + 30f;
                Multiplayer.WorldData data = null;
                while (Time.time < wait && Net.Active && !Net.IsOwner && !Net.WasRefused(target.Id) && !Net.TryTakeWorld(target.Id, out data))
                    yield return null;
                if (data != null) w = AddFriendWorld(target, data);
                if (w == null) Plugin.Log.LogWarning("Couldn't get your friend's world, so this is your own one (not shared).");
            }
            if (w == null) w = EnsureVoxelWorld(target);
            _loadingWorld = w;
            float until = Time.time + 20f;
            Plugin.Log.LogInfo("Loading terrain...");
            while (Time.time < until && !SpawnReady(w)) yield return null;
            _loadingWorld = null;
            _loading = false;
            _voxelReady.Add(target);
            if (Plugin.Allowed) DoTravel(target);
        }

        private bool SpawnReady(VoxelWorld w)
        {
            var b = w.SpawnBlock;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!w.IsLoaded(b + new Vector3Int(dx * 16, 0, dz * 16))) return false;
            return w.PendingJobs == 0 && w.LoadedColumns >= 25;
        }

        private void DoTravel(Pack target)
        {
            var gp = GTPlayer.Instance;
            if (gp == null) return;
            try
            {
                if (Gui.Open) Gui.Close();
                if (CurrentPack == null)
                {
                    _returnPos = gp.headCollider.transform.position;
                    _returnRot = gp.transform.rotation;
                }
                SaveAll();

                Vector3 dest;
                if (target == null) dest = _returnPos != Vector3.zero ? _returnPos : _anchor + Vector3.up;
                else if (target.Type == PackType.Voxel) dest = SpawnHead(EnsureVoxelWorld(target));
                else
                {
                    var map = EnsureMap(target);
                    if (map == null) return;
                    dest = map.SpawnPoint + Vector3.up * 0.9f;
                }

                var leaving = CurrentPack;
                CurrentPack = target;
                foreach (var kv in _voxelWorlds) kv.Value.SetVisible(CurrentPack == kv.Key);
                foreach (var kv in _maps) kv.Value.SetVisible(CurrentPack == kv.Key);
                foreach (var kv in _skies) kv.Value.SetVisible(CurrentPack == kv.Key);
                BuildWorld.SetVisible(CurrentPack == null || CurrentPack.Type == PackType.Map);
                Physics.SyncTransforms();

                gp.TeleportTo(dest, target == null ? _returnRot : gp.transform.rotation, false, true);
                Sounds.StopMusic();
                Sounds.PlayEvent(target ?? leaving, "travel", dest, 0.6f);
                _travelReadyAt = Time.time + 2.5f;
                _ui.MenuOpen = false;
                Plugin.Log.LogInfo("Travelled to " + (target != null ? target.Name : "Gorilla Tag"));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Travel failed: " + ex);
            }
        }

        private Vector3 SpawnHead(VoxelWorld w)
        {
            var b = w.SpawnBlock;
            int ground = w.TopSolid(b.x, b.z, TerrainGen.Height - 1, 0);
            if (ground != int.MinValue) b.y = ground + 1;
            return w.Corner(b) + new Vector3(w.S * 0.5f, 0.9f, w.S * 0.5f);
        }

        public void RespawnPlayer()
        {
            if (CurrentPack == null || !_voxelWorlds.TryGetValue(CurrentPack, out var w)) return;
            GTPlayer.Instance.TeleportTo(SpawnHead(w), GTPlayer.Instance.transform.rotation, false, true);
        }

        private VoxelWorld EnsureVoxelWorld(Pack pack)
        {
            if (_voxelWorlds.TryGetValue(pack, out var existing)) return existing;
            int index = VoxelPacks.IndexOf(pack);
            int seed = Plugin.Instance.WorldSeed.Value + index;
            var gen = new TerrainGen(pack, seed);
            // World block (0,0,0) sits so the spawn point is straight above Gorilla Tag's stump, high in the sky.
            var origin = _anchor + new Vector3(0, Plugin.Instance.WorldHeightOffset.Value + index * 200f, 0)
                         - new Vector3((gen.Spawn.x + 0.5f) * S, 0, (gen.Spawn.z + 0.5f) * S);
            var w = new VoxelWorld(pack.Id, pack, origin, S, _layer, gen) { SpawnBlock = gen.Spawn };
            string save = Path.Combine(SaveDir, pack.Id + "_world.gmw");
            if (w.Load(save)) w.SpawnBlock = gen.Spawn;
            AddWorld(pack, w);
            Plugin.Log.LogInfo($"World '{pack.Name}': endless, seed {seed}, spawn {gen.Spawn}.");
            return w;
        }

        private void AddWorld(Pack pack, VoxelWorld w)
        {
            w.SetVisible(false);
            w.Changed = (p, id) => { if (IsShared(w)) Net.QueueEdit(pack.Id, p, id); };
            _voxelWorlds[pack] = w;
            var sky = new Sky(pack, w.Origin.y + 150 * w.S, w.S);
            sky.SetVisible(false);
            _skies[pack] = sky;
            EnsurePlayer(pack);
        }

        // ---------- Friends ----------

        /// <summary>Is this the world we're sharing right now: our own as the owner, or our copy of the owner's.</summary>
        private bool IsShared(VoxelWorld w)
        {
            if (!Net.Active || w == null || w == BuildWorld || w.Pack == null) return false;
            if (!_voxelWorlds.TryGetValue(w.Pack, out var cur) || cur != w) return false;
            return Net.IsOwner ? !_friendWorlds.Contains(w) : _friendWorlds.Contains(w);
        }

        /// <summary>A copy of the owner's world: their seed, their place in the sky, their changes.</summary>
        private VoxelWorld AddFriendWorld(Pack pack, Multiplayer.WorldData data)
        {
            if (_voxelWorlds.ContainsKey(pack)) UnloadWorld(pack, true);
            var gen = new TerrainGen(pack, data.Seed);
            var w = new VoxelWorld(pack.Id + "@friend", pack, data.Origin, data.BlockSize, _layer, gen) { SpawnBlock = gen.Spawn };
            if (!w.LoadBytes(data.Bytes)) { w.Destroy(); return null; }
            w.SpawnBlock = gen.Spawn;
            w.TimeOfDay = data.Time;
            _friendWorlds.Add(w);
            AddWorld(pack, w);
            var early = Net.TakeEarlyEdits(pack.Id);
            if (early != null) ApplyFriendEdits(pack.Id, early);
            Plugin.Log.LogInfo($"Joined {Net.OwnerName}'s world ({data.Bytes.Length / 1024} KB, seed {data.Seed}).");
            return w;
        }

        /// <summary>Takes a world out of memory (saving it first if it's yours), so the next trip loads the right one.</summary>
        private void UnloadWorld(Pack pack, bool save)
        {
            if (!_voxelWorlds.TryGetValue(pack, out var w)) return;
            if (CurrentPack == pack) DoTravel(null);
            bool friends = _friendWorlds.Remove(w);
            if (save && !friends) w.Save(Path.Combine(SaveDir, pack.Id + "_world.gmw"));
            w.Destroy();
            _voxelWorlds.Remove(pack);
            _voxelReady.Remove(pack);
            _crops.Remove(w);
            if (friends) _furnaces.Remove(w.Id);
            if (_skies.TryGetValue(pack, out var sky)) { sky.Destroy(); _skies.Remove(pack); }
        }

        /// <summary>
        /// Someone joined or left. If a friend owns the world now, put ours away (saved) and, if we were in it,
        /// go straight to theirs. Copies of a world whose owner left are thrown away.
        /// </summary>
        private void OnFriendsChanged()
        {
            Pack wasIn = InPackWorld ? CurrentPack : null;
            if (Net.IsOwner) BackupWorlds();
            foreach (var pack in new List<Pack>(_voxelWorlds.Keys))
                if (_friendWorlds.Contains(_voxelWorlds[pack]) || (Net.Active && !Net.IsOwner)) UnloadWorld(pack, true);
            if (wasIn != null && CurrentPack == null && Plugin.Allowed) Travel(wasIn, true);
        }

        /// <summary>
        /// Friends can now change your world, so keep a copy of it from before they came
        /// (BepInEx\config\GorillaMerge\backups, the newest 10). Copy one back over the save to undo griefing.
        /// </summary>
        private void BackupWorlds()
        {
            try
            {
                SaveAll();
                string dir = Path.Combine(SaveDir, "backups");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                foreach (var pack in VoxelPacks)
                {
                    string save = Path.Combine(SaveDir, pack.Id + "_world.gmw");
                    if (!File.Exists(save)) continue;
                    File.Copy(save, Path.Combine(dir, $"{pack.Id}_world_{stamp}.gmw"), true);
                    var old = Directory.GetFiles(dir, pack.Id + "_world_*.gmw").OrderByDescending(f => f).Skip(10);
                    foreach (var f in old) File.Delete(f);
                }
                Plugin.Log.LogInfo("Backed up your world before friends joined.");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World backup failed: " + ex.Message); }
        }

        /// <summary>The owner's game: the world to send to a friend who asked for it.</summary>
        public VoxelWorld WorldToShare(string packId)
        {
            var pack = VoxelPacks.Find(p => p.Id == packId);
            if (pack == null || !Net.IsOwner) return null;
            if (_voxelWorlds.TryGetValue(pack, out var w) && _friendWorlds.Contains(w)) return null;
            return EnsureVoxelWorld(pack);
        }

        /// <summary>Blocks a friend changed. False if we don't have that world yet (it's still on its way).</summary>
        public bool ApplyFriendEdits(string packId, List<(Vector3Int p, ushort id)> edits)
        {
            var pack = VoxelPacks.Find(p => p.Id == packId);
            if (pack == null) return true;
            if (!_voxelWorlds.TryGetValue(pack, out var w))
            {
                if (!Net.IsOwner) return false;
                w = EnsureVoxelWorld(pack); // the owner keeps every change, even while back in Gorilla Tag
            }
            if (!IsShared(w)) return !(Net.Active && !Net.IsOwner);
            var head = GTPlayer.Instance.headCollider.transform.position;
            foreach (var (p, id) in edits)
            {
                var old = w.Def(p);
                if (!w.ApplyRemote(p, id)) continue;
                var now = BlockRegistry.All[id];
                if (now.Special == "crop") Crops(w).Add(p);
                if (edits.Count > 8 || (w.Center(p) - head).sqrMagnitude > 24f * 24f) continue;
                // Close by: hear and see it, like in Minecraft.
                if (id == 0 && old.Index != 0) { Sounds.PlayBlock(old, "break", w.Center(p)); Fx.Burst(old, w.Center(p), w.S, 8); }
                else if (id != 0) Sounds.PlayBlock(now, "place", w.Center(p));
            }
            return true;
        }

        public void SetFriendTime(string packId, float time)
        {
            var pack = VoxelPacks.Find(p => p.Id == packId);
            if (pack != null && _voxelWorlds.TryGetValue(pack, out var w) && _friendWorlds.Contains(w)) w.TimeOfDay = time;
        }

        /// <summary>Your inventory, health and hunger for a pack, loaded from its save the first time.</summary>
        private Survival EnsurePlayer(Pack pack)
        {
            if (_players.TryGetValue(pack, out var existing)) return existing;
            var player = new Survival(this);
            _players[pack] = player;
            try
            {
                string pf = Path.Combine(SaveDir, pack.Id + "_player.json");
                if (File.Exists(pf))
                {
                    var o = JObject.Parse(File.ReadAllText(pf));
                    player.FromJson(o);
                    _survivalMode[pack] = (bool?)o["survival"] ?? true;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Couldn't load player: " + ex.Message); }
            if (!_survivalMode.ContainsKey(pack)) _survivalMode[pack] = !string.Equals(Plugin.Instance.GameMode.Value, "Creative", StringComparison.OrdinalIgnoreCase);
            return player;
        }

        private static string FurnaceFile => Path.Combine(SaveDir, "furnaces.json");

        private void LoadFurnaces()
        {
            try
            {
                if (!File.Exists(FurnaceFile)) return;
                var o = JObject.Parse(File.ReadAllText(FurnaceFile));
                foreach (var world in o.Properties())
                {
                    var map = new Dictionary<Vector3Int, Furnace>();
                    foreach (var p in ((JObject)world.Value).Properties())
                    {
                        var parts = p.Name.Split(',');
                        map[new Vector3Int(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]))] = Furnace.FromJson((JObject)p.Value);
                    }
                    _furnaces[world.Name] = map;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Couldn't load furnaces: " + ex.Message); }
        }

        private ObjMap EnsureMap(Pack pack)
        {
            if (_maps.TryGetValue(pack, out var existing)) return existing;
            try
            {
                int index = Packs.IndexOf(pack);
                var origin = _anchor + new Vector3(index * 600f, Plugin.Instance.WorldHeightOffset.Value, 900f);
                var map = ObjMap.Load(pack, origin, _layer, Surfaces.Resolve(pack.Surface));
                _maps[pack] = map;
                _skies[pack] = new Sky(pack, map.Bounds.max.y + 30f, S);
                return map;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Map '{pack.Name}' failed to load: {ex.Message}");
                return null;
            }
        }

        private void CheckVoid(Vector3 head)
        {
            if (_voxelWorlds.TryGetValue(CurrentPack, out var w))
            {
                // Survival: the void hurts (Survival.Tick). Creative: it just catches you.
                if (!Survival && head.y < w.Origin.y - 20f) GTPlayer.Instance.TeleportTo(SpawnHead(w), GTPlayer.Instance.transform.rotation, false, true);
                if (head.y < w.Origin.y - 60f) RespawnPlayer();
            }
            else if (_maps.TryGetValue(CurrentPack, out var map) && head.y < map.Bounds.min.y - 30f)
                GTPlayer.Instance.TeleportTo(map.SpawnPoint + Vector3.up * 0.9f, GTPlayer.Instance.transform.rotation, false, true);
        }

        private void CheckPortal()
        {
            if (Time.time < _travelReadyAt || _loading) return;
            var gp = GTPlayer.Instance;
            var w = ActiveWorld;
            var def = w.Def(w.ToBlock(gp.headCollider.transform.position));
            if (!def.IsPortal) def = w.Def(w.ToBlock(gp.bodyCollider.bounds.center));
            if (!def.IsPortal) return;
            if (CurrentPack != null) Travel(null);
            else if (def.Pack != null && def.Pack.Type == PackType.Voxel) Travel(def.Pack);
        }

        // ---------- Menu actions ----------

        public void ClearCurrent()
        {
            if (CurrentPack == null)
            {
                BuildWorld.Clear();
                BuildWorld.Save(Path.Combine(SaveDir, "build.gmw"));
                return;
            }
            if (!_voxelWorlds.TryGetValue(CurrentPack, out var w)) return;
            // Not while friends are playing in it with you.
            if (Net.Active) return;
            // A brand-new world: new seed, fresh inventory.
            var pack = CurrentPack;
            Plugin.Instance.WorldSeed.Value = UnityEngine.Random.Range(1, int.MaxValue / 2);
            DoTravel(null);
            w.Destroy();
            _voxelWorlds.Remove(pack);
            _voxelReady.Remove(pack);
            _players.Remove(pack);
            _furnaces.Remove(pack.Id);
            if (_skies.TryGetValue(pack, out var oldSky)) { oldSky.SetVisible(false); _skies.Remove(pack); }
            try { File.Delete(Path.Combine(SaveDir, pack.Id + "_world.gmw")); File.Delete(Path.Combine(SaveDir, pack.Id + "_player.json")); } catch { }
            _travelReadyAt = 0;
            Travel(pack);
        }

        public void GoOffline()
        {
            try
            {
                if (NetworkSystem.Instance != null && NetworkSystem.Instance.InRoom)
                {
                    Plugin.Log.LogInfo("Leaving the room (Go Offline pressed).");
                    _ = NetworkSystem.Instance.ReturnToSinglePlayer();
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Go offline failed: " + ex.Message); }
        }

        // ---------- Saving ----------

        private void SaveAll()
        {
            if (BuildWorld != null && BuildWorld.Unsaved) BuildWorld.Save(Path.Combine(SaveDir, "build.gmw"));
            foreach (var kv in _voxelWorlds)
                if (!_friendWorlds.Contains(kv.Value)) kv.Value.Save(Path.Combine(SaveDir, kv.Key.Id + "_world.gmw"));
            try
            {
                Directory.CreateDirectory(SaveDir);
                foreach (var kv in _players)
                {
                    var o = kv.Value.ToJson();
                    o["survival"] = _survivalMode.TryGetValue(kv.Key, out bool sv) && sv;
                    File.WriteAllText(Path.Combine(SaveDir, kv.Key.Id + "_player.json"), o.ToString());
                }
                var all = new JObject();
                foreach (var w in _furnaces)
                {
                    if (w.Key.EndsWith("@friend")) continue;
                    var fs = new JObject();
                    foreach (var f in w.Value) fs[$"{f.Key.x},{f.Key.y},{f.Key.z}"] = f.Value.ToJson();
                    all[w.Key] = fs;
                }
                File.WriteAllText(FurnaceFile, all.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Saving player failed: " + ex.Message); }
        }

        private void OnApplicationQuit()
        {
            if (_ready) SaveAll();
        }

        // ---------- Developer self-test ----------

        /// <summary>
        /// Friends can't be tested alone, so send this world and some edits through the same code a friend's game
        /// runs (encode, decode, world bytes, a copy at the same spot) and check everything arrives.
        /// </summary>
        private void FriendsLoopbackTest(Pack pack, VoxelWorld w)
        {
            try
            {
                var stone = pack.Block("stone");
                var near = w.SpawnBlock + new Vector3Int(3, 0, 3);
                near.y = w.TopSolid(near.x, near.z, TerrainGen.Height - 1, 0) + 1;
                var far = new Vector3Int(w.SpawnBlock.x + 400, 60, w.SpawnBlock.z + 400);
                var edits = new List<(Vector3Int p, ushort id)> { (near, stone.Index), (far, stone.Index), (near + Vector3Int.up, 0) };
                var back = Multiplayer.DecodeEdits(Multiplayer.EncodeEdits(pack.Id, edits));
                bool codec = back != null && back.Count == 3 && back[1].p == far && back[1].id == stone.Index && back[2].id == 0;
                bool applied = true;
                foreach (var (p, id) in back) applied &= w.ApplyRemote(p, id) || w.Get(p) == id;
                var bytes = w.SaveBytes();
                var copy = new VoxelWorld(pack.Id + "@test", pack, w.Origin, w.S, _layer, new TerrainGen(pack, w.Gen.Seed));
                bool loaded = copy.LoadBytes(bytes);
                bool same = copy.GetAnywhere(near) == stone.Index && copy.GetAnywhere(far) == stone.Index;
                copy.Destroy();
                // The backup an owner gets when friends join (the test's own backup is removed again).
                string dir = Path.Combine(SaveDir, "backups");
                var before = Directory.Exists(dir) ? new HashSet<string>(Directory.GetFiles(dir)) : new HashSet<string>();
                BackupWorlds();
                var made = Directory.Exists(dir) ? Directory.GetFiles(dir).Where(f => !before.Contains(f)).ToList() : new List<string>();
                bool backup = made.Count == 1 && new FileInfo(made[0]).Length > 0;
                foreach (var f in made) File.Delete(f);
                Plugin.Log.LogInfo($"AUTOTEST friends: codec {(codec ? "ok" : "BAD")}, apply {(applied ? "ok" : "BAD")}, " +
                                   $"world {bytes.Length / 1024} KB load {(loaded ? "ok" : "BAD")}, copy matches {(same ? "ok" : "BAD")}, " +
                                   $"backup {(backup ? "ok" : "BAD")}, active={Net.Active}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("AUTOTEST friends failed: " + ex); }
        }

        private static string AutoTestDir => Path.Combine(SaveDir, "autotest");
        private static string RealSaveStash => Path.Combine(AutoTestDir, "real-save");

        private static IEnumerable<string> SurvivalSaveFiles(string dir) =>
            Directory.GetFiles(dir, "*_world.gmw").Concat(Directory.GetFiles(dir, "*_player.json")).Concat(Directory.GetFiles(dir, "furnaces.json"));

        /// <summary>Self-test only: moves your survival saves out of the way and forgets them, so the test plays a fresh world.</summary>
        private void StashRealSave()
        {
            Directory.CreateDirectory(RealSaveStash);
            foreach (var f in SurvivalSaveFiles(SaveDir).ToList())
                File.Move(f, Path.Combine(RealSaveStash, Path.GetFileName(f)));
            _players.Clear();
            _furnaces.Clear();
        }

        /// <summary>Puts stashed saves back (also after a test that crashed half way) and reloads them.</summary>
        private void RestoreRealSave()
        {
            if (!Directory.Exists(RealSaveStash)) return;
            var stashed = SurvivalSaveFiles(RealSaveStash).ToList();
            foreach (var f in stashed)
            {
                string to = Path.Combine(SaveDir, Path.GetFileName(f));
                if (File.Exists(to)) File.Delete(to);
                File.Move(f, to);
            }
            if (stashed.Count == 0) return;
            _players.Clear();
            _furnaces.Clear();
            LoadFurnaces();
            Plugin.Log.LogInfo($"AUTOTEST put back {stashed.Count} save files");
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            string path = Path.Combine(AutoTestDir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Plugin.Log.LogInfo("AUTOTEST shot " + path);
        }

        // What you'd see in the headset: render the player's own camera into a picture.
        private IEnumerator FpShot(string name)
        {
            yield return new WaitForEndOfFrame();
            var cam = GTPlayer.Instance.mainCamera;
            var rt = RenderTexture.GetTemporary(1280, 720, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            try { cam.Render(); } catch (Exception ex) { Plugin.Log.LogWarning("First-person render failed: " + ex.Message); }
            cam.targetTexture = prev;
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            string path = Path.Combine(AutoTestDir, name + "_fp.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Plugin.Log.LogInfo("AUTOTEST shot " + path);
        }

        private IEnumerator AutoTest()
        {
            Directory.CreateDirectory(AutoTestDir);
            foreach (var old in Directory.GetFiles(AutoTestDir, "*.png")) File.Delete(old);
            Plugin.Log.LogInfo("AUTOTEST start");
            // Put your real save aside so the test starts empty and can't touch it; it comes back at the end.
            RestoreRealSave();
            StashRealSave();
            yield return new WaitForSeconds(4f);
            if (Plugin.InRoom)
            {
                GoOffline();
                float until = Time.time + 20f;
                while (Plugin.InRoom && Time.time < until) yield return null;
                yield return new WaitForSeconds(2f);
            }
            var gp = GTPlayer.Instance;
            var cam = gp.mainCamera.transform;
            yield return Shot("1_gt_start");
            if (VoxelPacks.Count == 0) yield break;
            var pack = VoxelPacks[0];

            float t0 = Time.realtimeSinceStartup;
            Travel(pack);
            while (CurrentPack != pack && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            Plugin.Log.LogInfo($"AUTOTEST travel took {Time.realtimeSinceStartup - t0:0.0}s");
            var w = ActiveWorld;
            w.TimeOfDay = 0.2f;
            void Give(string id, int n) { var d = Items.Local(pack, id); if (d != null && Player != null) Player.Inv.Add(d, n); }
            Give("oak_log", 12); Give("cobblestone", 40); Give("torch", 16); Give("crafting_table", 1); Give("stone_pickaxe", 1);
            Give("apple", 5); Give("bow", 1); Give("arrow", 16); Give("wheat_seeds", 8);
            var chest = Items.Local(pack, "iron_chestplate"); var helmet = Items.Local(pack, "diamond_helmet");
            if (chest != null && Player != null) Player.Inv.Armor[1] = new ItemStack(chest, 1);
            if (helmet != null && Player != null) Player.Inv.Armor[0] = new ItemStack(helmet, 1);
            yield return new WaitForSeconds(6f);
            yield return Shot("2_world_day");
            yield return FpShot("2_world_day");
            for (int k = 1; k <= 3; k++)
            {
                gp.Turn(90f);
                yield return new WaitForSeconds(2f);
                yield return Shot("3_turn" + k);
            }
            var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, fwd);
            if (_mobs.TryGetValue(pack, out var defs))
            {
                for (int i = 0; i < defs.Count; i++)
                {
                    // An arc in front of you so they're all in view.
                    float ang = Mathf.Lerp(-38f, 38f, defs.Count > 1 ? i / (float)(defs.Count - 1) : 0.5f);
                    var spot = cam.position + Quaternion.AngleAxis(ang, Vector3.up) * fwd * 4.2f;
                    var b = w.ToBlock(spot);
                    int top = w.TopSolid(b.x, b.z, b.y + 6, b.y - 10);
                    if (top == int.MinValue) continue;
                    Mob.Spawn(this, w, defs[i], w.Corner(new Vector3Int(b.x, top + 1, b.z)) + new Vector3(w.S / 2, 0, w.S / 2));
                }
            }
            yield return new WaitForSeconds(0.7f);
            yield return Shot("4_mobs");
            yield return FpShot("4_mobs");
            foreach (var m in new List<Mob>(Mob.Live)) Destroy(m.gameObject);

            // A little farm: farmland with wheat at every growth stage.
            var farm = pack.Block("farmland");
            for (int i = 0; i < 8 && farm != null; i++)
            {
                var fb = w.ToBlock(cam.position + fwd * 1.6f + right * (i - 3.5f) * w.S);
                int ft = w.TopSolid(fb.x, fb.z, fb.y + 4, fb.y - 8);
                if (ft == int.MinValue) continue;
                var ground = new Vector3Int(fb.x, ft, fb.z);
                w.Set(ground, farm.Index);
                var crop = pack.Block("wheat_stage" + i);
                if (crop != null) w.Set(ground + Vector3Int.up, crop.Index);
            }
            yield return new WaitForSeconds(1.5f);
            yield return FpShot("4b_farm");

            w.TimeOfDay = 0.47f;
            yield return new WaitForSeconds(3f);
            yield return Shot("5_sunset");
            w.TimeOfDay = 0.75f;
            var tb = w.ToBlock(cam.position + fwd * 1.5f);
            int tt = w.TopSolid(tb.x, tb.z, tb.y + 4, tb.y - 8);
            var torch = pack.Block("torch");
            if (tt != int.MinValue && torch != null) w.Set(new Vector3Int(tb.x, tt + 1, tb.z), torch.Index);
            yield return new WaitForSeconds(5f);
            yield return Shot("6_night_torch");
            yield return FpShot("6_night_torch");
            w.TimeOfDay = 0.25f;
            yield return new WaitForSeconds(3f);

            // Inventory first, then the crafting table: their pictures must not cover each other's items.
            OpenInventory();
            yield return new WaitForSeconds(1f);
            yield return FpShot("7a_inventory");
            Gui.Close();
            yield return new WaitForSeconds(0.5f);
            OpenCrafting();
            yield return new WaitForSeconds(1f);
            yield return Shot("7_crafting");
            yield return FpShot("7_crafting");
            Gui.Close();
            yield return new WaitForSeconds(1f);
            FriendsLoopbackTest(pack, w);

            Travel(null, true);
            yield return new WaitForSeconds(3f);
            yield return Shot("8_back_home");
            Plugin.Log.LogInfo($"AUTOTEST stats: chunks={w.ChunkCount} columns={w.LoadedColumns} recipes={Recipes.Crafting.Count} items={Items.All.Count}");
            // Throw away everything the test made, so real play starts with an empty inventory and a fresh world.
            w.Destroy();
            _voxelWorlds.Remove(pack);
            _voxelReady.Remove(pack);
            _players.Remove(pack);
            _furnaces.Remove(pack.Id);
            if (_skies.TryGetValue(pack, out var testSky)) { testSky.SetVisible(false); _skies.Remove(pack); }
            foreach (var f in new[] { pack.Id + "_world.gmw", pack.Id + "_player.json", "furnaces.json" })
                try { File.Delete(Path.Combine(SaveDir, f)); } catch { }
            RestoreRealSave();
            _saveAt = Time.time + 60f;
            Plugin.Log.LogInfo("AUTOTEST cleaned up its world and items");
            Plugin.Log.LogInfo("AUTOTEST done");
        }
    }
}
