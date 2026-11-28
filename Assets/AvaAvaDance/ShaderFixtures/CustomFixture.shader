Shader "AvaAvaDance/CustomFixture"
{
 Properties { _Color("Color", Color)=(1,0.2,0.1,1) }
 SubShader { Tags {"RenderType"="Opaque"} Pass {
 Cull Off
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "UnityCG.cginc"
 float4 _Color;
 float4 vert(float4 vertex:POSITION):SV_POSITION { return UnityObjectToClipPos(vertex); }
 fixed4 frag():SV_Target { return _Color; }
 ENDCG
 } }
}
