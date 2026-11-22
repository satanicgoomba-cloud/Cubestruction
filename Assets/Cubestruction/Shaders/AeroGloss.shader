// Frutiger Aero "glossy plastic / glass" look for voxels, Buddies and tools.
// Lambert + wrapped sky ambient + tight Blinn highlight + fresnel rim, optional vertex colour
// and per-vertex glow (uv2.x, used by Crystal/Magma/Core voxels). Single pass, no shadows: Quest friendly.
Shader "Cubestruction/AeroGloss"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _UseVertexColor ("Use Vertex Color", Float) = 0
        _Gloss ("Gloss (spec power)", Range(8, 256)) = 90
        _SpecStrength ("Spec Strength", Range(0, 2)) = 0.9
        _RimColor ("Rim Color", Color) = (0.75, 0.95, 1, 1)
        _RimStrength ("Rim Strength", Range(0, 2)) = 0.6
        _SkyColor ("Sky Ambient", Color) = (0.62, 0.85, 1, 1)
        _GroundColor ("Ground Ambient", Color) = (0.55, 0.75, 0.45, 1)
        _GlowColor ("Glow Tint", Color) = (1, 0.8, 0.5, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor; half _UseVertexColor; half _Gloss; half _SpecStrength;
                half4 _RimColor; half _RimStrength; half4 _SkyColor; half4 _GroundColor; half4 _GlowColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; float2 uv2 : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1;
                half4 color : COLOR; half glow : TEXCOORD2; half fog : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);   // single-pass instanced stereo on Quest
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = lerp(half4(1,1,1,1), v.color, _UseVertexColor);
                o.glow = v.uv2.x * _UseVertexColor;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 albedo = _BaseColor.rgb * i.color.rgb;
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light L = GetMainLight();
                half ndl = saturate(dot(n, L.direction) * 0.8 + 0.2);            // wrapped diffuse = soft plastic
                half3 ambient = lerp(_GroundColor.rgb, _SkyColor.rgb, n.y * 0.5 + 0.5);
                float3 h = normalize(L.direction + v);
                half spec = pow(saturate(dot(n, h)), _Gloss) * _SpecStrength;
                // second, broader "studio softbox" highlight from above for the glassy Aero sheen
                spec += pow(saturate(dot(reflect(-v, n), float3(0, 1, 0))), 6) * 0.25 * _SpecStrength;
                half rim = pow(1 - saturate(dot(n, v)), 3) * _RimStrength;
                half3 col = albedo * (ambient * 0.55 + L.color * ndl * 0.75) + L.color * spec + _RimColor.rgb * rim;
                col += albedo * _GlowColor.rgb * i.glow * (0.65 + 0.35 * sin(_Time.y * 2 + i.positionWS.y * 3));
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex v
            #pragma fragment f
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 p : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            V v (A i) { V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.p = TransformObjectToHClip(i.p.xyz); return o; }
            half f (V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
