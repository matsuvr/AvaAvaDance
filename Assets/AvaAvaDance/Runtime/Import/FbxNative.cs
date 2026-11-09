using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance.Import
{
    /// <summary>Windows x64 runtime FBX subset. Never uses AssetDatabase or executes package code.</summary>
    public static class FbxNative
    {
        const string Dll = "AvaFbx";
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_load(byte[] data, int size, StringBuilder error, int capacity);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern void ava_free(IntPtr scene);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_nodes(IntPtr scene);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_name(IntPtr scene,int n);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_parent(IntPtr scene,int n);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern void ava_transform(IntPtr scene,int n,[Out]float[] values);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_vertices(IntPtr scene,int n);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_materials(IntPtr scene,int n);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_material_name(IntPtr scene,int n,int material);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_bones(IntPtr scene,int n);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_bone(IntPtr scene,int n,int bone,[Out]float[] bind);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_mesh(IntPtr scene,int n,[Out]float[] vertices,[Out]int[] bones,[Out]int[] materials,int capacity);
        static string Utf8(IntPtr p) { if(p==IntPtr.Zero)return ""; int len=0;while(len<4096&&Marshal.ReadByte(p,len)!=0)len++; var b=new byte[len];Marshal.Copy(p,b,0,len);return Encoding.UTF8.GetString(b); }

        public static GameObject Load(byte[] bytes, string name)
        {
            if(bytes==null||bytes.Length==0||bytes.Length>256*1024*1024)throw new ArgumentException("FBX size must be 1 byte to 256 MiB.");
            if(Application.platform!=RuntimePlatform.WindowsPlayer && Application.platform!=RuntimePlatform.WindowsEditor)
                throw new PlatformNotSupportedException("FBX runtime importer currently supports Windows x64 only.");
            var error=new StringBuilder(2048); var scene=ava_load(bytes,bytes.Length,error,error.Capacity);
            if(scene==IntPtr.Zero)throw new InvalidOperationException("FBX import failed or safety limit exceeded: "+error);
            var root=new GameObject(name); var assets=root.AddComponent<FbxOwnedResources>();
            try
            {
                int count=ava_nodes(scene);var nodes=new Transform[count];
                for(int i=0;i<count;i++)nodes[i]=new GameObject(Utf8(ava_name(scene,i))).transform;
                for(int i=0;i<count;i++)
                {
                    int parent=ava_parent(scene,i); nodes[i].SetParent(parent>=0&&parent<count?nodes[parent]:root.transform,false);
                    var t=new float[10];ava_transform(scene,i,t);
                    nodes[i].localPosition=new Vector3(t[0],t[1],t[2]);nodes[i].localRotation=new Quaternion(t[3],t[4],t[5],t[6]);nodes[i].localScale=new Vector3(t[7],t[8],t[9]);
                }
                for(int n=0;n<count;n++)
                {
                    int vc=ava_vertices(scene,n);if(vc==0)continue;
                    var raw=new float[checked(vc*12)];var ids=new int[checked(vc*4)];var matIds=new int[vc];
                    if(ava_mesh(scene,n,raw,ids,matIds,vc)!=vc)throw new InvalidOperationException("FBX triangulation failed.");
                    var vertices=new Vector3[vc];var normals=new Vector3[vc];var uv=new Vector2[vc];
                    int mc=Math.Max(1,ava_materials(scene,n));var triangles=new List<int>[mc];var materials=new Material[mc];
                    var shader=Shader.Find("AvaAvaDance/Preview");if(shader==null)throw new InvalidOperationException("Required preview shader missing from build.");
                    for(int m=0;m<mc;m++){triangles[m]=new List<int>();materials[m]=new Material(shader){name=Utf8(ava_material_name(scene,n,m)), color=new Color(.65f,.65f,.7f,1f)};assets.Own(materials[m]);}
                    for(int v=0;v<vc;v++){int a=v*12;vertices[v]=new Vector3(raw[a],raw[a+1],raw[a+2]);normals[v]=new Vector3(raw[a+3],raw[a+4],raw[a+5]);uv[v]=new Vector2(raw[a+6],raw[a+7]);triangles[Mathf.Clamp(matIds[v],0,mc-1)].Add(v);}
                    var mesh=new Mesh{name=nodes[n].name,indexFormat=IndexFormat.UInt32};assets.Own(mesh);mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.subMeshCount=mc;
                    for(int m=0;m<mc;m++)mesh.SetTriangles(triangles[m],m);
                    mesh.RecalculateBounds();
                    int bc=ava_bones(scene,n);
                    if(bc>0)
                    {
                        var bones=new Transform[bc+1];var binds=new Matrix4x4[bc+1];bones[bc]=nodes[n];binds[bc]=Matrix4x4.identity;
                        for(int b=0;b<bc;b++){var f=new float[12];int node=ava_bone(scene,n,b,f);if(node<0||node>=count)throw new InvalidOperationException("FBX has an unresolved skin bone.");bones[b]=nodes[node];var matrix=Matrix4x4.identity;for(int c=0;c<4;c++)matrix.SetColumn(c,new Vector4(f[c*3],f[c*3+1],f[c*3+2],c==3?1:0));binds[b]=matrix;}
                        var weights=new BoneWeight[vc];for(int v=0;v<vc;v++){int a=v*12;weights[v]=new BoneWeight{boneIndex0=ids[v*4],boneIndex1=ids[v*4+1],boneIndex2=ids[v*4+2],boneIndex3=ids[v*4+3],weight0=raw[a+8],weight1=raw[a+9],weight2=raw[a+10],weight3=raw[a+11]};if(raw[a+8]+raw[a+9]+raw[a+10]+raw[a+11]<=0)weights[v]=new BoneWeight{boneIndex0=bc,weight0=1};}
                        mesh.boneWeights=weights;mesh.bindposes=binds;var renderer=nodes[n].gameObject.AddComponent<SkinnedMeshRenderer>();renderer.sharedMesh=mesh;renderer.sharedMaterials=materials;renderer.bones=bones;renderer.rootBone=root.transform;renderer.localBounds=mesh.bounds;renderer.updateWhenOffscreen=true;
                    }
                    else {nodes[n].gameObject.AddComponent<MeshFilter>().sharedMesh=mesh;nodes[n].gameObject.AddComponent<MeshRenderer>().sharedMaterials=materials;}
                    mesh.RecalculateBounds();
                }
                foreach(var renderer in root.GetComponentsInChildren<Renderer>()) Debug.Log("FBX renderer " + renderer.name + " bounds=" + renderer.bounds + " local=" + (renderer is SkinnedMeshRenderer skinRenderer ? skinRenderer.localBounds.ToString() : "static"));
                return root;
            }
            catch { UnityEngine.Object.Destroy(root);throw; }
            finally { ava_free(scene); }
        }
    }
    public sealed class FbxOwnedResources : MonoBehaviour
    {
        readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        public void Own(UnityEngine.Object resource){if(resource!=null)owned.Add(resource);}
        void OnDestroy(){foreach(var item in owned)if(item!=null)Destroy(item);}
    }
}
