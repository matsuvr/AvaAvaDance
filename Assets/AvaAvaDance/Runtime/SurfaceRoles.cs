using System.Linq;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        SurfaceBinding ResolveRole(MaterialSlot slot,string role)
        {
            var request=slot.ForRole(role);
            return surfaceProviders.Select(provider=>provider.Resolve(request)).FirstOrDefault(b=>b!=null);
        }

        Mesh CurrentSurfaceMesh(Renderer renderer)
        {
            if(renderer is SkinnedMeshRenderer skin){
                if(bakedSurface==null)bakedSurface=new Mesh();
                skin.BakeMesh(bakedSurface,true);
                return bakedSurface;
            }
            return renderer.GetComponent<MeshFilter>()?.sharedMesh;
        }

        void DrawSurfaceRoles(SurfaceBinding binding,MaterialSlot slot,Camera camera,Renderer renderer,Material material,
            Mesh mesh,float[] vertices,int[] indices,float[] values)
        {
            var roles=binding.Plan?.Roles;
            if(roles==null)return;
            if(groundShadow && roles.Contains("shadow")){
                var shadow=ResolveRole(slot,"shadow");
                var light=previewLamp.transform.forward;
                if(shadow?.Visible==true && shadow.Passes!=null && Mathf.Abs(light.y)>.0001f){
                    float floor=shadowFloor.transform.position.y+.0002f;
                    var planar=Matrix4x4.identity;
                    planar.SetColumn(1,new Vector4(-light.x/light.y,0,-light.z/light.y,0));
                    planar.SetColumn(3,new Vector4(floor*light.x/light.y,floor,floor*light.z/light.y,1));
                    var projected=SurfaceConstants(camera,planar*renderer.localToWorldMatrix,material,mesh);
                    QueueSurface(shadow,slot,camera,vertices,indices,material,projected);
                }
            }
            if(roles.Contains("edge") && viewOptions.edgeWidth!=0){
                var edge=ResolveRole(slot,"edge");
                if(edge?.Visible==true && edge.Passes!=null)QueueSurface(edge,slot,camera,vertices,indices,material,values);
            }
        }
    }
}
