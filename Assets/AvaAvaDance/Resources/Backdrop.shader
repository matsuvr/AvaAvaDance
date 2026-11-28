Shader "AvaAvaDance/Backdrop" {
    Properties { _MainTex ("Image", 2D) = "white" {} }
    SubShader { Pass {
        Cull Off ZWrite Off ZTest Always
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex; float _Aspect; fixed4 _Border; float4 _Placement;
        fixed4 frag(v2f_img i) : SV_Target {
            float2 uv = (i.uv - .5 - _Placement.xy) / max(.01, _Placement.z) * float2(max(1, 1 / _Aspect), max(1, _Aspect)) + .5;
            if (any(uv < 0) || any(uv > 1)) return _Border;
            return tex2D(_MainTex, uv);
        }
        ENDCG
    }
    Pass {
        Cull Off ZWrite Off ZTest Always
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment gradient
        #include "UnityCG.cginc"
        fixed4 gradient(v2f_img i) : SV_Target {
            float3 color = lerp(float3(48,54,61), float3(94,106,119), saturate(i.uv.y)) / 255.0;
            #if !defined(UNITY_COLORSPACE_GAMMA)
            color = GammaToLinearSpace(color);
            #endif
            return fixed4(color, 1);
        }
        ENDCG
    }}

}
