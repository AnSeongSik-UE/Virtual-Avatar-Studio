Shader "Hidden/VAS/PremultipliedPreview"
{
    Properties
    {
        [PerRendererData] _MainTex ("Avatar Texture", 2D) = "black" {}
        _BackgroundColor ("Preview Background", Color) = (0.055, 0.075, 0.12, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            half4 _BackgroundColor;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 avatar = tex2D(_MainTex, input.uv);
                half3 preview = avatar.rgb + _BackgroundColor.rgb * (1.0h - avatar.a);
                return half4(preview, 1.0h);
            }
            ENDHLSL
        }
    }
}
