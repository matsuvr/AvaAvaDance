Shader "AvaAvaDance/glTF Double Sided PBR"
{
    Properties
    {
        _Color ("Base color", Color) = (1,1,1,1)
        _MainTex ("Base color texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0
        _MetallicGlossMap ("Metallic and smoothness", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _GlossMapScale ("Smoothness scale", Range(0,1)) = 1
        _BumpMap ("Normal", 2D) = "bump" {}
        _BumpScale ("Normal scale", Float) = 1
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _OcclusionStrength ("Occlusion strength", Range(0,1)) = 1
        _EmissionColor ("Emission", Color) = (0,0,0,1)
        _EmissionMap ("Emission texture", 2D) = "white" {}
        [HideInInspector] _Mode ("Mode", Float) = 0
        [HideInInspector] _SrcBlend ("Source blend", Float) = 1
        [HideInInspector] _DstBlend ("Destination blend", Float) = 0
        [HideInInspector] _ZWrite ("Depth write", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        Pass
        {
            Name "FORWARD"
            Tags { "LightMode"="ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertForwardBase
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap
            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile_local _ _METALLICGLOSSMAP
            #pragma multi_compile_local _ _EMISSION
            #pragma multi_compile_local _ _ALPHATEST_ON _ALPHABLEND_ON
            #define UNITY_SETUP_BRDF_INPUT MetallicSetup
            #include "UnityStandardCore.cginc"
            half4 frag(VertexOutputForwardBase i, fixed face : VFACE) : SV_Target
            {
                if (face < 0) for (int n=0; n<3; ++n) i.tangentToWorldAndPackedData[n].xyz *= -1;
                return fragForwardBaseInternal(i);
            }
            ENDCG
        }
        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode"="ForwardAdd" }
            Blend [_SrcBlend] One
            ZWrite Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertForwardAdd
            #pragma fragment frag
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile_local _ _METALLICGLOSSMAP
            #pragma multi_compile_local _ _ALPHATEST_ON _ALPHABLEND_ON
            #define UNITY_SETUP_BRDF_INPUT MetallicSetup
            #include "UnityStandardCore.cginc"
            half4 frag(VertexOutputForwardAdd i, fixed face : VFACE) : SV_Target
            {
                if (face < 0) for (int n=0; n<3; ++n) i.tangentToWorldAndLightDir[n].xyz *= -1;
                return fragForwardAddInternal(i);
            }
            ENDCG
        }
        UsePass "Standard/SHADOWCASTER"
    }
}
