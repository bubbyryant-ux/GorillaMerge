using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaMerge
{
    /// <summary>
    /// Materials that render in Gorilla Tag's URP. Everything is unlit: the voxel atlas already has
    /// its shading baked in, and unlit shaders look the same wherever the world is placed.
    /// </summary>
    public static class Materials
    {
        private static Shader _unlit, _vertexColor;
        private static bool _searched;

        private static void Find()
        {
            if (_searched) return;
            _searched = true;
            _unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
            // Particles/Unlit multiplies by vertex colour, which lets imported maps have simple baked lighting.
            _vertexColor = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            Plugin.Log.LogInfo($"Shaders: unlit={(_unlit != null ? _unlit.name : "none")}, vertexColor={(_vertexColor != null ? _vertexColor.name : "none")}");
        }

        public static bool HasVertexColorShader { get { Find(); return _vertexColor != null; } }

        public static Material Opaque(Texture tex)
        {
            Find();
            var m = new Material(_unlit) { name = "GM_Opaque" };
            SetTexture(m, tex);
            SetColor(m, Color.white);
            return m;
        }

        public static Material Transparent(Texture tex, Color color)
        {
            Find();
            var m = new Material(_unlit) { name = "GM_Transparent" };
            SetTexture(m, tex);
            SetColor(m, color);
            MakeTransparent(m);
            return m;
        }

        public static Material Solid(Color color)
        {
            Find();
            var m = new Material(_unlit) { name = "GM_Color" };
            SetTexture(m, Texture2D.whiteTexture);
            SetColor(m, color);
            if (color.a < 0.999f) MakeTransparent(m);
            return m;
        }

        /// <summary>Textured material that also multiplies by vertex colour (falls back to plain unlit).</summary>
        public static Material VertexLit(Texture tex)
        {
            Find();
            if (_vertexColor == null) return tex != null ? Opaque(tex) : Solid(Color.white);
            var m = new Material(_vertexColor) { name = "GM_VertexLit" };
            SetTexture(m, tex != null ? tex : Texture2D.whiteTexture);
            SetColor(m, Color.white);
            return m;
        }

        public static Material VertexLitTransparent(Texture tex, Color color)
        {
            Find();
            if (_vertexColor == null) return Transparent(tex, color);
            var m = new Material(_vertexColor) { name = "GM_VertexLitT" };
            SetTexture(m, tex != null ? tex : Texture2D.whiteTexture);
            SetColor(m, color);
            MakeTransparent(m);
            return m;
        }

        private static Shader _ui;

        /// <summary>
        /// For the HUD and menus: vertex colours, see-through, and drawn on top of everything so
        /// it never disappears into a wall.
        /// </summary>
        public static Material Overlay(Texture tex)
        {
            Find();
            if (_ui == null) _ui = Shader.Find("UI/Default");
            Material m;
            if (_ui != null)
            {
                m = new Material(_ui) { name = "GM_Overlay" };
                m.mainTexture = tex != null ? tex : Texture2D.whiteTexture;
                m.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                m.renderQueue = 4000;
            }
            else
            {
                m = VertexLitTransparent(tex, Color.white);
                m.renderQueue = 4000;
            }
            return m;
        }

        private static void SetTexture(Material m, Texture tex)
        {
            if (tex == null) return;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static void MakeTransparent(Material m)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
        }
    }
}
