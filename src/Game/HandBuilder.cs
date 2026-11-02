using GorillaLocomotion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GorillaMerge
{
    /// <summary>
    /// One gorilla hand's controls.
    ///   Placing hand (right by default), trigger: place the held block where you point (or in the air in front
    ///     of your hand), use a crafting table or furnace, light TNT with flint and steel, or eat held food.
    ///   Mining hand (left by default), hold trigger: mine what you point at (Minecraft mining times with tools),
    ///     or hit a mob you point at.
    ///   Either hand: make a fist and punch blocks to break them; swing at mobs to hit them.
    ///   B / Y: next / previous hotbar slot. X: inventory.
    /// </summary>
    public class HandBuilder
    {
        private readonly bool _isLeft;
        private readonly GameObject _outline, _ghost;
        private readonly Mesh _ghostMesh;
        private ushort _ghostBlock;
        private bool _trigWas, _secWas, _secChord, _priWas, _wasTouching;
        private float _repeatAt, _attackCd;
        private Vector3Int _lastPlaced;
        private bool _mining;
        private Vector3Int _mineBlock;
        private float _mineProgress, _nextHitSound;
        private float _speed0, _speed1, _speed2;
        private Vector3 _lastRel;
        private int _lastRelFrame = -10;
        private float _bowCharge;

        public HandBuilder(bool isLeft, Transform parent)
        {
            _isLeft = isLeft;
            _outline = MeshUtil.Object(isLeft ? "GM_outlineL" : "GM_outlineR", MeshUtil.Outline(1f, 0.025f), Materials.Solid(new Color(0f, 0f, 0f, 0.7f)), parent);
            _outline.SetActive(false);
            _ghostMesh = new Mesh { name = "GM_ghost" };
            _ghost = MeshUtil.Object(isLeft ? "GM_ghostL" : "GM_ghostR", _ghostMesh, BlockRegistry.GhostMat, parent);
            _ghost.SetActive(false);
        }

        private bool IsPlacer => _isLeft == Plugin.Instance.SwapHands.Value;

        public static bool DesktopMode
        {
            get { try { return !UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head).isValid; } catch { return true; } }
        }

        public void Hide()
        {
            _outline.SetActive(false);
            _ghost.SetActive(false);
            if (_mining) MergeManager.Instance.Cracks.Hide();
            _mining = false;
            PunchGuard.Punching[_isLeft ? 0 : 1] = false;
        }

        /// <summary>
        /// selected: the block to place in creative. In survival the held item comes from the inventory.
        /// </summary>
        public void Tick(MergeManager mgr, VoxelWorld world, BlockDef selected, float dt)
        {
            var gp = GTPlayer.Instance;
            var ip = ControllerInputPoller.instance;
            if (gp == null || ip == null || world == null) { Hide(); return; }
            var cfg = Plugin.Instance;
            float s = world.S;
            var player = mgr.Survival ? mgr.Player : null;
            bool guiOpen = mgr.Gui.Open || mgr.Gui.JustClosedSecondary;

            // ---- input ----
            float trig = _isLeft ? ip.leftControllerIndexFloat : ip.rightControllerIndexFloat;
            float grip = _isLeft ? ip.leftControllerGripFloat : ip.rightControllerGripFloat;
            bool sec = _isLeft ? ip.leftControllerSecondaryButton : ip.rightControllerSecondaryButton;
            bool primary = _isLeft ? ip.leftControllerPrimaryButton : ip.rightControllerPrimaryButton;
            float speed = (_isLeft ? ip.leftVelocity : ip.rightVelocity).magnitude;

            Vector3 handPos = gp.GetHandPosition(_isLeft);
            var ct = gp.GetControllerTransform(_isLeft);
            // Some headsets don't report controller speed, so also measure the swing ourselves: the
            // controller relative to your head, turned with you, so running and turning don't count.
            if (ct != null && dt > 0f)
            {
                var body = gp.turnParent != null ? gp.turnParent.transform : gp.transform;
                Vector3 rel = Quaternion.Inverse(body.rotation) * (ct.position - gp.headCollider.transform.position);
                if (_lastRelFrame == Time.frameCount - 1) speed = Mathf.Max(speed, (rel - _lastRel).magnitude / dt);
                _lastRel = rel;
                _lastRelFrame = Time.frameCount;
            }
            _speed2 = _speed1; _speed1 = _speed0; _speed0 = speed;
            Vector3 dir = ct != null ? ct.rotation * (Quaternion.Euler(cfg.AimPitch.Value, 0f, 0f) * Vector3.forward) : gp.headCollider.transform.forward;
            Vector3 origin = handPos;

            bool desktop = DesktopMode;
            if (desktop)
            {
                var cam = gp.mainCamera != null ? gp.mainCamera.transform : null;
                if (cam != null) { origin = cam.position; dir = cam.forward; }
                var kb = Keyboard.current;
                var mouse = Mouse.current;
                if (IsPlacer)
                {
                    if ((kb != null && kb.f6Key.isPressed) || (mouse != null && mouse.middleButton.isPressed)) trig = 1f;
                    if (kb != null && kb.rightBracketKey.wasPressedThisFrame) mgr.CycleItem(1);
                    if (kb != null && kb.leftBracketKey.wasPressedThisFrame) mgr.CycleItem(-1);
                    if (kb != null && kb.eKey.wasPressedThisFrame && player != null) mgr.OpenInventory();
                }
                else if (kb != null && kb.f7Key.isPressed) trig = 1f;
            }

            bool trigDown = _trigWas ? trig > 0.4f : trig > 0.65f;
            bool pressed = trigDown && !_trigWas;
            _trigWas = trigDown;

            // B / Y switch slots when let go, unless A / X was also held (A+B is the world teleport).
            if (sec && !_secWas) _secChord = false;
            if (sec && primary) _secChord = true;
            if (!sec && _secWas && !_secChord && !guiOpen) mgr.CycleItem(_isLeft ? -1 : 1);
            _secWas = sec;
            // X opens your inventory (survival).
            if (_isLeft && primary && !_priWas && !sec && player != null && !mgr.Gui.Open) mgr.OpenInventory();
            _priWas = primary;

            if (guiOpen || (player != null && player.Dead)) { Hide(); return; }

            float reach = cfg.Reach.Value * (desktop ? 1.6f : 1f) * Mathf.Max(0.3f, gp.scale);
            bool hasHit = world.Raycast(origin, dir, reach, out var hit, out var normal);
            bool aiming = trig > 0.08f || desktop;
            var held = player != null ? player.Inv.Held : ItemStack.None;

            // ---- swinging at mobs (either hand) ----
            _attackCd -= dt;
            float swing = Mathf.Max(_speed0, Mathf.Max(_speed1, _speed2));
            if (_attackCd <= 0 && swing >= cfg.PunchSpeed.Value)
            {
                foreach (var m in Mob.Live)
                {
                    var box = m.Box;
                    box.Expand(0.1f);
                    if (!box.Contains(handPos)) continue;
                    m.Hurt(AttackDamage(held), handPos);
                    if (player != null && held.Item != null && held.Item.ToolType != null && !_isLeft == !cfg.SwapHands.Value) player.Inv.WearHeld();
                    _attackCd = 0.45f;
                    try { GorillaTagger.Instance.StartVibration(_isLeft, 0.4f, 0.06f); } catch { }
                    break;
                }
            }

            // ---- punching blocks (fist closed) ----
            bool fistPunch = cfg.PunchToBreak.Value && grip > 0.6f && swing >= cfg.PunchSpeed.Value;
            PunchGuard.Punching[_isLeft ? 0 : 1] = fistPunch;
            bool touching = gp.IsHandTouching(_isLeft);
            if (touching && !_wasTouching)
            {
                // GetTouchHitInfo is never filled in by the game any more; lastHitInfo is.
                var info = (_isLeft ? gp.LeftHand : gp.RightHand).lastHitInfo;
                var vc = info.collider != null ? info.collider.GetComponent<VoxelCollider>() : null;
                if (vc != null && vc.World != null)
                {
                    var touched = TouchedBlock(vc.World, info, handPos);
                    var tdef = vc.World.Def(touched);
                    if (tdef.Index != 0) mgr.Sounds.PlayBlock(tdef, "step", handPos, 0.35f);
                    if (fistPunch && tdef.Index != 0) mgr.Punch(vc.World, touched, _isLeft);
                }
            }
            _wasTouching = touching;

            if (IsPlacer)
            {
                _outline.SetActive(false);

                // Eat by holding food to your mouth or holding the trigger with it.
                if (player != null)
                {
                    bool atMouth = (handPos - (gp.headCollider.transform.position - Vector3.up * 0.07f)).magnitude < 0.2f;
                    player.TickEating((atMouth || trigDown) && !held.Empty && held.Item.IsFood, dt);
                    if (!held.Empty && held.Item.IsFood) { _ghost.SetActive(false); return; }
                }

                // Bow: hold the trigger to draw, let go to shoot (needs arrows).
                if (player != null && !held.Empty && held.Item.Use == "bow")
                {
                    _ghost.SetActive(false);
                    var arrow = Items.Local(mgr.SurvivalPack, "arrow");
                    if (trigDown && arrow != null && player.Inv.CountOf(arrow) > 0) _bowCharge = Mathf.Min(1f, _bowCharge + dt);
                    else if (_bowCharge > 0.15f && arrow != null && player.Inv.TakeOne(arrow))
                    {
                        // Minecraft's draw curve: a full draw (1 s) shoots at 3 blocks per tick.
                        float f = Mathf.Min(1f, (_bowCharge * _bowCharge + 2f * _bowCharge) / 3f);
                        Arrow.Spawn(mgr, world, handPos + dir.normalized * 0.25f, dir.normalized * (3f * f * 20f * s), null, true);
                        mgr.Sounds.PlayEvent(mgr.SurvivalPack, "bow", handPos, 0.8f, Random.Range(0.9f, 1.2f));
                        player.Inv.WearHeld();
                        _bowCharge = 0;
                    }
                    else if (!trigDown) _bowCharge = 0;
                    player.BowCharge = _bowCharge;
                    return;
                }
                if (player != null) player.BowCharge = 0;

                // Armor, shears, hoes, seeds and bone meal do their own thing.
                if (pressed && player != null && !held.Empty)
                {
                    var it = held.Item;
                    if (it.Armor > 0 && it.Slot != null) { mgr.EquipHeldArmor(); return; }
                    if (it.Use == "shears")
                        foreach (var m in Mob.Live)
                            if (m.Box.IntersectRay(new Ray(origin, dir), out float d) && d <= reach && m.Shear()) { player.Inv.WearHeld(); return; }
                    if (hasHit)
                    {
                        var target = world.Def(hit);
                        bool airAbove = world.Get(hit + Vector3Int.up) == 0;
                        if (it.ToolType == "hoe" && airAbove && (target.LocalId == "grass_block" || target.LocalId == "dirt")) { mgr.Till(world, hit); player.Inv.WearHeld(); return; }
                        if (it.Plants != null && airAbove && target.Special == "farmland") { if (mgr.Plant(world, hit + Vector3Int.up, it)) player.Inv.UseHeld(); return; }
                        if (it.Use == "bonemeal" && target.Special == "crop") { if (mgr.BoneMeal(world, hit)) player.Inv.UseHeld(); return; }
                    }
                }

                // Using a crafting table / furnace / TNT instead of placing against it.
                if (pressed && hasHit && player != null)
                {
                    var target = world.Def(hit);
                    if (target.Special == "crafting") { mgr.OpenCrafting(); return; }
                    if (target.Special == "furnace") { mgr.OpenFurnace(world, hit); return; }
                    if (target.IsTnt && !held.Empty && held.Item.LocalId == "flint_and_steel")
                    {
                        mgr.BreakBlock(world, hit, false);
                        player.Inv.WearHeld();
                        return;
                    }
                }

                BlockDef place = player != null ? held.Item?.Block : selected;
                Vector3Int cell;
                bool valid;
                if (hasHit) { cell = hit + normal; valid = true; }
                else if (!desktop) { cell = world.ToBlock(handPos + dir.normalized * cfg.AirPlaceDistance.Value); valid = world.Get(cell) == 0; }
                else { cell = default; valid = false; }
                valid = valid && place != null && mgr.CanPlace(world, cell);
                // Torches and plants need something under them; grass can't go on air.
                if (valid && place.Render == RenderKind.Torch && !BlockRegistry.SolidT[world.Get(cell + Vector3Int.down)]) valid = false;

                if (aiming && valid)
                {
                    if (_ghostBlock != place.Index)
                    {
                        var m = MeshUtil.BlockCube(place, 1f);
                        _ghostMesh.Clear();
                        _ghostMesh.vertices = m.vertices;
                        _ghostMesh.uv = m.uv;
                        _ghostMesh.triangles = m.triangles;
                        _ghostMesh.RecalculateBounds();
                        Object.Destroy(m);
                        _ghostBlock = place.Index;
                    }
                    _ghost.transform.position = world.Center(cell);
                    _ghost.transform.localScale = Vector3.one * (s * 1.001f);
                    _ghost.SetActive(true);
                }
                else _ghost.SetActive(false);

                if (trigDown && valid && (pressed || (Time.time >= _repeatAt && cell != _lastPlaced)))
                {
                    if (mgr.PlaceBlock(world, cell, place) && player != null) player.Inv.UseHeld();
                    _lastPlaced = cell;
                    _repeatAt = Time.time + (pressed ? 0.35f : 0.16f);
                }
                if (!trigDown) _lastPlaced = new Vector3Int(int.MinValue, 0, 0);
            }
            else
            {
                _ghost.SetActive(false);

                // Pointing at a mob closer than the block: hit it.
                if (pressed)
                {
                    float blockDist = hasHit ? Vector3.Distance(origin, world.Center(hit)) : reach;
                    foreach (var m in Mob.Live)
                        if (m.Box.IntersectRay(new Ray(origin, dir), out float d) && d <= Mathf.Min(reach, blockDist))
                        {
                            m.Hurt(AttackDamage(held), origin);
                            _mining = false;
                            return;
                        }
                }

                if (aiming && hasHit)
                {
                    _outline.transform.position = world.Center(hit);
                    _outline.transform.localScale = Vector3.one * s;
                    _outline.SetActive(true);
                }
                else _outline.SetActive(false);

                if (trigDown && hasHit)
                {
                    var def = world.Def(hit);
                    if (!_mining || hit != _mineBlock)
                    {
                        _mining = true;
                        _mineBlock = hit;
                        _mineProgress = 0f;
                    }
                    if (!def.Unbreakable)
                    {
                        float seconds = mgr.Survival ? Survival.BreakSeconds(def, held) : def.BreakTime;
                        _mineProgress += dt / Mathf.Max(0.05f, seconds);
                        if (Time.time >= _nextHitSound)
                        {
                            mgr.Sounds.PlayBlock(def, "step", world.Center(hit), 0.3f);
                            _nextHitSound = Time.time + 0.25f;
                        }
                        if (_mineProgress >= 1f)
                        {
                            mgr.BreakBlock(world, hit, true);
                            _mining = false;
                            mgr.Cracks.Hide();
                        }
                        else mgr.Cracks.Show(world, hit, _mineProgress);
                    }
                }
                else if (_mining)
                {
                    _mining = false;
                    mgr.Cracks.Hide();
                }
            }
        }

        /// <summary>The block a hand touched: just behind the hit point, or failing that a solid block right next to the hand.</summary>
        private static Vector3Int TouchedBlock(VoxelWorld w, RaycastHit info, Vector3 handPos)
        {
            if (info.normal != Vector3.zero)
            {
                var c = w.ToBlock(info.point - info.normal * (w.S * 0.05f));
                if (BlockRegistry.SolidT[w.Get(c)]) return c;
            }
            var at = w.ToBlock(handPos);
            if (BlockRegistry.SolidT[w.Get(at)]) return at;
            foreach (var d in Probe)
            {
                var c = w.ToBlock(handPos + d * (w.S * 0.3f));
                if (BlockRegistry.SolidT[w.Get(c)]) return c;
            }
            return at;
        }

        private static readonly Vector3[] Probe = { Vector3.down, Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.up };

        private static float AttackDamage(ItemStack held) => held.Empty || held.Item.ToolType == null ? 1.5f : held.Item.Damage;
    }
}
