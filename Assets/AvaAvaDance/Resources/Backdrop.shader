Shader "AvaAvaDance/Backdrop" {
    Properties { _MainTex ("Image", 2D) = "white" {} }
    SubShader { Pass {
        Cull Off ZWrite Off ZTest Always
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex; float _Aspect; fixed4 _Border;
        fixed4 frag(v2f_img i) : SV_Target {
            float2 uv = (i.uv - .5) * float2(max(1, 1 / _Aspect), max(1, _Aspect)) + .5;
            if (any(uv < 0) || any(uv > 1)) return _Border;
            return tex2D(_MainTex, uv);
        }
        ENDCG
    }}
}
