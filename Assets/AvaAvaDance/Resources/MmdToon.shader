Shader "AvaAvaDance/MMD Toon" {
    Properties {
        _MainTex ("Base", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Ramp ("Toon ramp", 2D) = "white" {}
    }
    SubShader { Tags { "RenderType"="Opaque" } Pass {
        Tags { "LightMode"="ForwardBase" }
        Cull Off
        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        #include "Lighting.cginc"
        sampler2D _MainTex, _Ramp; float4 _MainTex_ST, _Color;
        struct Varyings { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float light:TEXCOORD1; };
        Varyings vert(appdata_base v) {
            Varyings o; o.pos=UnityObjectToClipPos(v.vertex);
            o.uv=TRANSFORM_TEX(v.texcoord,_MainTex);
            o.light=.5+.5*dot(UnityObjectToWorldNormal(v.normal),normalize(_WorldSpaceLightPos0.xyz));
            return o;
        }
        fixed4 frag(Varyings i):SV_Target {
            fixed4 color=tex2D(_MainTex,i.uv)*_Color;clip(color.a-.03);
            color.rgb*=tex2D(_Ramp,float2(.5,saturate(i.light))).rgb*_LightColor0.rgb;
            return color;
        }
        ENDCG
    }}
}
