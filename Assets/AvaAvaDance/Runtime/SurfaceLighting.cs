using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        Texture2D surfaceAttenuation;
        static readonly string[] lightKeywords={"DIRECTIONAL","DIRECTIONAL_COOKIE","POINT","POINT_COOKIE","SPOT"};
        void DrawAdditionalLights(CommandBuffer commands,Renderer renderer,Material material,int slot,int pass)
        {
            var lights=Light.GetLights(LightType.Directional,renderer.gameObject.layer)
                .Concat(Light.GetLights(LightType.Point,renderer.gameObject.layer)).Concat(Light.GetLights(LightType.Spot,renderer.gameObject.layer))
                .Where(l=>l!=previewLamp && l.isActiveAndEnabled && l.intensity>0 && l.renderMode!=LightRenderMode.ForceVertex &&
                    (l.type==LightType.Directional || renderer.bounds.SqrDistance(l.transform.position)<l.range*l.range)).ToArray();
            if(lights.Length==0)return;
            if(surfaceAttenuation==null){
                surfaceAttenuation=new Texture2D(128,1,TextureFormat.RGBA32,false,true){wrapMode=TextureWrapMode.Clamp};
                var colors=new Color[128];
                for(int i=0;i<128;i++){float r=i/127f;float a=Mathf.Max(0,1-r*r)/(1+25*r);colors[i]=new Color(a,a,a,a);}
                surfaceAttenuation.SetPixels(colors);surfaceAttenuation.Apply(false,true);
            }
            var previous=lightKeywords.Where(Shader.IsKeywordEnabled).ToArray();
            var position=Shader.GetGlobalVector("_WorldSpaceLightPos0");var color=Shader.GetGlobalVector("_LightColor0");
            var matrix=Shader.GetGlobalMatrix("unity_WorldToLight");var texture=Shader.GetGlobalTexture("_LightTexture0");var falloff=Shader.GetGlobalTexture("_LightTextureB0");
            foreach(var l in lights){
                bool directional=l.type==LightType.Directional;
                foreach(var keyword in lightKeywords)commands.DisableShaderKeyword(keyword);
                string type=directional?"DIRECTIONAL":l.type==LightType.Point?"POINT":"SPOT";
                commands.EnableShaderKeyword(type+(l.cookie!=null && type!="SPOT"?"_COOKIE":""));
                var v=directional?-l.transform.forward:l.transform.position;
                commands.SetGlobalVector("_WorldSpaceLightPos0",new Vector4(v.x,v.y,v.z,directional?0:1));
                commands.SetGlobalVector("_LightColor0",l.color*l.intensity);
                var scale=1/Mathf.Max(.001f,directional?l.cookieSize:l.range);
                commands.SetGlobalMatrix("unity_WorldToLight",Matrix4x4.Scale(Vector3.one*scale)*l.transform.worldToLocalMatrix);
                commands.SetGlobalTexture("_LightTexture0",l.cookie!=null?l.cookie:(Texture)surfaceAttenuation);
                commands.SetGlobalTexture("_LightTextureB0",surfaceAttenuation);commands.DrawRenderer(renderer,material,slot,pass);
            }
            foreach(var keyword in lightKeywords)commands.DisableShaderKeyword(keyword);
            foreach(var keyword in previous)commands.EnableShaderKeyword(keyword);
            commands.SetGlobalVector("_WorldSpaceLightPos0",position);commands.SetGlobalVector("_LightColor0",color);
            commands.SetGlobalMatrix("unity_WorldToLight",matrix);commands.SetGlobalTexture("_LightTexture0",texture);
            commands.SetGlobalTexture("_LightTextureB0",falloff);
        }
    }
}
