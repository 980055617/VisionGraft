Shader "Custom/PerEyeStereoVideoURP"
{
    Properties
    {
        _MainTex ("Video Texture", 2D) = "black" {}
        _Color   ("Tint Color", Color) = (1,1,1,1)
        _EyeMode ("Eye Mode (0=Both, 1=LeftOnly, 2=RightOnly)", Float) = 0
        _UVOffset ("UV Offset", Vector) = (0,0,0,0)
        _UVScale  ("UV Scale",  Vector) = (1,1,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _EyeMode;
                float4 _UVOffset;
                float4 _UVScale;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // 片目だけ表示するロジック
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    // _EyeMode == 1 → 左目だけ：右目(1)のとき捨てる
                    if (_EyeMode == 1 && unity_StereoEyeIndex == 1)
                        discard;

                    // _EyeMode == 2 → 右目だけ：左目(0)のとき捨てる
                    if (_EyeMode == 2 && unity_StereoEyeIndex == 0)
                        discard;
                #endif

                // UV をスケール＆オフセットして、片側半分だけ読む
                float2 uv = IN.uv * _UVScale.xy + _UVOffset.xy;

                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                return col * _Color;
            }
            ENDHLSL
        }
    }
}
