Shader "AvaAvaDance/FloorGrid"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Vertex { float4 position : POSITION; fixed4 color : COLOR; };
            struct Fragment { float4 position : SV_POSITION; fixed4 color : COLOR; };
            Fragment vert(Vertex v)
            {
                Fragment o;
                o.position = UnityObjectToClipPos(v.position);
                o.color = v.color;
                return o;
            }
            fixed4 frag(Fragment f) : SV_Target { return f.color; }
            ENDCG
        }
    }
}
