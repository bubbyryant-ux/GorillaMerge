using System;
using System.Collections.Generic;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// A Minecraft-style menu above your left hand: a MENU button, and when it's open a column of real
    /// Minecraft buttons (big text). Press them with your right finger. It always turns to face you.
    /// </summary>
    public class WristUI
    {
        public enum Mode { Hidden, Build, PublicNotice }

        private struct Button
        {
            public string Text;
            public float X, Y, W, H;
            public Action OnPress;
        }

        private readonly GameObject _root;
        private readonly Canvas2D _c;
        private readonly List<Button> _buttons = new List<Button>();
        private int _touching = -1;
        private float _cooldown;
        private bool _menuOpen;
        private string _confirm;
        private const float Px = 0.0017f;

        public WristUI()
        {
            _root = new GameObject("GM_Wrist");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _c = new Canvas2D("wrist", Px, _root.transform);
        }

        public bool MenuOpen { get => _menuOpen; set { _menuOpen = value; _confirm = null; } }

        public void RebuildMenu(MergeManager mgr) { }

        private List<(string text, Action action)> Entries(MergeManager mgr)
        {
            var list = new List<(string, Action)>();
            if (mgr.Player != null && mgr.Survival) list.Add(("Inventory", () => { _menuOpen = false; mgr.OpenInventory(); }));
            if (mgr.CurrentPack != null) list.Add(("Back to Gorilla Tag", () => mgr.Travel(null)));
            foreach (var p in mgr.Packs)
            {
                if (p == mgr.CurrentPack) continue;
                var target = p;
                list.Add((p.Type == PackType.Voxel ? "Go to " + p.Name : "Go to " + p.Name, () => mgr.Travel(target)));
            }
            if (mgr.Player != null)
                list.Add(("Game Mode: " + (mgr.Survival ? "Survival" : "Creative"), () => mgr.ToggleGameMode()));
            list.Add(("Building: " + (mgr.BuildMode ? "ON" : "OFF"), () => mgr.BuildMode = !mgr.BuildMode));
            if (mgr.CurrentPack == null && mgr.VoxelPacks.Count > 1) list.Add(("Blocks from: " + mgr.BuildPack.Name, () => mgr.NextBuildPack()));
            var net = mgr.Net;
            if (net != null && net.Active)
                list.Add((net.IsOwner ? $"Your world: {net.FriendCount} friend(s) here" : $"In {Short(net.OwnerName)}'s world", () => { }));
            string clear = mgr.CurrentPack == null ? "Clear My Builds" : "New World";
            if (mgr.CurrentPack == null || net == null || !net.Active)
                list.Add((_confirm == "clear" ? "Press again: " + clear : clear, () =>
                {
                    if (_confirm == "clear") { _confirm = null; _menuOpen = false; mgr.ClearCurrent(); }
                    else _confirm = "clear";
                }));
            list.Add(("Close", () => { _menuOpen = false; _confirm = null; }));
            return list;
        }

        private static string Short(string name) => string.IsNullOrEmpty(name) ? "a friend" : name.Length > 12 ? name.Substring(0, 12) : name;

        public void Tick(MergeManager mgr, Mode mode, IList<BlockDef> items, int selected)
        {
            var tagger = GorillaTagger.Instance;
            if (mode == Mode.Hidden || tagger == null || tagger.leftHandTransform == null || tagger.mainCamera == null)
            {
                if (_root.activeSelf) _root.SetActive(false);
                return;
            }
            if (!_root.activeSelf) _root.SetActive(true);

            float scale = Plugin.Instance.UiScale.Value * Mathf.Max(0.3f, GorillaLocomotion.GTPlayer.Instance != null ? GorillaLocomotion.GTPlayer.Instance.scale : 1f);
            var head = tagger.mainCamera.transform.position;
            var pos = tagger.leftHandTransform.position + Vector3.up * (0.09f * scale);
            _root.transform.position = pos;
            var look = pos - head;
            if (look.sqrMagnitude > 1e-6f) _root.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            _root.transform.localScale = Vector3.one * scale;

            // Lay out buttons (Minecraft GUI pixels, origin at the panel's bottom-centre so it grows upward).
            _buttons.Clear();
            if (mode == Mode.PublicNotice)
            {
                _buttons.Add(new Button { Text = "Go Offline", X = -50, Y = -20, W = 100, H = 20, OnPress = () => mgr.GoOffline() });
            }
            else
            {
                _buttons.Add(new Button { Text = _menuOpen ? "Close" : "Menu", X = -25, Y = -20, W = 50, H = 20, OnPress = () => { _menuOpen = !_menuOpen; _confirm = null; } });
                if (_menuOpen)
                {
                    var entries = Entries(mgr);
                    float y = -20 - 24 * entries.Count - 4;
                    foreach (var e in entries)
                    {
                        _buttons.Add(new Button { Text = e.text, X = -100, Y = y, W = 200, H = 20, OnPress = e.action });
                        y += 24;
                    }
                }
            }

            // Finger presses (right index finger).
            _cooldown -= Time.deltaTime;
            var tip = tagger.rightHandTriggerCollider != null ? tagger.rightHandTriggerCollider.transform.position : tagger.rightHandTransform.position;
            var local = _c.Root.transform.InverseTransformPoint(tip);
            float fx = local.x / Px, fy = -local.y / Px;
            int hover = -1;
            if (Mathf.Abs(local.z) < 0.03f / Mathf.Max(0.3f, scale))
                for (int i = 0; i < _buttons.Count; i++)
                {
                    var b = _buttons[i];
                    if (fx >= b.X && fx <= b.X + b.W && fy >= b.Y && fy <= b.Y + b.H) { hover = i; break; }
                }
            if (hover >= 0 && hover != _touching && _cooldown <= 0f)
            {
                _cooldown = 0.35f;
                mgr.Sounds.PlayEvent(mgr.UiPack, "click", tip, 0.5f);
                try { tagger.StartVibration(false, 0.2f, 0.04f); } catch { }
                var action = _buttons[hover].OnPress;
                action?.Invoke();
            }
            _touching = hover;

            // Draw.
            var btn = GuiTex.Get("gui/sprites/widget/button");
            var btnHi = GuiTex.Get("gui/sprites/widget/button_highlighted");
            _c.Begin();
            if (mode == Mode.PublicNotice)
            {
                _c.Rect(-120, -58, 240, 34, new Color32(0, 0, 0, 150));
                _c.TextCentered("GorillaMerge is off in public lobbies", 0, -54, new Color32(255, 220, 100, 255));
                _c.TextCentered("Join a modded lobby or go offline", 0, -42, new Color32(255, 255, 255, 255));
            }
            for (int i = 0; i < _buttons.Count; i++)
            {
                var b = _buttons[i];
                var tex = i == hover ? btnHi ?? btn : btn;
                if (tex != null) NineSlice(tex, b.X, b.Y, b.W, b.H);
                else _c.Rect(b.X, b.Y, b.W, b.H, i == hover ? new Color32(110, 150, 90, 240) : new Color32(90, 90, 90, 230));
                _c.Front();
                _c.TextCentered(b.Text, b.X + b.W / 2, b.Y + 6, i == hover ? new Color32(255, 255, 160, 255) : new Color32(255, 255, 255, 255));
                _c.Back();
            }
            _c.End();
        }

        // Minecraft buttons are 200x20 pictures; stretch the middle so narrow buttons keep their edges.
        private void NineSlice(Texture2D tex, float x, float y, float w, float h)
        {
            var white = new Color32(255, 255, 255, 255);
            int tw = tex.width, th = tex.height;
            float half = w / 2f;
            _c.Sprite(tex, x, y, half, h, new RectInt(0, 0, Mathf.Min(tw / 2, (int)half), th), white);
            _c.Sprite(tex, x + half, y, half, h, new RectInt(tw - Mathf.Min(tw / 2, (int)half), 0, Mathf.Min(tw / 2, (int)half), th), white);
        }
    }
}
