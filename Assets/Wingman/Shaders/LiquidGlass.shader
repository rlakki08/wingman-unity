// Liquid Glass UI shader for Wingman HUD
// Frosted glass effect: SDF rounded rect, border, procedural noise, tinted fill
// Based on UI/Default Correct (VR-correct alpha) + RoundedBoxUnlit (SDF math)
Shader "Wingman/LiquidGlass"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _TintColor ("Tint Color", Color) = (0.05, 0.05, 0.1, 1)
        _Opacity ("Fill Opacity", Range(0, 1)) = 0.15
        _CornerRadius ("Corner Radius", Range(0, 0.5)) = 0.08
        _BorderWidth ("Border Width", Range(0, 0.05)) = 0.008
        _BorderColor ("Border Color", Color) = (1, 1, 1, 0.4)
        _BorderSoftness ("Border Softness", Range(0.001, 0.02)) = 0.003
        _NoiseScale ("Noise Scale", Range(1, 100)) = 30
        _NoiseOpacity ("Noise Opacity", Range(0, 0.3)) = 0.04
        _FresnelPower ("Edge Glow Power", Range(0, 8)) = 2.5
        _FresnelOpacity ("Edge Glow Opacity", Range(0, 0.5)) = 0.12
        _HighlightColor ("Highlight Color", Color) = (0.7, 0.8, 1.0, 1)

        // UI stencil (required for Canvas masking)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "LiquidGlass"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex       : SV_POSITION;
                fixed4 color        : COLOR;
                float2 texcoord     : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                float2 localUV      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;

            fixed4 _TintColor;
            float _Opacity;
            float _CornerRadius;
            float _BorderWidth;
            fixed4 _BorderColor;
            float _BorderSoftness;
            float _NoiseScale;
            float _NoiseOpacity;
            float _FresnelPower;
            float _FresnelOpacity;
            fixed4 _HighlightColor;

            // ---- SDF Rounded Rectangle ----
            // Inigo Quilez's signed distance to a 2D rounded box
            // p = position (-0.5..0.5 centered), b = box half-extents, r = corner radius
            float sdRoundedBox(float2 p, float2 b, float r)
            {
                r = min(r, min(b.x, b.y));
                float2 q = abs(p) - b + r;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
            }

            // ---- Procedural noise (hash-based, no texture needed) ----
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 uv)
            {
                float2 i = floor(uv);
                float2 f = frac(uv);
                // Smooth interpolation
                float2 u = f * f * (3.0 - 2.0 * f);

                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float fbmNoise(float2 uv, int octaves)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                for (int i = 0; i < octaves; i++)
                {
                    value += amplitude * valueNoise(uv * frequency);
                    frequency *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color;

                // Map UVs to centered -0.5..0.5 space for SDF
                OUT.localUV = v.texcoord - 0.5;

                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.localUV; // -0.5 to 0.5
                float2 halfSize = float2(0.5, 0.5);

                // ---- SDF rounded rectangle ----
                float dist = sdRoundedBox(uv, halfSize, _CornerRadius);

                // Anti-aliased edge using screen-space derivatives
                float2 ddDist = float2(ddx(dist), ddy(dist));
                float ddLen = length(ddDist);
                float pixelDist = dist / max(ddLen, 0.0001);

                // Outside the rounded rect = fully transparent
                float outerMask = 1.0 - saturate(pixelDist + 0.5);

                // ---- Border (inner glow line) ----
                float borderInner = abs(dist + _BorderWidth * 0.5) - _BorderWidth * 0.5;
                float borderPixel = borderInner / max(ddLen, 0.0001);
                float borderMask = 1.0 - saturate(abs(borderPixel) / (_BorderSoftness / max(ddLen, 0.0001)));
                borderMask *= outerMask;

                // ---- Procedural noise (frosted texture) ----
                float noise = fbmNoise(IN.texcoord * _NoiseScale, 3);
                noise = noise * 2.0 - 1.0; // remap to -1..1

                // ---- Edge glow (Fresnel-like) ----
                // Distance from center normalized, raised at edges
                float edgeDist = length(uv) / length(halfSize);
                float edgeGlow = pow(saturate(edgeDist), _FresnelPower) * _FresnelOpacity;

                // ---- Compose final color ----
                // Base tinted fill
                fixed4 fillColor;
                fillColor.rgb = _TintColor.rgb;
                fillColor.a = _Opacity;

                // Add noise variation to the fill
                fillColor.rgb += noise * _NoiseOpacity;

                // Add edge glow highlight
                fillColor.rgb += _HighlightColor.rgb * edgeGlow;
                fillColor.a += edgeGlow * 0.3;

                // Blend in border
                fillColor.rgb = lerp(fillColor.rgb, _BorderColor.rgb, borderMask * _BorderColor.a);
                fillColor.a = lerp(fillColor.a, _BorderColor.a, borderMask * 0.8);

                // Apply vertex color (UI tint from Canvas)
                fillColor *= IN.color;

                // Apply rounded rect mask
                fillColor.a *= outerMask;

                // ---- UI clip rect (Canvas masking) ----
                #ifdef UNITY_UI_CLIP_RECT
                fillColor.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(fillColor.a - 0.001);
                #endif

                return fillColor;
            }
        ENDCG
        }
    }
}