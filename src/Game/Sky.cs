using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaMerge
{
    /// <summary>
    /// Minecraft's sky: a colour dome that goes blue / orange at sunset / dark at night, the sun and the moon
    /// (with phases) moving east to west, stars at night, and Minecraft's chunky 3D clouds drifting along.
    /// </summary>
    public class Sky
    {
        private readonly Pack _pack;
        private readonly GameObject _root, _celestial, _sun, _moon, _stars, _clouds;
        private readonly Mesh _dome, _cloudMesh;
        private readonly Material _starMat, _cloudMat, _moonMat;
        private readonly Texture2D[] _moonPhases = new Texture2D[8];
        private readonly Vector3[] _domeDirs;
        private readonly Color32[] _domeColors;
        private readonly bool[] _cloudPx;
        private readonly int _cloudW, _cloudH;
        private readonly float _cloudY, _cell;
        private float _cloudShift, _nextDome;
        private Vector2Int _cloudBuiltAt = new Vector2Int(int.MinValue, 0);
        private float _cloudBuiltShift;
        private int _moonPhase = -1;
        private readonly float _radius;

        public float Daylight { get; private set; } = 1f;
        public Color FogColor { get; private set; }

        private static readonly string[] MoonNames =
            { "full_moon", "waning_gibbous", "third_quarter", "waning_crescent", "new_moon", "waxing_crescent", "first_quarter", "waxing_gibbous" };

        public Sky(Pack pack, float cloudHeight, float blockSize)
        {
            _pack = pack;
            _root = new GameObject("GM_Sky_" + pack.Id);
            Object.DontDestroyOnLoad(_root);

            float far = 1000f;
            try { if (Camera.main != null) far = Camera.main.farClipPlane; } catch { }
            _radius = Mathf.Clamp(far * 0.85f, 60f, 450f);

            // Dome: a sphere whose vertex colours are repainted as the day goes by.
            _dome = new Mesh { name = "GM_dome" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            const int rings = 16, segs = 32;
            for (int r = 0; r <= rings; r++)
            {
                float lat = Mathf.Lerp(-90f, 90f, r / (float)rings) * Mathf.Deg2Rad;
                for (int s = 0; s <= segs; s++)
                {
                    float lon = s / (float)segs * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon)));
                }
            }
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segs; s++)
                {
                    int a = r * (segs + 1) + s, b = a + segs + 1;
                    tris.Add(a); tris.Add(a + 1); tris.Add(b);
                    tris.Add(b); tris.Add(a + 1); tris.Add(b + 1);
                }
            _domeDirs = verts.ToArray();
            for (int i = 0; i < verts.Count; i++) verts[i] *= _radius;
            _dome.SetVertices(verts);
            _dome.SetTriangles(tris, 0);
            _dome.RecalculateBounds();
            _domeColors = new Color32[verts.Count];
            var domeMat = Materials.VertexLit(Texture2D.whiteTexture);
            if (domeMat.HasProperty("_Cull")) domeMat.SetFloat("_Cull", 0f);
            domeMat.renderQueue = 1000;
            MeshUtil.Object("dome", _dome, domeMat, _root.transform);

            // Sun, moon and stars ride on one turning frame.
            _celestial = new GameObject("celestial");
            _celestial.transform.SetParent(_root.transform, false);
            var sunTex = Additive(GameAssets.LoadTexture(pack.Sun, pack.Dir));
            if (sunTex != null) _sun = Disc("sun", sunTex, Vector3.right, 0.16f, out _);
            bool anyMoon = false;
            for (int i = 0; i < 8; i++)
            {
                _moonPhases[i] = Additive(GameAssets.LoadTexture("mc:environment/celestial/moon/" + MoonNames[i], pack.Dir));
                anyMoon |= _moonPhases[i] != null;
            }
            if (anyMoon && pack.Sun != null && pack.Sun.StartsWith("mc:")) _moon = Disc("moon", _moonPhases[0] ?? Texture2D.whiteTexture, Vector3.left, 0.1f, out _moonMat);

            _starMat = Materials.VertexLitTransparent(Texture2D.whiteTexture, new Color(1, 1, 1, 0));
            _starMat.renderQueue = 1001;
            _stars = MeshUtil.Object("stars", StarMesh(_radius * 0.95f), _starMat, _celestial.transform);

            // Clouds: each pixel of Minecraft's clouds picture is a 12 x 4 x 12 block puff.
            var clouds = GameAssets.LoadTexture(pack.Clouds, pack.Dir, false);
            if (clouds != null)
            {
                _cloudW = clouds.width;
                _cloudH = clouds.height;
                var px = clouds.GetPixels32();
                _cloudPx = new bool[px.Length];
                for (int i = 0; i < px.Length; i++) _cloudPx[i] = px[i].a > 127 && px[i].r > 127;
                Object.Destroy(clouds);
                _cell = 12f * blockSize;
                _cloudY = cloudHeight;
                _cloudMesh = new Mesh { name = "GM_clouds", indexFormat = IndexFormat.UInt32 };
                _cloudMat = Materials.VertexLitTransparent(Texture2D.whiteTexture, new Color(1, 1, 1, 0.8f));
                if (_cloudMat.HasProperty("_Cull")) _cloudMat.SetFloat("_Cull", 0f);
                _clouds = MeshUtil.Object("GM_Clouds_" + pack.Id, _cloudMesh, _cloudMat);
                Object.DontDestroyOnLoad(_clouds);
            }
        }

        // Minecraft draws the sun and moon by adding light; turn their black backgrounds see-through.
        private static Texture2D Additive(Texture2D t)
        {
            if (t == null) return null;
            var px = t.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                byte a = (byte)Mathf.Min(c.a, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
                px[i] = new Color32(c.r, c.g, c.b, a);
            }
            t.SetPixels32(px);
            t.Apply(false);
            t.filterMode = FilterMode.Point;
            return t;
        }

        private GameObject Disc(string name, Texture2D tex, Vector3 dir, float size, out Material mat)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(quad.GetComponent<Collider>());
            quad.name = name;
            quad.transform.SetParent(_celestial.transform, false);
            quad.transform.localPosition = dir * _radius * 0.9f;
            quad.transform.localRotation = Quaternion.LookRotation(dir);
            quad.transform.localScale = Vector3.one * _radius * size;
            mat = Materials.Transparent(tex, Color.white);
            mat.renderQueue = 1002;
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return quad;
        }

        private static Mesh StarMesh(float r)
        {
            var rng = new System.Random(10842);
            var v = new List<Vector3>();
            var c = new List<Color32>();
            var t = new List<int>();
            for (int i = 0; i < 1500; i++)
            {
                var d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
                if (d.sqrMagnitude > 1 || d.sqrMagnitude < 0.01f) continue;
                d.Normalize();
                float s = r * (0.0025f + (float)rng.NextDouble() * 0.002f);
                var up = Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.forward;
                var a = Vector3.Cross(d, up).normalized * s;
                var b = Vector3.Cross(d, a).normalized * s;
                var p = d * r;
                int start = v.Count;
                v.Add(p - a - b); v.Add(p - a + b); v.Add(p + a + b); v.Add(p + a - b);
                byte bright = (byte)(150 + rng.Next(105));
                for (int k = 0; k < 4; k++) c.Add(new Color32(bright, bright, bright, 255));
                t.Add(start); t.Add(start + 1); t.Add(start + 2); t.Add(start); t.Add(start + 2); t.Add(start + 3);
                t.Add(start); t.Add(start + 2); t.Add(start + 1); t.Add(start); t.Add(start + 3); t.Add(start + 2);
            }
            var m = new Mesh { name = "GM_stars" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>time: 0 sunrise, 0.25 noon, 0.5 sunset, 0.75 midnight. day: how many days have passed (moon phase).</summary>
        public void Tick(Vector3 viewer, float time, int day)
        {
            _root.transform.position = viewer;
            float angle = time * 360f;
            _celestial.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            var sunDir = _celestial.transform.rotation * Vector3.right;

            // Minecraft-like brightness: full day once the sun is a little above the horizon.
            Daylight = Mathf.Clamp01(sunDir.y * 2.5f + 0.5f);
            if (_starMat != null) Materials.SetColor(_starMat, new Color(1, 1, 1, Mathf.Clamp01(1f - Daylight * 1.6f)));

            if (_moon != null)
            {
                int phase = ((day % 8) + 8) % 8;
                if (phase != _moonPhase && _moonPhases[phase] != null)
                {
                    _moonPhase = phase;
                    _moonMat.mainTexture = _moonPhases[phase];
                    if (_moonMat.HasProperty("_BaseMap")) _moonMat.SetTexture("_BaseMap", _moonPhases[phase]);
                }
            }

            if (Time.time >= _nextDome)
            {
                _nextDome = Time.time + 0.25f;
                PaintDome(sunDir);
            }
            if (_clouds != null) TickClouds(viewer);
        }

        private void PaintDome(Vector3 sunDir)
        {
            Color dayTop = _pack.Sky, dayHorizon = _pack.Fog;
            Color nightTop = new Color(0.005f, 0.008f, 0.02f), nightHorizon = new Color(0.03f, 0.04f, 0.08f);
            Color sunset = new Color(1f, 0.45f, 0.15f);
            float dusk = Mathf.Clamp01(1f - Mathf.Abs(sunDir.y) * 3.5f); // strongest when the sun is on the horizon
            for (int i = 0; i < _domeDirs.Length; i++)
            {
                var d = _domeDirs[i];
                float up = Mathf.Clamp01(d.y);
                var top = Color.Lerp(nightTop, dayTop, Daylight);
                var hor = Color.Lerp(nightHorizon, dayHorizon, Daylight);
                var c = Color.Lerp(hor, top, Mathf.Pow(up, 0.5f));
                if (d.y < 0) c = Color.Lerp(hor, hor * 0.6f, -d.y);
                // Orange glow on the side of the sky where the sun is setting or rising.
                float toward = Mathf.Clamp01(Vector3.Dot(new Vector3(d.x, 0, d.z).normalized, new Vector3(sunDir.x, 0, sunDir.z).normalized));
                float glow = dusk * Mathf.Pow(toward, 3f) * Mathf.Clamp01(1f - Mathf.Abs(d.y) * 2.2f);
                c = Color.Lerp(c, sunset, glow * 0.85f);
                _domeColors[i] = c;
            }
            _dome.colors32 = _domeColors;
            FogColor = Color.Lerp(nightHorizon, dayHorizon, Daylight);
            if (_cloudMat != null)
            {
                var cc = Color.Lerp(new Color(0.08f, 0.08f, 0.12f), Color.white, Daylight);
                cc = Color.Lerp(cc, new Color(1f, 0.7f, 0.55f), dusk * 0.4f * Daylight);
                cc.a = 0.8f;
                Materials.SetColor(_cloudMat, cc);
            }
        }

        private void TickClouds(Vector3 viewer)
        {
            // Drift slowly (about a block every two seconds), like Minecraft.
            _cloudShift += Time.deltaTime * 0.5f * (_cell / 12f);
            var local = viewer + new Vector3(_cloudShift, 0, 0);
            var cell = new Vector2Int(Mathf.FloorToInt(local.x / _cell), Mathf.FloorToInt(local.z / _cell));
            if (Mathf.Abs(cell.x - _cloudBuiltAt.x) > 6 || Mathf.Abs(cell.y - _cloudBuiltAt.y) > 6) BuildClouds(cell);
            _clouds.transform.position = new Vector3(-(_cloudShift - _cloudBuiltShift), _cloudY, 0f);
        }

        private void BuildClouds(Vector2Int center)
        {
            _cloudBuiltAt = center;
            _cloudBuiltShift = _cloudShift;
            const int R = 26;
            var v = new List<Vector3>();
            var c = new List<Color32>();
            var t = new List<int>();
            float h = _cell / 3f;
            bool On(int x, int z) => _cloudPx[((z % _cloudH + _cloudH) % _cloudH) * _cloudW + ((x % _cloudW + _cloudW) % _cloudW)];
            void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, byte shade)
            {
                int s = v.Count;
                v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) c.Add(new Color32(shade, shade, shade, 255));
                t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
            }
            for (int z = center.y - R; z <= center.y + R; z++)
                for (int x = center.x - R; x <= center.x + R; x++)
                {
                    if ((x - center.x) * (x - center.x) + (z - center.y) * (z - center.y) > R * R || !On(x, z)) continue;
                    float x0 = x * _cell + _cloudBuiltShift, x1 = x0 + _cell, z0 = z * _cell, z1 = z0 + _cell;
                    Quad(new Vector3(x0, h, z0), new Vector3(x0, h, z1), new Vector3(x1, h, z1), new Vector3(x1, h, z0), 255);
                    Quad(new Vector3(x0, 0, z1), new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), 180);
                    if (!On(x + 1, z)) Quad(new Vector3(x1, 0, z0), new Vector3(x1, h, z0), new Vector3(x1, h, z1), new Vector3(x1, 0, z1), 230);
                    if (!On(x - 1, z)) Quad(new Vector3(x0, 0, z1), new Vector3(x0, h, z1), new Vector3(x0, h, z0), new Vector3(x0, 0, z0), 230);
                    if (!On(x, z + 1)) Quad(new Vector3(x1, 0, z1), new Vector3(x1, h, z1), new Vector3(x0, h, z1), new Vector3(x0, 0, z1), 215);
                    if (!On(x, z - 1)) Quad(new Vector3(x0, 0, z0), new Vector3(x0, h, z0), new Vector3(x1, h, z0), new Vector3(x1, 0, z0), 215);
                }
            _cloudMesh.Clear();
            _cloudMesh.SetVertices(v);
            _cloudMesh.SetColors(c);
            _cloudMesh.SetTriangles(t, 0);
            _cloudMesh.RecalculateBounds();
        }

        public void SetVisible(bool on)
        {
            if (_root != null && _root.activeSelf != on) _root.SetActive(on);
            if (_clouds != null && _clouds.activeSelf != on) _clouds.SetActive(on);
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            if (_clouds != null) Object.Destroy(_clouds);
            if (_cloudMesh != null) Object.Destroy(_cloudMesh);
        }
    }
}
