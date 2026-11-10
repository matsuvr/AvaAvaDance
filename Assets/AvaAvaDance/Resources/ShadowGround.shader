Shader "AvaAvaDance/ShadowGround" {
    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" }
        Pass {
            Tags { "LightMode"="ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "AvatarShadow.cginc"
            struct v2f { float4 pos : SV_POSITION; float3 world:TEXCOORD0; };
            v2f vert(appdata_base v) {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.world=mul(unity_ObjectToWorld,v.vertex).xyz; return o;
            }
            float _GroundBrightness, _GroundOpacity; float4 _GroundTint;
            fixed4 frag(v2f i) : SV_Target {
                fixed alpha=(1-avaVisibility(i.world,0.0006))*_GroundOpacity;
                return fixed4(_GroundTint.rgb * _GroundBrightness, alpha);
            }
            ENDCG
        }
    }
}
