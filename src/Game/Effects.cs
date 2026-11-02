using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace GorillaMerge
{
    /// <summary>Loads pack sounds in the background and plays them in 3D.</summary>
    public class SoundBank : MonoBehaviour
    {
        private readonly Dictionary<string, List<AudioClip>> _clips = new Dictionary<string, List<AudioClip>>();
        private readonly HashSet<string> _requested = new HashSet<string>();
        private AudioSource[] _pool;
        private int _next;
        private AudioSource _music;
        private float _nextSongAt;
        private bool _songLoading;

        private void Awake()
        {
            _pool = new AudioSource[16];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject("GM_sound" + i);
                go.transform.SetParent(transform, false);
                var a = go.AddComponent<AudioSource>();
                a.playOnAwake = false;
                a.spatialBlend = 1f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.minDistance = 1f;
                a.maxDistance = 20f;
                a.dopplerLevel = 0f;
                _pool[i] = a;
            }
            _music = gameObject.AddComponent<AudioSource>();
            _music.playOnAwake = false;
            _music.spatialBlend = 0f;
            _music.volume = 0.35f;
            _nextSongAt = Time.time + UnityEngine.Random.Range(15f, 40f);
        }

        public void Preload(string spec, string packDir)
        {
            if (string.IsNullOrEmpty(spec) || !_requested.Add(spec)) return;
            _clips[spec] = new List<AudioClip>();
            StartCoroutine(LoadAll(spec, GameAssets.SoundFiles(spec, packDir)));
        }

        private IEnumerator LoadAll(string spec, List<string> files)
        {
            foreach (var file in files)
            {
                AudioClip clip = null;
                yield return Load(file, false, c => clip = c);
                if (clip != null) _clips[spec].Add(clip);
            }
        }

        private static IEnumerator Load(string file, bool stream, Action<AudioClip> done)
        {
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, GameAssets.AudioTypeFor(file)))
            {
                if (stream && req.downloadHandler is DownloadHandlerAudioClip dh) dh.streamAudio = true;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip != null) clip.name = System.IO.Path.GetFileName(file);
                    done(clip);
                }
                else
                {
                    Plugin.Log.LogWarning($"Sound {file}: {req.error}");
                    done(null);
                }
            }
        }

        public void Play(string spec, Vector3 pos, float volume, float pitch = 1f)
        {
            if (string.IsNullOrEmpty(spec) || !_clips.TryGetValue(spec, out var list) || list.Count == 0) return;
            var a = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            a.transform.position = pos;
            a.clip = list[UnityEngine.Random.Range(0, list.Count)];
            a.volume = Mathf.Clamp01(volume * Plugin.Instance.SoundVolume.Value);
            a.pitch = pitch;
            a.Play();
        }

        public void PlayBlock(BlockDef def, string kind, Vector3 pos, float volume = 1f)
        {
            if (def?.Pack == null || def.Sound == null || !def.Pack.SoundGroups.TryGetValue(def.Sound, out var set)) return;
            string spec = kind == "break" ? set.Break : kind == "place" ? set.Place : set.Step;
            // Minecraft plays dig sounds a bit low and step sounds at normal pitch.
            Play(spec, pos, volume, kind == "step" ? 1f : 0.8f + UnityEngine.Random.Range(-0.05f, 0.05f));
        }

        public void PlayEvent(Pack pack, string evt, Vector3 pos, float volume = 1f, float pitch = 1f)
        {
            if (pack != null && pack.Events.TryGetValue(evt, out var spec)) Play(spec, pos, volume, pitch);
        }

        public void PreloadPack(Pack pack)
        {
            foreach (var s in pack.SoundGroups.Values)
            {
                Preload(s.Break, pack.Dir);
                Preload(s.Place, pack.Dir);
                Preload(s.Step, pack.Dir);
            }
            foreach (var e in pack.Events.Values) Preload(e, pack.Dir);
        }

        /// <summary>Every few minutes plays one of the pack's songs, like Minecraft does.</summary>
        public void UpdateMusic(Pack pack)
        {
            bool want = pack != null && !string.IsNullOrEmpty(pack.MusicSpec) && Plugin.Instance.Music.Value;
            if (!want)
            {
                if (_music.isPlaying) _music.Stop();
                return;
            }
            if (_music.isPlaying)
            {
                _music.volume = 0.35f * Plugin.Instance.SoundVolume.Value;
                _nextSongAt = Time.time + UnityEngine.Random.Range(90f, 240f);
                return;
            }
            if (_songLoading || Time.time < _nextSongAt) return;
            var tracks = GameAssets.SoundFiles(pack.MusicSpec, pack.Dir);
            if (tracks.Count == 0) { _nextSongAt = Time.time + 600f; return; }
            _songLoading = true;
            StartCoroutine(Load(tracks[UnityEngine.Random.Range(0, tracks.Count)], true, clip =>
            {
                _songLoading = false;
                if (clip == null) { _nextSongAt = Time.time + 300f; return; }
                if (_music.clip != null) Destroy(_music.clip);
                _music.clip = clip;
                _music.volume = 0.35f * Plugin.Instance.SoundVolume.Value;
                _music.Play();
            }));
        }

        public void StopMusic()
        {
            if (_music.isPlaying) _music.Stop();
            _nextSongAt = Time.time + UnityEngine.Random.Range(15f, 40f);
        }
    }

    /// <summary>Little cubes that fly out when a block breaks.</summary>
    public class Particles : MonoBehaviour
    {
        private class P
        {
            public GameObject Go;
            public Mesh Mesh;
            public Vector3 Vel;
            public float Life, MaxLife, Size, Gravity = 1f;
        }

        private readonly List<P> _live = new List<P>();
        private readonly Stack<P> _free = new Stack<P>();

        public void Burst(BlockDef def, Vector3 center, float blockSize, int count)
        {
            if (def == null || BlockRegistry.Atlas == null) return;
            for (int i = 0; i < count; i++)
            {
                var p = _free.Count > 0 ? _free.Pop() : Create();
                float sx = UnityEngine.Random.Range(0, 3) / 4f, sy = UnityEngine.Random.Range(0, 3) / 4f;
                var m = MeshUtil.BlockCube(def, 1f, new Rect(sx, sy, 0.25f, 0.25f));
                p.Mesh.Clear();
                p.Mesh.SetVertices(new List<Vector3>(m.vertices));
                p.Mesh.SetUVs(0, new List<Vector2>(m.uv));
                p.Mesh.SetColors(new List<Color32>(m.colors32));
                p.Mesh.SetTriangles(m.triangles, 0);
                p.Go.GetComponent<MeshRenderer>().sharedMaterial = BlockRegistry.OpaqueMat;
                p.Gravity = 1f;
                p.Mesh.RecalculateBounds();
                Destroy(m);
                p.Go.SetActive(true);
                var offset = new Vector3(UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f) * blockSize * 0.8f;
                p.Go.transform.position = center + offset;
                p.Go.transform.rotation = UnityEngine.Random.rotation;
                p.Size = blockSize * UnityEngine.Random.Range(0.1f, 0.16f);
                p.Go.transform.localScale = Vector3.one * p.Size;
                p.Vel = offset.normalized * UnityEngine.Random.Range(0.6f, 1.6f) + Vector3.up * UnityEngine.Random.Range(0.8f, 2f);
                p.MaxLife = p.Life = UnityEngine.Random.Range(0.45f, 0.9f);
                _live.Add(p);
            }
        }

        private Material _itemMat, _smokeMat;

        /// <summary>Crumbs of an item (eating).</summary>
        public void BurstItem(ItemDef d, Vector3 center, int count)
        {
            if (d == null) return;
            if (d.Block != null) { Burst(d.Block, center, 0.25f, count); return; }
            if (_itemMat == null) _itemMat = Materials.VertexLit(Items.Atlas);
            for (int i = 0; i < count; i++)
            {
                var p = _free.Count > 0 ? _free.Pop() : Create();
                var r = d.AtlasUV;
                float sx = UnityEngine.Random.Range(0, 12) / 16f, sy = UnityEngine.Random.Range(0, 12) / 16f;
                float w = r.z - r.x, h = r.w - r.y;
                var sub = new Vector4(r.x + sx * w, r.y + sy * h, r.x + (sx + 0.25f) * w, r.y + (sy + 0.25f) * h);
                var uvs = new List<Vector2>();
                for (int f = 0; f < 6; f++) { uvs.Add(new Vector2(sub.x, sub.y)); uvs.Add(new Vector2(sub.x, sub.w)); uvs.Add(new Vector2(sub.z, sub.w)); uvs.Add(new Vector2(sub.z, sub.y)); }
                Unit(p.Mesh);
                p.Mesh.SetUVs(0, uvs);
                p.Go.GetComponent<MeshRenderer>().sharedMaterial = _itemMat;
                Launch(p, center, 0.25f, 0.6f, 0.035f);
            }
        }

        /// <summary>A puff of smoke (explosions, mobs dying or burning).</summary>
        public void Puff(Vector3 center, float size)
        {
            if (_smokeMat == null) _smokeMat = Materials.VertexLit(Texture2D.whiteTexture);
            for (int i = 0; i < 10; i++)
            {
                var p = _free.Count > 0 ? _free.Pop() : Create();
                Unit(p.Mesh);
                var uvs = new List<Vector2>();
                for (int k = 0; k < 24; k++) uvs.Add(new Vector2(0.5f, 0.5f));
                p.Mesh.SetUVs(0, uvs);
                p.Go.GetComponent<MeshRenderer>().sharedMaterial = _smokeMat;
                Launch(p, center, size, 1.2f, size * 0.25f);
                p.Vel = UnityEngine.Random.insideUnitSphere * size + Vector3.up * size * 1.5f;
                p.Gravity = 0f;
            }
        }

        private static void Unit(Mesh m)
        {
            var cube = MeshUtil.UnitCube(1f);
            m.Clear();
            m.SetVertices(new List<Vector3>(cube.vertices));
            var c = new List<Color32>();
            for (int k = 0; k < 24; k++) { byte g = (byte)(k < 8 ? 160 : k < 12 ? 230 : 190); c.Add(new Color32(g, g, g, 255)); }
            m.SetColors(c);
            m.SetTriangles(cube.triangles, 0);
            m.RecalculateBounds();
            UnityEngine.Object.Destroy(cube);
        }

        private void Launch(P p, Vector3 center, float spread, float life, float size)
        {
            p.Go.SetActive(true);
            var offset = UnityEngine.Random.insideUnitSphere * spread * 0.4f;
            p.Go.transform.position = center + offset;
            p.Go.transform.rotation = UnityEngine.Random.rotation;
            p.Size = size;
            p.Go.transform.localScale = Vector3.one * size;
            p.Vel = offset.normalized * UnityEngine.Random.Range(0.3f, 1f) + Vector3.up * UnityEngine.Random.Range(0.5f, 1.2f);
            p.Gravity = 1f;
            p.MaxLife = p.Life = UnityEngine.Random.Range(life * 0.6f, life);
            _live.Add(p);
        }

        private P Create()
        {
            var mesh = new Mesh { name = "GM_particle" };
            mesh.MarkDynamic();
            var go = MeshUtil.Object("GM_particle", mesh, BlockRegistry.OpaqueMat, transform);
            return new P { Go = go, Mesh = mesh };
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var p = _live[i];
                p.Life -= dt;
                if (p.Life <= 0f)
                {
                    p.Go.SetActive(false);
                    _live.RemoveAt(i);
                    _free.Push(p);
                    continue;
                }
                p.Vel += Physics.gravity * dt * p.Gravity;
                p.Go.transform.position += p.Vel * dt;
                p.Go.transform.localScale = Vector3.one * (p.Size * Mathf.Clamp01(p.Life / p.MaxLife * 2f));
            }
        }
    }

    /// <summary>The crack picture drawn over a block while you mine it.</summary>
    public class Cracks
    {
        private readonly GameObject _go;
        private readonly Material _mat;
        private readonly Texture2D[] _stages = new Texture2D[10];

        public Cracks(Transform parent)
        {
            for (int i = 0; i < 10; i++)
            {
                var t = GameAssets.LoadTexture("mc:block/destroy_stage_" + i, null);
                _stages[i] = t != null ? DarkCrack(t) : MakeCrack(i);
            }
            _mat = Materials.Transparent(_stages[0], Color.white);
            _go = MeshUtil.Object("GM_cracks", MeshUtil.UnitCube(1.004f), _mat, parent);
            _go.SetActive(false);
        }

        // Minecraft multiplies its cracks onto the block; with normal blending, black cracks look the same.
        private static Texture2D DarkCrack(Texture2D t)
        {
            var px = t.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float lum = (c.r + c.g + c.b) / (3f * 255f);
                px[i] = new Color32(0, 0, 0, (byte)Mathf.Clamp(c.a * (1f - lum) * 1.6f, 0, 230));
            }
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        private static Texture2D MakeCrack(int stage)
        {
            var t = new Texture2D(16, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var rng = new System.Random(77);
            var px = new Color32[256];
            for (int i = 0; i < 256; i++)
                px[i] = rng.NextDouble() < (stage + 1) / 14.0 ? new Color32(0, 0, 0, 200) : new Color32(0, 0, 0, 0);
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        public void Show(VoxelWorld w, Vector3Int block, float progress)
        {
            int stage = Mathf.Clamp((int)(progress * 10f), 0, 9);
            _mat.SetTexture("_BaseMap", _stages[stage]);
            _mat.mainTexture = _stages[stage];
            _go.transform.position = w.Center(block);
            _go.transform.localScale = Vector3.one * w.S;
            _go.SetActive(true);
        }

        public void Hide() { if (_go.activeSelf) _go.SetActive(false); }
    }

    /// <summary>Lit TNT: falls, flashes, then explodes.</summary>
    public class PrimedTnt : MonoBehaviour
    {
        public VoxelWorld World;
        public BlockDef Def;
        public float Fuse = 2.5f;
        private Vector3 _vel;
        private GameObject _flash;
        private float _t;

        public static PrimedTnt Spawn(VoxelWorld w, BlockDef def, Vector3Int block, float fuse)
        {
            var go = MeshUtil.Object("GM_tnt", MeshUtil.BlockCube(def, w.S), BlockRegistry.OpaqueMat);
            go.transform.position = w.Center(block);
            var tnt = go.AddComponent<PrimedTnt>();
            tnt.World = w;
            tnt.Def = def;
            tnt.Fuse = fuse;
            tnt._vel = new Vector3(UnityEngine.Random.Range(-0.3f, 0.3f), 1.2f, UnityEngine.Random.Range(-0.3f, 0.3f)) * w.S * 2f;
            tnt._flash = MeshUtil.Object("flash", MeshUtil.UnitCube(w.S * 1.01f), Materials.Solid(new Color(1f, 1f, 1f, 0.6f)), go.transform);
            return tnt;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _t += dt;
            _vel += Physics.gravity * dt;
            var next = transform.position + _vel * dt;
            float half = World.S * 0.5f;
            if (_vel.y < 0 && World.IsSolidAt(next - new Vector3(0, half, 0)))
            {
                _vel = Vector3.zero;
                next = transform.position;
            }
            transform.position = next;
            _flash.SetActive(((int)(_t * 4f)) % 2 == 1);
            if (_t >= Fuse)
            {
                MergeManager.Instance?.Explode(World, transform.position, 3.5f, Def.Pack);
                Destroy(gameObject);
            }
        }
    }
}
