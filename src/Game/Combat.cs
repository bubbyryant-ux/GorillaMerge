using System.Collections.Generic;
using GorillaLocomotion;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// An arrow: flies with gravity, sticks into blocks, hurts mobs or you. Arrows you shot can be picked back up.
    /// </summary>
    public class Arrow : MonoBehaviour
    {
        public static readonly List<Arrow> Live = new List<Arrow>();
        private static Material _mat;
        private static Mesh _mesh;

        private VoxelWorld _w;
        private MergeManager _mgr;
        private Vector3 _vel;
        private Mob _shooter;
        private bool _byPlayer, _stuck;
        private float _age;

        public static void Spawn(MergeManager mgr, VoxelWorld w, Vector3 pos, Vector3 vel, Mob shooter, bool byPlayer)
        {
            if (_mat == null)
            {
                var tex = GameAssets.LoadTexture("mc:entity/projectiles/arrow", null, false);
                if (tex != null) tex.filterMode = FilterMode.Point;
                _mat = Materials.VertexLitTransparent(tex != null ? tex : Texture2D.whiteTexture, Color.white);
                if (_mat.HasProperty("_Cull")) _mat.SetFloat("_Cull", 0f);
                _mesh = BuildMesh(tex);
            }
            var go = MeshUtil.Object("GM_arrow", _mesh, _mat);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * w.S;
            go.transform.rotation = Quaternion.LookRotation(vel);
            var a = go.AddComponent<Arrow>();
            a._w = w;
            a._mgr = mgr;
            a._vel = vel;
            a._shooter = shooter;
            a._byPlayer = byPlayer;
            Live.Add(a);
        }

        // Two crossed strips from the top of Minecraft's arrow picture (16 x 5 pixels), one block long.
        private static Mesh BuildMesh(Texture2D tex)
        {
            float tw = tex != null ? tex.width : 32, th = tex != null ? tex.height : 32;
            float u1 = 16 / tw, v0 = 1f - 5 / th;
            var m = new Mesh { name = "GM_arrow" };
            float L = 0.5f, H = 5f / 32f;
            m.vertices = new[]
            {
                new Vector3(0, -H, -L), new Vector3(0, H, -L), new Vector3(0, H, L), new Vector3(0, -H, L),
                new Vector3(-H, 0, -L), new Vector3(H, 0, -L), new Vector3(H, 0, L), new Vector3(-H, 0, L),
            };
            m.uv = new[]
            {
                new Vector2(0, v0), new Vector2(0, 1), new Vector2(u1, 1), new Vector2(u1, v0),
                new Vector2(0, v0), new Vector2(0, 1), new Vector2(u1, 1), new Vector2(u1, v0),
            };
            var c = new Color32(255, 255, 255, 255);
            m.colors32 = new[] { c, c, c, c, c, c, c, c };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            m.RecalculateBounds();
            return m;
        }

        private void OnDestroy() => Live.Remove(this);

        private void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            _age += dt;
            if (_w == null || !_w.Root.activeInHierarchy || _age > 60f) { Destroy(gameObject); return; }
            float s = _w.S;
            var gp = GTPlayer.Instance;
            var light = _mgr.LightAt(_w, transform.position);
            Materials.SetColor(_mat, new Color(light, light, light, 1f));

            if (_stuck)
            {
                // Walk past (or reach for) your own arrows to pick them up.
                if (_byPlayer && _mgr.Player != null && _age > 0.5f &&
                    ((gp.headCollider.transform.position - transform.position).sqrMagnitude < 0.5f ||
                     (gp.GetHandPosition(true) - transform.position).sqrMagnitude < 0.12f ||
                     (gp.GetHandPosition(false) - transform.position).sqrMagnitude < 0.12f))
                {
                    var arrow = Items.Local(_mgr.SurvivalPack, "arrow");
                    if (arrow != null && _mgr.Player.Inv.Add(arrow, 1) == 0)
                    {
                        _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "pickup", transform.position, 0.4f, 1.6f);
                        Destroy(gameObject);
                    }
                }
                return;
            }

            _vel += Vector3.down * 20f * s * dt;
            _vel *= Mathf.Pow(0.99f, dt * 20f);
            var from = transform.position;
            var step = _vel * dt;
            float len = step.magnitude;
            if (len < 1e-5f) return;
            var dir = step / len;

            // Mobs first (anything closer than the next block).
            float blockDist = len;
            bool hitBlock = _w.Raycast(from, dir, len, out var hb, out var hn);
            if (hitBlock)
            {
                var center = _w.Center(hb);
                blockDist = Vector3.Dot(center - (Vector3)hn * s * 0.5f - from, dir);
                blockDist = Mathf.Clamp(blockDist, 0, len);
            }
            float speedBlocksPerTick = _vel.magnitude / s / 20f;
            float damage = Mathf.Ceil(speedBlocksPerTick * 2f);
            foreach (var m in Mob.Live)
            {
                if (m == _shooter || m.Dying) continue;
                if (m.Box.IntersectRay(new Ray(from, dir), out float d) && d <= blockDist)
                {
                    m.Hurt(damage, from, _byPlayer);
                    _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "bowhit", transform.position, 0.6f);
                    Destroy(gameObject);
                    return;
                }
            }
            if (!_byPlayer && _mgr.Player != null && !_mgr.Player.Dead)
            {
                var body = gp.bodyCollider.bounds;
                body.Encapsulate(gp.headCollider.bounds);
                if (body.IntersectRay(new Ray(from, dir), out float d) && d <= blockDist)
                {
                    _mgr.Player.Damage(Mathf.Max(2f, damage), "were shot by " + (_shooter != null ? _shooter.Def.Name : "an arrow"), from);
                    Destroy(gameObject);
                    return;
                }
            }
            if (hitBlock)
            {
                transform.position = from + dir * Mathf.Max(0, blockDist - s * 0.15f);
                _stuck = true;
                _age = Mathf.Max(_age, 0f);
                _mgr.Sounds.PlayEvent(_mgr.SurvivalPack, "bowhit", transform.position, 0.5f);
                if (!_byPlayer) _age = 50f; // skeleton arrows vanish after a few seconds
                return;
            }
            transform.position = from + step;
            transform.rotation = Quaternion.LookRotation(_vel);
        }
    }

    /// <summary>The item you're holding, shown in your right gorilla hand.</summary>
    public class HeldItemView
    {
        private readonly GameObject _go;
        private readonly MeshFilter _mf;
        private readonly MeshRenderer _mr;
        private ItemDef _shown;
        private Material _blockMat, _itemMat;

        public HeldItemView()
        {
            _go = new GameObject("GM_HeldItem");
            Object.DontDestroyOnLoad(_go);
            _mf = _go.AddComponent<MeshFilter>();
            _mr = _go.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _go.SetActive(false);
        }

        public void Tick(MergeManager mgr, ItemDef item, float light)
        {
            var gp = GTPlayer.Instance;
            bool right = !Plugin.Instance.SwapHands.Value;
            var ct = gp != null ? gp.GetControllerTransform(!right) : null;
            if (item == null || ct == null) { if (_go.activeSelf) _go.SetActive(false); _shown = null; return; }
            if (item != _shown)
            {
                _shown = item;
                if (item.Block != null)
                {
                    if (_blockMat == null) _blockMat = Materials.VertexLit(BlockRegistry.Atlas);
                    _mf.sharedMesh = MeshUtil.BlockCube(item.Block, 0.09f);
                    _mr.sharedMaterial = _blockMat;
                }
                else
                {
                    if (_itemMat == null)
                    {
                        _itemMat = Materials.VertexLitTransparent(Items.Atlas, Color.white);
                        if (_itemMat.HasProperty("_Cull")) _itemMat.SetFloat("_Cull", 0f);
                    }
                    _mf.sharedMesh = ItemEntity.ItemQuad(item, 0.16f);
                    _mr.sharedMaterial = _itemMat;
                }
            }
            if (!_go.activeSelf) _go.SetActive(true);
            var hand = gp.GetHandPosition(!right);
            // Held like a Minecraft tool: sticking up out of the fist, tilted forward.
            var rot = ct.rotation * Quaternion.Euler(Plugin.Instance.AimPitch.Value, 0, 0);
            _go.transform.position = hand + rot * new Vector3(0, 0.04f, 0.07f);
            _go.transform.rotation = rot * Quaternion.Euler(-30f, item.Block != null ? 45f : 90f, item.Block != null ? 0 : -45f);
            var mat = item.Block != null ? _blockMat : _itemMat;
            Materials.SetColor(mat, new Color(light, light, light, 1f));
        }

        public void Hide() { if (_go.activeSelf) _go.SetActive(false); _shown = null; }
    }
}
