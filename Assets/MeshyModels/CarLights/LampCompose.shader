// 車のランプの光（Emission）の絵を、ランプの種類ごとの明るさで合成する（CarLights.cs から Graphics.Blit で使う）
//  _MainTex = 光る色（lamp_color.png）、_Mask = R:ヘッドライト G:左オレンジ B:右オレンジ A:テール、_I = それぞれの明るさ
Shader "Hidden/CarLampCompose"
{
    Properties
    {
        _MainTex ("Lamp colour", 2D) = "black" {}
        _Mask ("Lamp mask", 2D) = "black" {}
    }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _Mask;
            float4 _I;
            float4 frag (v2f_img i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb;
                float4 m = tex2D(_Mask, i.uv);
                return float4(c * dot(m, _I), 1);
            }
            ENDCG
        }
    }
}
