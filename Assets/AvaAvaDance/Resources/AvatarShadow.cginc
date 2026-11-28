#ifndef AVA_AVATAR_SHADOW
#define AVA_AVATAR_SHADOW
sampler2D _AvaShadowDepth;
float4 _AvaShadowOrigin, _AvaLightRight, _AvaLightUp, _AvaLightForward, _AvaShadowScale;
float3 avaLightCoord(float3 world) {
    float3 delta=world-_AvaShadowOrigin.xyz;
    return .5+float3(dot(delta,_AvaLightRight.xyz)*_AvaShadowScale.x,
        dot(delta,_AvaLightUp.xyz)*_AvaShadowScale.x,dot(delta,_AvaLightForward.xyz)*_AvaShadowScale.y);
}
float avaVisibility(float3 world,float bias) {
    float3 p=avaLightCoord(world);
    if(any(p<0) || any(p>1))return 1;
    float2 d=_AvaShadowScale.zz*.65;
    float z=p.z-bias;
    return .25*(step(z,tex2D(_AvaShadowDepth,p.xy+d).r)+step(z,tex2D(_AvaShadowDepth,p.xy-d).r)+
        step(z,tex2D(_AvaShadowDepth,p.xy+float2(d.x,-d.y)).r)+step(z,tex2D(_AvaShadowDepth,p.xy+float2(-d.x,d.y)).r));
}
#endif
