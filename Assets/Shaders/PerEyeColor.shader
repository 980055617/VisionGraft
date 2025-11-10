Shader "Custom/PerEyeVisibilityURP"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _EyeMode ("Eye Mode (0=Both, 1=LeftOnly, 2=RightOnly)", Float) = 0
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _EyeMode;    // 0: 両目, 1: 左のみ, 2: 右のみ
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    // _EyeMode == 1 → 左目専用：右目(1)のとき捨てる
                    if (_EyeMode == 1 && unity_StereoEyeIndex == 1)
                        discard;

                    // _EyeMode == 2 → 右目専用：左目(0)のとき捨てる
                    if (_EyeMode == 2 && unity_StereoEyeIndex == 0)
                        discard;
                #endif

                return _Color;
            }
            ENDHLSL
        }
    }
}
