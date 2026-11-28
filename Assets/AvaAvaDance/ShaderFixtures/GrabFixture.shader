Shader "AvaAvaDance/GrabFixture"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest Always
        GrabPass { "_AvaFixtureColor" }
        Pass
        {
            Name "FIRST"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _AvaFixtureColor;
            struct Output { float4 pos:SV_POSITION; float4 uv:TEXCOORD0; };
            Output vert(float4 vertex:POSITION) { Output o; o.pos=UnityObjectToClipPos(vertex); o.uv=ComputeGrabScreenPos(o.pos); return o; }
            fixed4 frag(Output i):SV_Target {
                fixed3 color=tex2Dproj(_AvaFixtureColor,i.uv).rgb;
                return fixed4(0, color.r>.8 && color.b<.2 && color.g<.2 ? 1 : 0, 0, 1);
            }
            ENDCG
        }
        GrabPass { }
        Pass
        {
            Name "SECOND"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _GrabTexture;
            struct Output { float4 pos:SV_POSITION; float4 uv:TEXCOORD0; };
            Output vert(float4 vertex:POSITION) { Output o; o.pos=UnityObjectToClipPos(vertex); o.uv=ComputeGrabScreenPos(o.pos); return o; }
            fixed4 frag(Output i):SV_Target {
                fixed3 color=tex2Dproj(_GrabTexture,i.uv).rgb;
                return fixed4(0,0, color.g>.8 && color.b<.2 && color.r<.2 ? 1 : 0, 1);
            }
            ENDCG
        }
    }
}
