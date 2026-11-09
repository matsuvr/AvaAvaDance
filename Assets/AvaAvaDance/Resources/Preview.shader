Shader "AvaAvaDance/Preview"
{
 Properties { _MainTex("Texture", 2D)="white"{} _Color("Color",Color)=(0.65,0.65,0.7,1) }
 SubShader {
  Tags { "RenderType"="Opaque" }
  Cull Off
  CGPROGRAM
  #pragma surface surf Lambert
  sampler2D _MainTex; fixed4 _Color;
  struct Input { float2 uv_MainTex; };
  void surf(Input IN, inout SurfaceOutput o) { fixed4 c=tex2D(_MainTex,IN.uv_MainTex)*_Color; o.Albedo=c.rgb; o.Emission=c.rgb*0.25; o.Alpha=1; }
  ENDCG
 }
 Fallback "Diffuse"
}
