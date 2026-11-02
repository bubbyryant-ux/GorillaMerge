using System;
using System.Collections.Generic;
using GorillaLocomotion;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// Minecraft's HUD floating low in your view: the real hotbar picture with your items, hearts and hunger
    /// above it, the held item's name, a red flash when you're hurt, and the death screen.
    /// </summary>
    public class Hud
    {
        private readonly Canvas2D _c;
        private readonly GameObject _root;
        private int _lastSel = -1;
        private ItemDef _lastItem;
        private float _nameTime;
        private bool _placed;

        public Hud()
        {
            _root = new GameObject("GM_Hud");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _c = new Canvas2D("hud", 1f, _root.transform);
        }

        public void Hide() { if (_root.activeSelf) _root.SetActive(false); _placed = false; }

        public void Tick(bool survival, Survival player, IList<BlockDef> creativeItems, int creativeSel)
        {
            var cam = GorillaTagger.Instance?.mainCamera;
            if (cam == null) { Hide(); return; }
            if (!_root.activeSelf) _root.SetActive(true);
            var ct = cam.transform;

            // Low in the middle of your view, following your head smoothly.
            float scale = Mathf.Clamp(Plugin.Instance.HudScale.Value, 0.3f, 3f);
            float dist = 0.55f;
            var dir = Quaternion.AngleAxis(Plugin.Instance.HudAngle.Value, ct.right) * ct.forward;
            var pos = ct.position + dir * dist;
            var rot = Quaternion.LookRotation(dir, Quaternion.AngleAxis(Plugin.Instance.HudAngle.Value, ct.right) * ct.up);
            float k = _placed ? 1f - Mathf.Exp(-Time.deltaTime * 14f) : 1f;
            _root.transform.position = Vector3.Lerp(_root.transform.position, pos, k);
            _root.transform.rotation = Quaternion.Slerp(_root.transform.rotation, rot, k);
            _placed = true;

            float px = 0.3f / 182f * scale;
            _c.Px = px;
            _c.Root.transform.localPosition = new Vector3(-91 * px, 11 * px, 0);

            _c.Begin();
            var hotbar = GuiTex.Get("gui/sprites/hud/hotbar");
            var sel = GuiTex.Get("gui/sprites/hud/hotbar_selection");
            if (hotbar != null) _c.Sprite(hotbar, 0, 0);
            else _c.Rect(0, 0, 182, 22, new Color32(0, 0, 0, 140));

            int selected;
            ItemDef heldItem;
            if (survival && player != null)
            {
                selected = player.Inv.Selected;
                for (int i = 0; i < 9; i++) _c.Stack(player.Inv.Slots[i], 3 + i * 20, 3);
                heldItem = player.Inv.Held.Item;
            }
            else
            {
                int n = creativeItems?.Count ?? 0;
                int start = Mathf.Clamp(creativeSel - 4, 0, Mathf.Max(0, n - 9));
                for (int i = 0; i < 9 && start + i < n; i++) _c.BlockIcon(creativeItems[start + i], 3 + i * 20, 3, 16);
                selected = creativeSel - start;
                heldItem = n > 0 ? Items.ForBlock(creativeItems[Mathf.Clamp(creativeSel, 0, n - 1)]) : null;
            }
            if (sel != null) _c.Sprite(sel, -1 + selected * 20, -1);
            else _c.Rect(selected * 20, 0, 22, 22, new Color32(255, 255, 255, 90));

            if (survival && player != null)
            {
                var full = GuiTex.Get("gui/sprites/hud/heart/full");
                var half = GuiTex.Get("gui/sprites/hud/heart/half");
                var box = GuiTex.Get("gui/sprites/hud/heart/container");
                var foodFull = GuiTex.Get("gui/sprites/hud/food_full");
                var foodHalf = GuiTex.Get("gui/sprites/hud/food_half");
                var foodBox = GuiTex.Get("gui/sprites/hud/food_empty");
                int hp = Mathf.CeilToInt(player.Health), food = Mathf.CeilToInt(player.Food);
                int armor = player.Inv.ArmorPoints;
                if (armor > 0)
                {
                    var aFull = GuiTex.Get("gui/sprites/hud/armor_full");
                    var aHalf = GuiTex.Get("gui/sprites/hud/armor_half");
                    var aEmpty = GuiTex.Get("gui/sprites/hud/armor_empty");
                    for (int i = 0; i < 10; i++)
                        _c.Sprite(armor >= i * 2 + 2 ? aFull : armor == i * 2 + 1 ? aHalf : aEmpty, i * 8, -20);
                }
                bool shake = player.Health <= 4;
                for (int i = 0; i < 10; i++)
                {
                    float y = -10 + (shake ? UnityEngine.Random.Range(0, 2) : 0);
                    _c.Sprite(box, i * 8, y);
                    if (hp >= i * 2 + 2) _c.Sprite(full, i * 8, y);
                    else if (hp == i * 2 + 1) _c.Sprite(half, i * 8, y);
                    float fx = 182 - 9 - i * 8;
                    _c.Sprite(foodBox, fx, -10);
                    if (food >= i * 2 + 2) _c.Sprite(foodFull, fx, -10);
                    else if (food == i * 2 + 1) _c.Sprite(foodHalf, fx, -10);
                }
            }

            // Held item name pops up for a couple of seconds when you switch, like Minecraft.
            if (selected != _lastSel || heldItem != _lastItem) { _lastSel = selected; _lastItem = heldItem; _nameTime = Time.time; }
            float age = Time.time - _nameTime;
            if (heldItem != null && age < 2.5f)
            {
                byte a = (byte)(255 * Mathf.Clamp01((2.5f - age) * 2f));
                _c.TextCentered(heldItem.Name, 91, survival ? -24 : -13, new Color32(255, 255, 255, a));
            }

            if (player != null && survival)
            {
                if (player.HurtFlash > 0) _c.Rect(-400, -400, 982, 822, new Color32(200, 0, 0, (byte)(90 * player.HurtFlash)));
                if (player.EatProgress > 0)
                {
                    _c.Rect(61, -36, 60, 4, new Color32(0, 0, 0, 160));
                    _c.Rect(61, -36, 60 * player.EatProgress, 4, new Color32(120, 220, 80, 230));
                }
                if (player.BowCharge > 0)
                {
                    _c.Rect(61, -36, 60, 4, new Color32(0, 0, 0, 160));
                    _c.Rect(61, -36, 60 * player.BowCharge, 4, player.BowCharge >= 1f ? new Color32(255, 230, 90, 240) : new Color32(230, 230, 230, 230));
                }
                if (player.Dead)
                {
                    _c.Rect(-400, -400, 982, 822, new Color32(120, 0, 0, 120));
                    _c.Band = 2;
                    _c.TextCentered("You died!", 91, -150, new Color32(255, 255, 255, 255), 3f);
                    _c.TextCentered(player.DeathMessage ?? "", 91, -118, new Color32(255, 255, 255, 255), 1.2f);
                }
            }
            _c.End();
        }
    }

    /// <summary>
    /// Minecraft's container menus floating in front of you: inventory (2x2 crafting), crafting table (3x3)
    /// and furnace. Point at a slot with either hand: trigger = left click, grip = right click (half / one).
    /// B or Y closes it.
    /// </summary>
    public class ContainerGui
    {
        public enum Kind { Inventory, Crafting, Furnace }

        private struct Slot
        {
            public int X, Y;
            public Func<ItemStack> Get;
            public Action<ItemStack> Set;
            public bool Output;
            public Func<ItemStack, bool> Accept;
        }

        private readonly Canvas2D _c;
        private readonly GameObject _root;
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly ItemStack[] _grid = new ItemStack[9];
        private int _gridW;
        private ItemStack _cursor;
        private Kind _kind;
        private Furnace _furnace;
        private Survival _p;
        private bool _trigWasL, _trigWasR, _gripWasL, _gripWasR, _secWas;
        private const float Px = 0.0028f;

        public bool Open { get; private set; }
        public bool JustClosedSecondary;

        public ContainerGui()
        {
            _root = new GameObject("GM_Container");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _c = new Canvas2D("panel", Px, _root.transform);
            _c.Root.transform.localPosition = new Vector3(-88 * Px, 83 * Px, 0);
            _root.SetActive(false);
        }

        public void Show(Kind kind, Survival player, Furnace furnace = null)
        {
            if (Open) Close();
            _kind = kind;
            _p = player;
            _furnace = furnace;
            _gridW = kind == Kind.Crafting ? 3 : kind == Kind.Inventory ? 2 : 0;
            for (int i = 0; i < 9; i++) _grid[i] = ItemStack.None;
            _cursor = ItemStack.None;
            BuildSlots();

            var cam = GorillaTagger.Instance.mainCamera.transform;
            var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.1f) fwd = cam.up;
            _root.transform.position = cam.position + fwd * 0.5f + Vector3.down * 0.12f;
            _root.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(12f, 0, 0);
            _root.SetActive(true);
            Open = true;
            var ip = ControllerInputPoller.instance;
            _trigWasL = _trigWasR = _gripWasL = _gripWasR = true; // ignore the press that opened it
            _secWas = ip != null && (ip.leftControllerSecondaryButton || ip.rightControllerSecondaryButton);
        }

        public void Close()
        {
            if (!Open) return;
            Open = false;
            _root.SetActive(false);
            // Anything left in the crafting grid or on the pointer goes back into your inventory.
            var mgr = MergeManager.Instance;
            void Return(ItemStack s)
            {
                if (s.Empty || _p == null) return;
                int left = _p.Inv.Add(s.Item, s.Count);
                if (left > 0) ItemEntity.Spawn(mgr, mgr.ActiveWorld, GTPlayer.Instance.headCollider.transform.position, new ItemStack(s.Item, left), Vector3.up);
            }
            for (int i = 0; i < 9; i++) { Return(_grid[i]); _grid[i] = ItemStack.None; }
            Return(_cursor);
            _cursor = ItemStack.None;
        }

        private void BuildSlots()
        {
            _slots.Clear();
            var inv = _p.Inv;
            for (int i = 9; i < 36; i++)
            {
                int idx = i;
                _slots.Add(new Slot { X = 8 + (i - 9) % 9 * 18, Y = 84 + (i - 9) / 9 * 18, Get = () => inv.Slots[idx], Set = s => inv.Slots[idx] = s });
            }
            for (int i = 0; i < 9; i++)
            {
                int idx = i;
                _slots.Add(new Slot { X = 8 + i * 18, Y = 142, Get = () => inv.Slots[idx], Set = s => inv.Slots[idx] = s });
            }
            if (_kind == Kind.Inventory)
            {
                string[] slotNames = { "head", "chest", "legs", "feet" };
                for (int i = 0; i < 4; i++)
                {
                    int idx = i;
                    string name = slotNames[i];
                    _slots.Add(new Slot { X = 8, Y = 8 + i * 18, Get = () => inv.Armor[idx], Set = s => inv.Armor[idx] = s, Accept = s => s.Item.Slot == name });
                }
            }
            if (_kind == Kind.Furnace)
            {
                _slots.Add(new Slot { X = 56, Y = 17, Get = () => _furnace.In, Set = s => _furnace.In = s });
                _slots.Add(new Slot { X = 56, Y = 53, Get = () => _furnace.Fuel, Set = s => _furnace.Fuel = s });
                _slots.Add(new Slot { X = 116, Y = 35, Get = () => _furnace.Out, Set = s => _furnace.Out = s, Output = true });
                return;
            }
            int gx = _kind == Kind.Crafting ? 30 : 98, gy = _kind == Kind.Crafting ? 17 : 18;
            for (int y = 0; y < _gridW; y++)
                for (int x = 0; x < _gridW; x++)
                {
                    int idx = y * _gridW + x;
                    _slots.Add(new Slot { X = gx + x * 18, Y = gy + y * 18, Get = () => _grid[idx], Set = s => _grid[idx] = s });
                }
            _slots.Add(new Slot
            {
                X = _kind == Kind.Crafting ? 124 : 154, Y = _kind == Kind.Crafting ? 35 : 28, Output = true,
                Get = () => { var r = Recipes.Match(Grid(), _gridW); return r == null ? ItemStack.None : new ItemStack(r.Result, r.Count); },
                Set = _ => { },
            });
        }

        private ItemStack[] Grid()
        {
            var g = new ItemStack[_gridW * _gridW];
            Array.Copy(_grid, g, g.Length);
            return g;
        }

        private bool Ray(bool left, out Vector2 px)
        {
            px = default;
            var gp = GTPlayer.Instance;
            var ct = gp.GetControllerTransform(left);
            if (ct == null) return false;
            var origin = gp.GetHandPosition(left);
            var dir = ct.rotation * (Quaternion.Euler(Plugin.Instance.AimPitch.Value, 0, 0) * Vector3.forward);
            if (HandBuilder.DesktopMode) { var cam = gp.mainCamera.transform; origin = cam.position; dir = cam.forward; }
            var plane = new Plane(-_root.transform.forward, _root.transform.position);
            if (!plane.Raycast(new Ray(origin, dir), out float t) || t > 1.5f) return false;
            var local = _c.Root.transform.InverseTransformPoint(origin + dir * t);
            px = new Vector2(local.x / Px, -local.y / Px);
            return px.x >= -20 && px.x <= 196 && px.y >= -20 && px.y <= 186;
        }

        private int SlotAt(Vector2 p)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                if (p.x >= s.X - 1 && p.x < s.X + 17 && p.y >= s.Y - 1 && p.y < s.Y + 17) return i;
            }
            return -1;
        }

        public void Tick(MergeManager mgr)
        {
            JustClosedSecondary = false;
            if (!Open) return;
            var ip = ControllerInputPoller.instance;
            var head = GTPlayer.Instance.headCollider.transform.position;
            bool sec = ip.leftControllerSecondaryButton || ip.rightControllerSecondaryButton;
            if ((sec && !_secWas) || Vector3.Distance(head, _root.transform.position) > 2.5f || _p == null || _p.Dead)
            {
                Close();
                JustClosedSecondary = sec;
                _secWas = sec;
                return;
            }
            _secWas = sec;

            // Pointer: right hand first, then left.
            bool rightHit = Ray(false, out var pr), leftHit = Ray(true, out var pl);
            bool usingLeft = !rightHit && leftHit;
            var p = rightHit ? pr : pl;
            bool any = rightHit || leftHit;
            int hover = any ? SlotAt(p) : -1;

            bool trigR = ip.rightControllerIndexFloat > 0.6f, trigL = ip.leftControllerIndexFloat > 0.6f;
            bool gripR = ip.rightControllerGripFloat > 0.6f, gripL = ip.leftControllerGripFloat > 0.6f;
            bool click = usingLeft ? trigL && !_trigWasL : trigR && !_trigWasR;
            bool rclick = usingLeft ? gripL && !_gripWasL : gripR && !_gripWasR;
            _trigWasL = trigL; _trigWasR = trigR; _gripWasL = gripL; _gripWasR = gripR;

            if (hover >= 0 && (click || rclick))
            {
                Click(_slots[hover], rclick);
                mgr.Sounds.PlayEvent(mgr.SurvivalPack, "click", _root.transform.position, 0.25f);
                try { GorillaTagger.Instance.StartVibration(usingLeft, 0.12f, 0.03f); } catch { }
            }
            else if (any && hover < 0 && click && !_cursor.Empty && (p.x < 0 || p.x > 176 || p.y < 0 || p.y > 166))
            {
                // Clicking outside the panel throws the held stack, like Minecraft.
                ItemEntity.Spawn(mgr, mgr.ActiveWorld, _root.transform.position, _cursor, -_root.transform.forward * 2f);
                _cursor = ItemStack.None;
            }

            Draw(any ? p : (Vector2?)null, hover);
        }

        private void Click(Slot s, bool right)
        {
            var cur = s.Get();
            if (s.Accept != null && !_cursor.Empty && !s.Accept(_cursor)) return;
            if (s.Output)
            {
                if (cur.Empty) return;
                if (!_cursor.Empty && (_cursor.Item != cur.Item || _cursor.Count + cur.Count > cur.Item.MaxStack)) return;
                if (_cursor.Empty) _cursor = cur; else _cursor.Count += cur.Count;
                if (_kind == Kind.Furnace) _furnace.Out = ItemStack.None;
                else
                    for (int i = 0; i < _gridW * _gridW; i++)
                        if (!_grid[i].Empty) { _grid[i].Count--; if (_grid[i].Count <= 0) _grid[i] = ItemStack.None; }
                return;
            }
            if (!right)
            {
                if (_cursor.Empty) { _cursor = cur; s.Set(ItemStack.None); }
                else if (cur.Empty) { s.Set(_cursor); _cursor = ItemStack.None; }
                else if (cur.Item == _cursor.Item && cur.Item.MaxStack > 1)
                {
                    int move = Math.Min(_cursor.Count, cur.Item.MaxStack - cur.Count);
                    cur.Count += move; _cursor.Count -= move;
                    s.Set(cur);
                    if (_cursor.Count <= 0) _cursor = ItemStack.None;
                }
                else { s.Set(_cursor); _cursor = cur; }
            }
            else
            {
                if (_cursor.Empty)
                {
                    if (cur.Empty) return;
                    int take = (cur.Count + 1) / 2;
                    _cursor = new ItemStack(cur.Item, take) { Wear = cur.Wear };
                    cur.Count -= take;
                    s.Set(cur.Count > 0 ? cur : ItemStack.None);
                }
                else if (cur.Empty || (cur.Item == _cursor.Item && cur.Count < cur.Item.MaxStack))
                {
                    if (cur.Empty) cur = new ItemStack(_cursor.Item, 0) { Wear = _cursor.Wear };
                    cur.Count++;
                    s.Set(cur);
                    _cursor.Count--;
                    if (_cursor.Count <= 0) _cursor = ItemStack.None;
                }
            }
        }

        private void Draw(Vector2? pointer, int hover)
        {
            _c.Begin();
            string file = _kind == Kind.Furnace ? "gui/container/furnace" : _kind == Kind.Crafting ? "gui/container/crafting_table" : "gui/container/inventory";
            var bg = GuiTex.Get(file);
            if (bg != null) _c.Sprite(bg, 0, 0, 176, 166, new RectInt(0, 0, 176, 166), new Color32(255, 255, 255, 255));
            else _c.Rect(0, 0, 176, 166, new Color32(198, 198, 198, 255));

            var white = new Color32(64, 64, 64, 255);
            if (_kind == Kind.Furnace)
            {
                _c.Text("Furnace", 88 - McFont.Width("Furnace") / 2f, 6, white, 1f, false);
                if (_furnace.Lit && _furnace.BurnMax > 0)
                {
                    var lit = GuiTex.Get("gui/sprites/container/furnace/lit_progress");
                    int h = Mathf.CeilToInt(14 * Mathf.Clamp01(_furnace.Burn / _furnace.BurnMax));
                    if (lit != null && h > 0) _c.Sprite(lit, 56, 36 + 14 - h, 14, h, new RectInt(0, 14 - h, 14, h), new Color32(255, 255, 255, 255));
                }
                var recipe = _furnace.In.Empty ? null : Recipes.SmeltFor(_furnace.In.Item);
                if (recipe != null && _furnace.Cook > 0)
                {
                    var arrow = GuiTex.Get("gui/sprites/container/furnace/burn_progress");
                    int w = Mathf.CeilToInt(24 * Mathf.Clamp01(_furnace.Cook / recipe.Seconds));
                    if (arrow != null && w > 0) _c.Sprite(arrow, 79, 34, w, 16, new RectInt(0, 0, w, 16), new Color32(255, 255, 255, 255));
                }
            }
            else _c.Text("Crafting", _kind == Kind.Crafting ? 28 : 97, 6, white, 1f, false);
            // Minecraft's own inventory screen has no "Inventory" label (the player preview sits there).
            if (_kind != Kind.Inventory) _c.Text("Inventory", 8, 72, white, 1f, false);

            foreach (var s in _slots) _c.Stack(s.Get(), s.X, s.Y);
            if (hover >= 0) _c.Rect(_slots[hover].X, _slots[hover].Y, 16, 16, new Color32(255, 255, 255, 110));
            if (hover >= 0 && _cursor.Empty)
            {
                var st = _slots[hover].Get();
                if (!st.Empty)
                {
                    _c.Band = 2;
                    float tw = McFont.Width(st.Item.Name) + 6;
                    _c.Rect(_slots[hover].X + 12, _slots[hover].Y - 14, tw, 12, new Color32(16, 0, 16, 235));
                    _c.Text(st.Item.Name, _slots[hover].X + 15, _slots[hover].Y - 12, new Color32(255, 255, 255, 255));
                }
            }
            if (pointer.HasValue)
            {
                var p = pointer.Value;
                _c.Band = 3;
                if (!_cursor.Empty) _c.Stack(_cursor, p.x - 8, p.y - 8);
                else _c.Rect(p.x - 1.5f, p.y - 1.5f, 3, 3, new Color32(255, 255, 255, 230));
            }
            _c.End();
        }
    }
}
