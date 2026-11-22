// Additive-ish glow for beams, tracers, explosion bubbles, shields and the comfort vignette.
// Uses vertex colour (LineRenderer / particles) x _BaseColor x optional texture.
Shader "Cubestruction/UnlitTransparent"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend (One = additive)", Float) = 10
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        _Fresnel ("Fresnel edge boost", Range(0, 4)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Blend SrcAlpha [_DstBlend]
        ZWrite Off
        ZTest LEqual
        Cull [_Cull]
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor; float4 _MainTex_ST; half _Fresnel; half _DstBlend; half _Cull;
            CBUFFER_END
            struct A { float4 p : POSITION; float3 n : NORMAL; float2 uv : TEXCOORD0; half4 c : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; float2 uv : TEXCOORD0; half4 c : COLOR; half rim : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
            V vert (A i)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(i); UNITY_TRANSFER_INSTANCE_ID(i, o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(i.p.xyz);
                o.p = TransformWorldToHClip(ws);
                o.uv = TRANSFORM_TEX(i.uv, _MainTex);
                o.c = i.c;
                float3 n = TransformObjectToWorldNormal(i.n);
                o.rim = pow(1 - saturate(abs(dot(normalize(n), normalize(GetWorldSpaceViewDir(ws))))), 2) * _Fresnel;
                return o;
            }
            half4 frag (V i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * _BaseColor * i.c;
                c.a = saturate(c.a * (1 + i.rim));
                c.rgb += i.rim * 0.3;
                return c;
            }
            ENDHLSL
        }
    }
}
