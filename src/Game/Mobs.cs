using System;
using System.Collections.Generic;
using GorillaLocomotion;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GorillaMerge
{
    public class MobDef
    {
        public string Id, Name, Model, Texture, Texture2, Spawn;
        public Pack Pack;
        public float Health = 20, Speed = 2.2f, Damage = 3, Width = 0.6f, Height = 1.9f;
        public int Weight = 100;
        public bool Hostile, Explodes, BurnsInDay, Ranged, Climbs, Teleports, Neutral, SlowFall, Shearable;
        public string Say, Hurt, Death, Step, Fuse, Shoot, Teleport;
        public JArray Drops;
        public Texture2D Tex, Tex2;

        public static List<MobDef> Load(Pack pack)
        {
            var list = new List<MobDef>();
            if (!(pack.Json["mobs"] is JArray arr)) return list;
            foreach (var t in arr)
            {
                if (!(t is JObject o)) continue;
                var s = o["sounds"] as JObject;
                var d = new MobDef
                {
                    Id = (string)o["id"], Name = (string)o["name"] ?? (string)o["id"], Model = (string)o["model"] ?? "humanoid",
                    Texture = (string)o["texture"], Texture2 = (string)o["texture2"], Spawn = (string)o["spawn"] ?? "night", Pack = pack,
                    Health = (float?)o["health"] ?? 20, Speed = (float?)o["speed"] ?? 2.2f, Damage = (float?)o["damage"] ?? 3,
                    Width = (float?)o["width"] ?? 0.6f, Height = (float?)o["height"] ?? 1.9f, Weight = (int?)o["weight"] ?? 100,
                    Hostile = (bool?)o["hostile"] ?? true, Explodes = (bool?)o["explodes"] ?? false, BurnsInDay = (bool?)o["burnsInDay"] ?? false,
                    Ranged = (bool?)o["ranged"] ?? false, Climbs = (bool?)o["climbs"] ?? false, Teleports = (bool?)o["teleports"] ?? false,
                    Neutral = (bool?)o["neutral"] ?? false, SlowFall = (bool?)o["slowFall"] ?? false, Shearable = (bool?)o["shearable"] ?? false,
                    Say = (string)s?["say"], Hurt = (string)s?["hurt"], Death = (string)s?["death"], Step = (string)s?["step"],
                    Fuse = (string)s?["fuse"], Shoot = (string)s?["shoot"], Teleport = (string)s?["teleport"],
                    Drops = o["drops"] as JArray,
                };
                d.Tex = Load(d.Texture, pack);
                d.Tex2 = Load(d.Texture2, pack);
                list.Add(d);
            }
            return list;
        }

        private static Texture2D Load(string spec, Pack pack)
        {
            var t = GameAssets.LoadTexture(spec, pack.Dir, false);
            if (t != null) { t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp; }
            return t;
        }

        public IEnumerable<string> Sounds() => new[] { Say, Hurt, Death, Step, Fuse, Shoot, Teleport };
    }

    /// <summary>
    /// A Minecraft mob: a box model built from its real skin layout, walking physics on the block grid, and
    /// Minecraft-like behaviour. Zombies chase and hit, skeletons keep their distance and shoot, spiders climb
    /// walls and are only angry in the dark, creepers hiss and explode, endermen leave you alone until you hit
    /// them and then teleport around, and pigs, cows, sheep and chickens wander and run when hurt.
    /// </summary>
    public class Mob : MonoBehaviour
    {
        public static readonly List<Mob> Live = new List<Mob>();

        public MobDef Def;
        public VoxelWorld World;
        public float Hp;
        public bool Sheared;
        private MergeManager _mgr;
        private Vector3 _vel;
        private float _yaw, _walk, _hurtFlash, _attackCd, _sayAt, _stepAt, _fuse, _fleeUntil, _wanderAt, _deadAt = -1, _burnTick, _shootAt, _regrowAt;
        private Vector3 _wander;
        private bool _onGround, _hissed, _angry;
        private Transform _head, _body;
        private readonly List<Transform> _legs = new List<Transform>(), _arms = new List<Transform>(), _wings = new List<Transform>();
        private readonly List<GameObject> _wool = new List<GameObject>();
        private readonly List<Vector3> _legBase = new List<Vector3>();
        private Material _mat, _mat2;
        private float _px; // meters per model pixel

        public Bounds Box
        {
            get
            {
                float s = World.S;
                return new Bounds(transform.position + Vector3.up * Def.Height * s * 0.5f, new Vector3(Def.Width * s, Def.Height * s, Def.Width * s));
            }
        }

        public bool Dying => _deadAt >= 0;

        public static Mob Spawn(MergeManager mgr, VoxelWorld w, MobDef def, Vector3 feet)
        {
            var go = new GameObject("GM_Mob_" + def.Id);
            go.transform.position = feet;
            var m = go.AddComponent<Mob>();
            m.Def = def;
            m.World = w;
            m._mgr = mgr;
            m.Hp = def.Health;
            m._yaw = UnityEngine.Random.Range(0f, 360f);
            m._sayAt = Time.time + UnityEngine.Random.Range(3f, 10f);
            m._shootAt = Time.time + UnityEngine.Random.Range(1f, 3f);
            m.Build();
            Live.Add(m);
            return m;
        }

        private void OnDestroy() => Live.Remove(this);

        // ---------------- model ----------------

        private void Build()
        {
            _px = World.S / 16f;
            _mat = Materials.VertexLit(Def.Tex != null ? Def.Tex : Texture2D.whiteTexture);
            if (Def.Tex2 != null)
            {
                _mat2 = Materials.VertexLitTransparent(Def.Tex2, Color.white);
                if (_mat2.HasProperty("_Cull")) _mat2.SetFloat("_Cull", 0f);
            }
            int tw = Def.Tex != null ? Def.Tex.width : 64, th = Def.Tex != null ? Def.Tex.height : 64;
            int tw2 = Def.Tex2 != null ? Def.Tex2.width : 64, th2 = Def.Tex2 != null ? Def.Tex2.height : 32;

            // A body part: a pivot (joint) with a box hanging off it, using the skin's box-UV layout at (u, v).
            // grow inflates the box without changing its texture (Minecraft's wool and hat layers).
            Transform Part(string name, Vector3 pivot, Vector3 min, Vector3 size, int u, int v, Transform parent = null, bool layer2 = false, float grow = 0f)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent ?? transform, false);
                go.transform.localPosition = pivot * _px;
                var g = Vector3.one * grow;
                var mesh = BoxMesh((min - g) * _px, (size + g * 2) * _px, size, u, v, layer2 ? tw2 : tw, layer2 ? th2 : th);
                var child = MeshUtil.Object("mesh", mesh, layer2 ? _mat2 : _mat, go.transform);
                if (layer2) _wool.Add(child);
                return go.transform;
            }
            void Legs(Vector3[] pivots, Vector3 min, Vector3 size, int u, int v)
            {
                foreach (var p in pivots) _legs.Add(Part("leg", p, min, size, u, v));
            }
            switch (Def.Model)
            {
                case "creeper":
                    _body = Part("body", new Vector3(0, 6, 0), new Vector3(-4, 0, -2), new Vector3(8, 12, 4), 16, 16);
                    _head = Part("head", new Vector3(0, 18, 0), new Vector3(-4, 0, -4), new Vector3(8, 8, 8), 0, 0);
                    Legs(new[] { new Vector3(-2, 6, 4), new Vector3(2, 6, 4), new Vector3(-2, 6, -4), new Vector3(2, 6, -4) }, new Vector3(-2, -6, -2), new Vector3(4, 6, 4), 0, 16);
                    break;
                case "pig":
                    _body = Part("body", new Vector3(0, 10, 0), new Vector3(-5, -8, -4), new Vector3(10, 16, 8), 28, 8);
                    _body.localRotation = Quaternion.Euler(-90f, 0, 0);
                    _head = Part("head", new Vector3(0, 12, 6), new Vector3(-4, -4, 0), new Vector3(8, 8, 8), 0, 0);
                    Part("snout", Vector3.zero, new Vector3(-2, -3, 8), new Vector3(4, 3, 1), 16, 16, _head);
                    Legs(new[] { new Vector3(-3, 6, 5), new Vector3(3, 6, 5), new Vector3(-3, 6, -7), new Vector3(3, 6, -7) }, new Vector3(-2, -6, -2), new Vector3(4, 6, 4), 0, 16);
                    break;
                case "cow":
                    _body = Part("body", new Vector3(0, 17, 0), new Vector3(-6, -9, -5), new Vector3(12, 18, 10), 18, 4);
                    _body.localRotation = Quaternion.Euler(-90f, 0, 0);
                    _head = Part("head", new Vector3(0, 20, 8), new Vector3(-4, -4, 0), new Vector3(8, 8, 6), 0, 0);
                    Part("hornR", Vector3.zero, new Vector3(4, 2, 2), new Vector3(1, 3, 1), 22, 0, _head);
                    Part("hornL", Vector3.zero, new Vector3(-5, 2, 2), new Vector3(1, 3, 1), 22, 0, _head);
                    Legs(new[] { new Vector3(-4, 12, 6), new Vector3(4, 12, 6), new Vector3(-4, 12, -7), new Vector3(4, 12, -7) }, new Vector3(-2, -12, -2), new Vector3(4, 12, 4), 0, 16);
                    break;
                case "sheep":
                    _body = Part("body", new Vector3(0, 15, 0), new Vector3(-4, -8, -3), new Vector3(8, 16, 6), 28, 8);
                    _body.localRotation = Quaternion.Euler(-90f, 0, 0);
                    if (_mat2 != null) Part("wool", Vector3.zero, new Vector3(-4, -8, -3), new Vector3(8, 16, 6), 28, 8, _body, true, 1.75f);
                    _head = Part("head", new Vector3(0, 18, 7), new Vector3(-3, -3, 0), new Vector3(6, 6, 8), 0, 0);
                    if (_mat2 != null) Part("woolHead", Vector3.zero, new Vector3(-3, -3, 0), new Vector3(6, 6, 6), 0, 0, _head, true, 0.6f);
                    Legs(new[] { new Vector3(-3, 12, 5), new Vector3(3, 12, 5), new Vector3(-3, 12, -6), new Vector3(3, 12, -6) }, new Vector3(-2, -12, -2), new Vector3(4, 12, 4), 0, 16);
                    if (_mat2 != null) foreach (var leg in _legs) Part("woolLeg", Vector3.zero, new Vector3(-2, -6, -2), new Vector3(4, 6, 4), 0, 16, leg, true, 0.5f);
                    break;
                case "chicken":
                    _body = Part("body", new Vector3(0, 8, 0), new Vector3(-3, -4, -3), new Vector3(6, 8, 6), 0, 9);
                    _body.localRotation = Quaternion.Euler(-90f, 0, 0);
                    _head = Part("head", new Vector3(0, 9, 3), new Vector3(-2, 0, 0), new Vector3(4, 6, 3), 0, 0);
                    Part("beak", Vector3.zero, new Vector3(-2, 2, 3), new Vector3(4, 2, 2), 14, 0, _head);
                    Part("wattle", Vector3.zero, new Vector3(-1, 0, 3), new Vector3(2, 2, 2), 14, 4, _head);
                    Legs(new[] { new Vector3(-1.5f, 5, 0), new Vector3(1.5f, 5, 0) }, new Vector3(-1.5f, -5, -1.5f), new Vector3(3, 5, 3), 26, 0);
                    _wings.Add(Part("wingR", new Vector3(3, 11, 0), new Vector3(0, -4, -3), new Vector3(1, 4, 6), 24, 13));
                    _wings.Add(Part("wingL", new Vector3(-3, 11, 0), new Vector3(-1, -4, -3), new Vector3(1, 4, 6), 24, 13));
                    break;
                case "spider":
                    Part("neck", new Vector3(0, 9, 0), new Vector3(-3, -3, -3), new Vector3(6, 6, 6), 0, 0);
                    _body = Part("body", new Vector3(0, 9, -3), new Vector3(-5, -4, -12), new Vector3(10, 8, 12), 0, 12);
                    _head = Part("head", new Vector3(0, 9, 3), new Vector3(-4, -4, 0), new Vector3(8, 8, 8), 32, 4);
                    for (int i = 0; i < 8; i++)
                    {
                        bool right = i % 2 == 0;
                        float z = 2 - (i / 2) * 1.3f;
                        var leg = Part("leg", new Vector3(right ? 4 : -4, 9, z), right ? new Vector3(0, -1, -1) : new Vector3(-16, -1, -1), new Vector3(16, 2, 2), 18, 0);
                        float spread = (1.5f - i / 2) * 22f;
                        var baseRot = new Vector3(0, right ? -spread : spread, right ? -35f : 35f);
                        leg.localRotation = Quaternion.Euler(baseRot);
                        _legs.Add(leg);
                        _legBase.Add(baseRot);
                    }
                    break;
                case "skeleton":
                    _legs.Add(Part("legR", new Vector3(2, 12, 0), new Vector3(-1, -12, -1), new Vector3(2, 12, 2), 0, 16));
                    _legs.Add(Part("legL", new Vector3(-2, 12, 0), new Vector3(-1, -12, -1), new Vector3(2, 12, 2), 0, 16));
                    _body = Part("body", new Vector3(0, 12, 0), new Vector3(-4, 0, -2), new Vector3(8, 12, 4), 16, 16);
                    _arms.Add(Part("armR", new Vector3(5, 22, 0), new Vector3(-1, -10, -1), new Vector3(2, 12, 2), 40, 16));
                    _arms.Add(Part("armL", new Vector3(-5, 22, 0), new Vector3(-1, -10, -1), new Vector3(2, 12, 2), 40, 16));
                    _head = Part("head", new Vector3(0, 24, 0), new Vector3(-4, 0, -4), new Vector3(8, 8, 8), 0, 0);
                    break;
                case "enderman":
                    _legs.Add(Part("legR", new Vector3(2, 30, 0), new Vector3(-1, -30, -1), new Vector3(2, 30, 2), 56, 0));
                    _legs.Add(Part("legL", new Vector3(-2, 30, 0), new Vector3(-1, -30, -1), new Vector3(2, 30, 2), 56, 0));
                    _body = Part("body", new Vector3(0, 30, 0), new Vector3(-4, 0, -2), new Vector3(8, 12, 4), 32, 16);
                    _arms.Add(Part("armR", new Vector3(5, 40, 0), new Vector3(-1, -28, -1), new Vector3(2, 30, 2), 56, 0));
                    _arms.Add(Part("armL", new Vector3(-5, 40, 0), new Vector3(-1, -28, -1), new Vector3(2, 30, 2), 56, 0));
                    _head = Part("head", new Vector3(0, 42, 0), new Vector3(-4, 0, -4), new Vector3(8, 8, 8), 0, 0);
                    if (_mat2 != null) Part("eyes", Vector3.zero, new Vector3(-4, 0, -4), new Vector3(8, 8, 8), 0, 0, _head, true, 0.05f);
                    break;
                default: // humanoid (zombie): 64x64 skin layout
                    _legs.Add(Part("legR", new Vector3(2, 12, 0), new Vector3(-2, -12, -2), new Vector3(4, 12, 4), 0, 16));
                    _legs.Add(Part("legL", new Vector3(-2, 12, 0), new Vector3(-2, -12, -2), new Vector3(4, 12, 4), th >= 64 ? 16 : 0, th >= 64 ? 48 : 16));
                    _body = Part("body", new Vector3(0, 12, 0), new Vector3(-4, 0, -2), new Vector3(8, 12, 4), 16, 16);
                    _arms.Add(Part("armR", new Vector3(6, 22, 0), new Vector3(-2, -10, -2), new Vector3(4, 12, 4), 40, 16));
                    _arms.Add(Part("armL", new Vector3(-6, 22, 0), new Vector3(-2, -10, -2), new Vector3(4, 12, 4), th >= 64 ? 32 : 40, th >= 64 ? 48 : 16));
                    _head = Part("head", new Vector3(0, 24, 0), new Vector3(-4, 0, -4), new Vector3(8, 8, 8), 0, 0);
                    break;
            }
        }

        /// <summary>A box with Minecraft's box-UV layout: top/bottom strip above, the four sides below it.</summary>
        private static Mesh BoxMesh(Vector3 min, Vector3 size, Vector3 sizePx, int u, int v, int tw, int th)
        {
            float w = sizePx.x, h = sizePx.y, d = sizePx.z;
            var max = min + size;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var cols = new List<Color32>();
            Vector2 T(float px, float py) => new Vector2(px / tw, 1f - py / th);
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 e, float u0, float v0, float u1, float v1, byte shade)
            {
                int s = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(e);
                uvs.Add(T(u0, v1)); uvs.Add(T(u0, v0)); uvs.Add(T(u1, v0)); uvs.Add(T(u1, v1));
                for (int i = 0; i < 4; i++) cols.Add(new Color32(shade, shade, shade, 255));
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
            }
            float x0 = min.x, y0 = min.y, z0 = min.z, x1 = max.x, y1 = max.y, z1 = max.z;
            // Front (+Z, the face), back (-Z), right (+X), left (-X), top, bottom.
            Face(new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), u + d, v + d, u + d + w, v + d + h, 230);
            Face(new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), u + d + w + d, v + d, u + d + w + d + w, v + d + h, 200);
            Face(new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), u, v + d, u + d, v + d + h, 170);
            Face(new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0), u + d + w, v + d, u + d + w + d, v + d + h, 170);
            Face(new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), u + d, v, u + d + w, v + d, 255);
            Face(new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), u + d + w, v, u + d + w + w, v + d, 130);
            var m = new Mesh { name = "GM_mobbox" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetColors(cols);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        // ---------------- behaviour ----------------

        /// <summary>Hostile right now? Spiders only in the dark, endermen and spiders once you've hit them.</summary>
        private bool Aggressive => Def.Hostile && (!Def.Neutral || _angry || (Def.Climbs && _mgr.Daylight < 0.5f));

        private void Update()
        {
            if (World == null || !World.Root.activeInHierarchy || _mgr.Player == null) { Destroy(gameObject); return; }
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            float s = World.S;
            var gp = GTPlayer.Instance;
            var target = gp.bodyCollider.bounds.center;
            var to = target - transform.position;
            float dist = new Vector2(to.x, to.z).magnitude / s; // in blocks

            if (_deadAt >= 0)
            {
                transform.rotation = Quaternion.Euler(0, _yaw, Mathf.Min(90f, (Time.time - _deadAt) * 180f));
                Tint(new Color(1f, 0.4f, 0.4f));
                if (Time.time - _deadAt > 1f) { _mgr.Fx.Puff(Box.center, s); Destroy(gameObject); }
                return;
            }
            if (dist > (Def.Hostile ? 64 : 96)) { Destroy(gameObject); return; }
            if (Sheared && Time.time >= _regrowAt) { Sheared = false; foreach (var g in _wool) g.SetActive(true); }

            // ---- decide where to go ----
            Vector3 want = Vector3.zero;
            float speed = Def.Speed;
            bool chasing = false;
            var toPlayer = new Vector3(to.x, 0, to.z).normalized;
            if (Aggressive && !_mgr.Player.Dead && dist < (Def.Ranged ? 20f : 18f))
            {
                chasing = true;
                if (Def.Ranged)
                {
                    // Skeletons keep a few blocks away and shoot.
                    if (dist < 5f) want = -toPlayer;
                    else if (dist > 11f) want = toPlayer;
                    else want = Quaternion.Euler(0, 90, 0) * toPlayer * (Mathf.Sin(Time.time * 0.7f + GetInstanceID()) > 0 ? 0.5f : -0.5f);
                }
                else want = toPlayer;
            }
            else if (Time.time < _fleeUntil)
            {
                want = -toPlayer;
                speed *= 1.8f;
            }
            else
            {
                if (Time.time >= _wanderAt)
                {
                    _wanderAt = Time.time + UnityEngine.Random.Range(4f, 9f);
                    _wander = UnityEngine.Random.value < 0.4f ? Vector3.zero : new Vector3(UnityEngine.Random.Range(-1f, 1f), 0, UnityEngine.Random.Range(-1f, 1f)).normalized;
                }
                want = _wander;
                speed *= 0.5f;
            }

            // Creepers stop and swell when close.
            if (Def.Explodes && chasing)
            {
                if (dist < 3f) _fuse += dt;
                else if (dist > 6f) _fuse = Mathf.Max(0, _fuse - dt);
                if (_fuse > 0 && !_hissed) { _hissed = true; _mgr.Sounds.Play(Def.Fuse, transform.position, 1f); }
                if (_fuse <= 0) _hissed = false;
                if (_fuse > 0) want = Vector3.zero;
                if (_fuse >= 1.5f)
                {
                    _mgr.Explode(World, Box.center, 3f, Def.Pack);
                    Destroy(gameObject);
                    return;
                }
            }

            // Melee mobs hit you when they reach you.
            _attackCd -= dt;
            if (chasing && !Def.Explodes && !Def.Ranged && _attackCd <= 0 && dist < Def.Width * 0.5f + 1.1f && Mathf.Abs(to.y) < Def.Height * s + s)
            {
                _attackCd = 1f;
                _mgr.Player.Damage(Def.Damage, "were slain by " + Def.Name, transform.position);
            }

            // Skeletons shoot when they can see you.
            if (chasing && Def.Ranged && Time.time >= _shootAt && dist <= 16f)
            {
                _shootAt = Time.time + UnityEngine.Random.Range(1.6f, 2.6f);
                var from = Box.center + Vector3.up * Def.Height * s * 0.3f;
                var aim = gp.headCollider.transform.position - Vector3.up * 0.15f - from;
                bool blocked = World.Raycast(from, aim, aim.magnitude, out _, out _);
                if (!blocked)
                {
                    // Aim a little high: arrows drop.
                    float speedM = 1.6f * 20f * s;
                    aim += Vector3.up * aim.magnitude * aim.magnitude / (speedM * speedM) * 10f * s;
                    Arrow.Spawn(_mgr, World, from + aim.normalized * s * 0.6f, aim.normalized * speedM, this, false);
                    _mgr.Sounds.Play(Def.Shoot, from, 0.8f, UnityEngine.Random.Range(0.8f, 1.2f));
                }
            }

            // Angry endermen pop up next to you now and then.
            if (Def.Teleports && _angry && dist > 10f && UnityEngine.Random.value < dt * 0.5f) TeleportNear(target);

            // ---- move (blocks per second), with gravity, stepping up and climbing ----
            var horiz = want * speed * s;
            _vel.x = Mathf.Lerp(_vel.x, horiz.x, dt * 8f);
            _vel.z = Mathf.Lerp(_vel.z, horiz.z, dt * 8f);
            _vel.y -= 26f * s * dt;
            if (Def.SlowFall) _vel.y = Mathf.Max(_vel.y, -2.5f * s);
            var p = transform.position;
            bool hitWall = false;
            var nx = p + new Vector3(_vel.x * dt, 0, 0);
            if (Collides(nx)) { _vel.x = 0; hitWall = true; } else p = nx;
            var nz = p + new Vector3(0, 0, _vel.z * dt);
            if (Collides(nz)) { _vel.z = 0; hitWall = true; } else p = nz;
            if (hitWall && Def.Climbs && want.sqrMagnitude > 0) _vel.y = Mathf.Max(_vel.y, speed * s);
            var ny = p + new Vector3(0, _vel.y * dt, 0);
            if (Collides(ny))
            {
                if (_vel.y < 0) { _onGround = true; p.y = Mathf.Floor((p.y - World.Origin.y) / s + 0.01f) * s + World.Origin.y; }
                _vel.y = 0;
            }
            else { p = ny; _onGround = false; }
            if (hitWall && _onGround && want.sqrMagnitude > 0 && !Def.Climbs) _vel.y = 8.7f * s;
            transform.position = p;

            var inside = World.Def(World.ToBlock(p + Vector3.up * s * 0.5f));
            if (inside.Render == RenderKind.Water)
            {
                _vel.y = Mathf.Max(_vel.y, 2f * s);
                if (Def.Teleports) TeleportNear(transform.position); // endermen hate water
            }

            // ---- face and animate ----
            var face = chasing ? toPlayer : want;
            if (face.sqrMagnitude > 0.01f) _yaw = Mathf.MoveTowardsAngle(_yaw, Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg, 360f * dt);
            transform.rotation = Quaternion.Euler(0, _yaw, 0);
            float moving = new Vector2(_vel.x, _vel.z).magnitude / s;
            _walk += moving * dt * 2.2f;
            float swing = Mathf.Sin(_walk * 3f) * Mathf.Min(1f, moving / 2f) * 40f;
            if (Def.Model == "spider")
                for (int i = 0; i < _legs.Count; i++)
                    _legs[i].localRotation = Quaternion.Euler(_legBase[i] + new Vector3(0, ((i / 2) % 2 == 0 ? swing : -swing) * 0.4f, 0));
            else
                for (int i = 0; i < _legs.Count; i++) _legs[i].localRotation = Quaternion.Euler((i % 2 == 0) == (i < 2) ? swing : -swing, 0, 0);
            for (int i = 0; i < _arms.Count; i++)
            {
                bool armsOut = Def.Model == "humanoid" || (Def.Ranged && chasing);
                _arms[i].localRotation = Quaternion.Euler(armsOut ? -90f + (i == 0 ? swing : -swing) * 0.2f : (i == 0 ? -swing : swing), 0, 0);
            }
            float flap = !_onGround && Def.SlowFall ? Mathf.Sin(Time.time * 30f) * 60f : 0f;
            for (int i = 0; i < _wings.Count; i++) _wings[i].localRotation = Quaternion.Euler(0, 0, i == 0 ? -flap : flap);
            if (_head != null && chasing)
            {
                var look = Quaternion.Inverse(transform.rotation) * (gp.headCollider.transform.position - _head.position);
                if (look.sqrMagnitude > 0.0001f) _head.localRotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }
            else if (_head != null) _head.localRotation = Quaternion.Slerp(_head.localRotation, Quaternion.identity, dt * 4f);
            float swell = Def.Explodes ? 1f + Mathf.Clamp01(_fuse / 1.5f) * 0.25f : 1f;
            transform.localScale = new Vector3(swell, swell, swell);

            // ---- light, flashes, sounds ----
            _hurtFlash = Mathf.Max(0, _hurtFlash - dt * 2.5f);
            float light = _mgr.LightAt(World, Box.center);
            var tint = new Color(light, light, light);
            if (_hurtFlash > 0) tint = Color.Lerp(tint, new Color(1f, 0.25f, 0.25f), _hurtFlash);
            if (Def.Explodes && _fuse > 0 && ((int)(_fuse * 8)) % 2 == 1) tint = Color.white * 1.3f;
            Tint(tint);

            if (Time.time >= _sayAt)
            {
                _sayAt = Time.time + UnityEngine.Random.Range(6f, 14f);
                _mgr.Sounds.Play(Def.Say, transform.position, 0.8f, UnityEngine.Random.Range(0.85f, 1.15f));
            }
            if (moving > 0.4f && _onGround && Time.time >= _stepAt)
            {
                _stepAt = Time.time + 0.45f;
                _mgr.Sounds.Play(Def.Step, transform.position, 0.3f);
            }

            // Zombies and skeletons burn in sunlight.
            if (Def.BurnsInDay && _mgr.Daylight > 0.6f && World.SkyOpen(World.ToBlock(Box.center + Vector3.up * s)) >= 15)
            {
                _burnTick -= dt;
                if (_burnTick <= 0) { _burnTick = 1f; Hurt(1f, transform.position + Vector3.up, false); _mgr.Fx.Puff(Box.center + Vector3.up * s * 0.6f, s * 0.6f); }
            }
        }

        private void Tint(Color c)
        {
            Materials.SetColor(_mat, c);
            if (_mat2 != null && Def.Model != "enderman") Materials.SetColor(_mat2, c);
        }

        private bool Collides(Vector3 feet)
        {
            float s = World.S, hw = Def.Width * s * 0.5f - 0.001f, h = Def.Height * s - 0.001f;
            for (float y = 0.001f; y <= h + 0.0001f; y += Mathf.Min(s * 0.9f, h))
                for (int ix = -1; ix <= 1; ix += 2)
                    for (int iz = -1; iz <= 1; iz += 2)
                        if (World.IsSolidAt(feet + new Vector3(ix * hw, Mathf.Min(y, h), iz * hw))) return true;
            return false;
        }

        private void TeleportNear(Vector3 around)
        {
            var c = World.ToBlock(around);
            for (int tries = 0; tries < 12; tries++)
            {
                int x = c.x + UnityEngine.Random.Range(-12, 13), z = c.z + UnityEngine.Random.Range(-12, 13);
                int top = World.HeightAt(x, z);
                if (top < 1) continue;
                var feet = new Vector3Int(x, top + 1, z);
                bool free = true;
                for (int dy = 0; dy < Mathf.CeilToInt(Def.Height) && free; dy++)
                    if (BlockRegistry.SolidT[World.Get(feet + Vector3Int.up * dy)] || World.Def(feet + Vector3Int.up * dy).Liquid) free = false;
                if (!free || World.Def(feet + Vector3Int.down).Liquid) continue;
                _mgr.Fx.Puff(Box.center, World.S);
                _mgr.Sounds.Play(Def.Teleport, transform.position, 0.8f);
                transform.position = World.Corner(feet) + new Vector3(World.S / 2, 0, World.S / 2);
                _vel = Vector3.zero;
                _mgr.Sounds.Play(Def.Teleport, transform.position, 0.8f);
                return;
            }
        }

        /// <summary>Shears: sheep drop wool and look bare until it grows back.</summary>
        public bool Shear()
        {
            if (!Def.Shearable || Sheared || Dying) return false;
            Sheared = true;
            _regrowAt = Time.time + 90f;
            foreach (var g in _wool) g.SetActive(false);
            var wool = Items.Local(Def.Pack, "white_wool");
            if (wool != null) ItemEntity.Spawn(_mgr, World, Box.center, new ItemStack(wool, UnityEngine.Random.Range(1, 4)), Vector3.up * 2f);
            _mgr.Sounds.PlayEvent(Def.Pack, "shear", transform.position, 0.8f);
            return true;
        }

        public void Hurt(float dmg, Vector3 from, bool byPlayer = true)
        {
            if (_deadAt >= 0) return;
            Hp -= dmg;
            _hurtFlash = 1f;
            var away = transform.position - from;
            away.y = 0;
            _vel += away.normalized * 7f * World.S + Vector3.up * 5f * World.S;
            if (!Def.Hostile) _fleeUntil = Time.time + 4f;
            if (byPlayer && Def.Neutral) _angry = true;
            _mgr.Sounds.Play(Def.Hurt, transform.position, 0.9f, UnityEngine.Random.Range(0.85f, 1.15f));
            if (Def.Teleports && Hp > 0 && UnityEngine.Random.value < 0.7f) TeleportNear(transform.position);
            if (Hp > 0) return;
            _deadAt = Time.time;
            _mgr.Sounds.Play(Def.Death, transform.position, 0.9f);
            if (Def.Drops != null && byPlayer)
                foreach (var e in Def.Drops)
                {
                    string id = (string)e["item"];
                    if (Sheared && id == "white_wool") continue;
                    var item = Items.Local(Def.Pack, id);
                    if (item == null || UnityEngine.Random.value > ((float?)e["chance"] ?? 1f)) continue;
                    int n = UnityEngine.Random.Range((int?)e["min"] ?? 1, ((int?)e["max"] ?? 1) + 1);
                    if (n > 0) ItemEntity.Spawn(_mgr, World, Box.center, new ItemStack(item, n), Vector3.up * 2f);
                }
        }
    }

    /// <summary>Spawns mobs around you: hostile ones in the dark (night or caves), animals on grass in the day.</summary>
    public class MobSpawner
    {
        private float _next;

        private static MobDef Pick(List<MobDef> list)
        {
            int total = 0;
            foreach (var d in list) total += Mathf.Max(1, d.Weight);
            int r = UnityEngine.Random.Range(0, total);
            foreach (var d in list) { r -= Mathf.Max(1, d.Weight); if (r < 0) return d; }
            return list[0];
        }

        public void Tick(MergeManager mgr, VoxelWorld w, List<MobDef> defs, float daylight)
        {
            if (defs == null || defs.Count == 0 || Time.time < _next) return;
            _next = Time.time + 1f;
            int hostile = 0, passive = 0;
            foreach (var m in Mob.Live) { if (m.Def.Hostile) hostile++; else passive++; }

            var head = GTPlayer.Instance.headCollider.transform.position;
            var c = w.ToBlock(head);
            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f), r = UnityEngine.Random.Range(16f, 34f);
            int x = c.x + Mathf.RoundToInt(Mathf.Cos(ang) * r), z = c.z + Mathf.RoundToInt(Mathf.Sin(ang) * r);
            int top = w.HeightAt(x, z);
            if (top < 0) return;

            bool night = daylight < 0.35f;
            var hostiles = defs.FindAll(d => d.Hostile);
            var passives = defs.FindAll(d => !d.Hostile);
            int cap = Plugin.Instance.MobCap.Value;
            if (hostile < cap && hostiles.Count > 0)
            {
                // On the surface at night, or in caves any time.
                int y = -1;
                if (night && UnityEngine.Random.value < 0.7f) y = top + 1;
                else
                {
                    int tryY = UnityEngine.Random.Range(6, Mathf.Max(7, top - 8));
                    if (w.Get(x, tryY, z) == 0 && w.Get(x, tryY + 1, z) == 0 && BlockRegistry.SolidT[w.Get(x, tryY - 1, z)]) y = tryY;
                }
                var def = Pick(hostiles);
                if (y > 0 && Free(w, x, y, z, def) && !NearLight(w, x, y, z))
                {
                    Mob.Spawn(mgr, w, def, w.Corner(new Vector3Int(x, y, z)) + new Vector3(w.S / 2, 0, w.S / 2));
                    return;
                }
            }
            if (!night && passive < Mathf.Max(4, cap) && passives.Count > 0 && UnityEngine.Random.value < 0.3f)
            {
                var ground = w.Def(new Vector3Int(x, top, z));
                var def = Pick(passives);
                if (ground.LocalId != "grass_block" || !Free(w, x, top + 1, z, def)) return;
                for (int i = 0; i < UnityEngine.Random.Range(2, 5); i++)
                    Mob.Spawn(mgr, w, def, w.Corner(new Vector3Int(x, top + 1, z)) + new Vector3(w.S / 2 + i * 0.12f, 0, w.S / 2 - i * 0.08f));
            }
        }

        private static bool Free(VoxelWorld w, int x, int y, int z, MobDef def)
        {
            if (!w.IsLoaded(new Vector3Int(x, y, z))) return false;
            for (int dy = 0; dy < Mathf.Max(1, Mathf.CeilToInt(def.Height)); dy++)
            {
                var p = new Vector3Int(x, y + dy, z);
                if (BlockRegistry.SolidT[w.Get(p)] || w.Def(p).Liquid) return false;
            }
            return true;
        }

        // No spawning right next to torches.
        private static bool NearLight(VoxelWorld w, int x, int y, int z)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dz = -6; dz <= 6; dz++)
                    for (int dx = -6; dx <= 6; dx++)
                        if (BlockRegistry.EmitT[w.Get(x + dx, y + dy, z + dz)] > 0) return true;
            return false;
        }
    }
}
