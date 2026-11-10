Shader "AvaAvaDance/AvatarShadow" {
Properties { _MainTex("Opacity",2D)="white"{} _Cutoff("Cutoff",Float)=0.1 _Opacity("Alpha",Float)=1 }
SubShader {
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "AvatarShadow.cginc"
    sampler2D _MainTex; float4 _MainTex_ST; float _Cutoff, _Opacity;
    struct app {float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0;};
    struct v2f {float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float3 world:TEXCOORD1; float3 normal:TEXCOORD2;};
    v2f commonVert(app v) {
        v2f o; o.world=mul(unity_ObjectToWorld,v.vertex).xyz; o.normal=UnityObjectToWorldNormal(v.normal);
        o.uv=TRANSFORM_TEX(v.uv,_MainTex); o.pos=UnityObjectToClipPos(v.vertex); return o;
    }
    void clipOpacity(float2 uv) { clip(tex2D(_MainTex,uv).a*_Opacity-_Cutoff); }
    ENDCG
    Pass {
        Cull Off ZWrite On ZTest LEqual
        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        v2f vert(app v) {
            v2f o=commonVert(v); float3 p=avaLightCoord(o.world);
            float2 xy=p.xy*2-1;
            #if UNITY_UV_STARTS_AT_TOP
                xy.y=-xy.y;
            #endif
            #if defined(UNITY_REVERSED_Z)
                o.pos=float4(xy,1-p.z,1);
            #else
                o.pos=float4(xy,lerp(UNITY_NEAR_CLIP_VALUE,1,p.z),1);
            #endif
            return o;
        }
        float4 frag(v2f i):SV_Target { clipOpacity(i.uv); return avaLightCoord(i.world).zzzz; }
        ENDCG
    }
    Pass {
        Cull Off ZWrite Off ZTest LEqual Blend DstColor Zero ColorMask RGB
        CGPROGRAM
        #pragma vertex commonVert
        #pragma fragment frag
        fixed4 frag(v2f i):SV_Target {
            clipOpacity(i.uv);
            float3 face=cross(ddx(i.world),ddy(i.world));
            float cosine=abs(dot(face,_AvaLightForward.xyz))/max(1e-8,length(face));
            float slope=sqrt(saturate(1-cosine*cosine))/max(.02,cosine);
            float bias=.0004+1.9*slope*_AvaShadowScale.z*_AvaShadowScale.y/_AvaShadowScale.x;
            float lit=avaVisibility(i.world,bias);
            return fixed4(lerp(.63,1,lit).xxx,1);
        }
        ENDCG
    }
}
}
