Shader "Skunkworks/TerminalAtlas"
{
    Properties { _MainTex ("Font atlas", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; float2 mode:TEXCOORD1; float4 color:COLOR; };
            struct Output { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float mode:TEXCOORD1; float4 color:COLOR; };
            Output vert(Input v)
            {
                Output o; o.position=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.mode=v.mode.x; o.color=v.color;
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color.rgb=GammaToLinearSpace(o.color.rgb);
                #endif
                return o;
            }
            float4 frag(Output i):SV_Target
            {
                return float4(i.color.rgb, i.color.a * lerp(1, tex2D(_MainTex,i.uv).a, i.mode));
            }
            ENDHLSL
        }
    }
}
