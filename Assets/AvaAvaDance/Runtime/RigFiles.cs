using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ava.Extensions;
using AvaAvaDance.Import;
using UniGLTF;
using UniJSON;
using UnityEngine;

namespace AvaAvaDance
{
    internal static class RigFiles
    {
        internal static RigProfile[] Read(string path)
        {
            if (new FileInfo(path).Length > UnityPackageReader.MaxExpandedBytes) throw new IOException("モデルのサイズ上限を超えました。");
            if(path.EndsWith(".unitypackage",StringComparison.OrdinalIgnoreCase))
            {
                var assets=UnityPackageReader.Read(path);
                var models=assets.Where(a=>a.HasAsset && a.Path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)).ToList();
                if(models.Count==0) throw new InvalidDataException("FBXがありません。");
                return PackageAvatar.Choices(assets,models).Select(item=>{
                    var rig=FbxNative.ReadRig(item.Model,item.Prefab);rig.name=item.Label;
                    using(var hash=System.Security.Cryptography.SHA256.Create())rig.sourceHash=Convert.ToBase64String(hash.ComputeHash(item.Model.Data));
                    rig.sourceId=item.Prefab==null?item.Model.Guid:item.Prefab.Guid+"/"+item.Model.Guid;
                    return rig;
                }).ToArray();
            }
            if(path.EndsWith(".avarig",StringComparison.OrdinalIgnoreCase))
            {
                var rig=JsonUtility.FromJson<RigProfile>(File.ReadAllText(path));
                if(rig?.format!="AvaAvaDance rig 1" || rig.schemaVersion!=1 || !(rig.metersPerMotionUnit>0) || !double.IsFinite(rig.metersPerMotionUnit) || rig.joints==null || rig.joints.Length==0 || rig.joints.Length>65536 ||
                    rig.joints.Any(j=>j==null || j.parent >= rig.joints.Length || j.parent < -1 || !double.IsFinite(j.x+j.y+j.z) || !float.IsFinite(j.qx+j.qy+j.qz+j.qw) || !float.IsFinite(j.scaleX+j.scaleY+j.scaleZ))) throw new InvalidDataException("リグ形式が不正です。");
                ValidateHierarchy(rig.joints.Select(j=>j.parent).ToArray());
                bool Vector(double[] values,int length)=>values==null || values.Length==length && values.All(double.IsFinite);
                foreach(var shape in rig.shapes??Array.Empty<RigShape>()){
                    if(shape==null || !Vector(shape.center,3) || !Vector(shape.axis,3) || !Vector(shape.boxAxes,9) ||
                        !double.IsFinite(shape.radius) || !double.IsFinite(shape.height) || shape.radius<0 || shape.height<0 ||
                        shape.shape=="box" && shape.boxAxes==null)throw new InvalidDataException("衝突形状の座標または寸法が不正です。");
                    if(string.IsNullOrEmpty(shape.shape))shape.shape="sphere";
                }
                return new[]{rig};
            }
            if(!path.EndsWith(".vrm",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("モデル形式を確認してください。");
            var bytes=File.ReadAllBytes(path);
            string originalHash;using(var hash=System.Security.Cryptography.SHA256.Create())originalHash=Convert.ToBase64String(hash.ComputeHash(bytes));
            using var data=new GlbBinaryParser(bytes,Path.GetFileName(path)).Parse();
            var nodes=data.GLTF.nodes;
            int[] parents=Enumerable.Repeat(-1,nodes.Count).ToArray();
            for(int i=0;i<nodes.Count;i++)
                foreach(int child in nodes[i].children??Array.Empty<int>())
                {
                    if((uint)child>=nodes.Count || parents[child]!=-1) throw new InvalidDataException("リグの親子関係が不正です。");
                    parents[child]=i;
                }
            var rigProfile=Build(Path.GetFileName(path),"",parents,nodes.Select(n=>n.name).ToArray(),nodes.Select(VrmaClip.RestTransform).ToArray(),out var world);
            void Role(int node,string role) { if((uint)node<rigProfile.joints.Length) rigProfile.joints[node].role=role; }
            rigProfile.sourceHash=originalHash;
            var expressions=new SortedSet<string>(StringComparer.Ordinal);var shapes=new List<RigShape>();
            foreach(var extension in ((glTFExtensionImport)data.GLTF.extensions).ObjectItems())
            {
                void Shape(int node,Vector3 start,Vector3 end,float radius,bool capsule)
                {
                    if((uint)node>=nodes.Count || !float.IsFinite(radius) || radius<=0)return;
                    start.x=-start.x;end.x=-end.x;
                    shapes.Add(RoundShape(rigProfile.joints[node].name,node,start,end,radius,capsule,shapes.Count));
                }
                Vector3 Point(JsonNode p,bool array)
                {
                    if(array){var v=p.ArrayItems().Select(e=>e.GetSingle()).ToArray();if(v.Length!=3)throw new InvalidDataException("コライダーの座標が不正です。");return new Vector3(v[0],v[1],v[2]);}
                    return new Vector3(p["x"].GetSingle(),p["y"].GetSingle(),p["z"].GetSingle());
                }
                if(extension.Key.GetString()=="VRMC_springBone" && extension.Value.TryGet("colliders",out var colliders))
                    foreach(var item in colliders.ArrayItems()){
                        int node=item["node"].GetInt32();var shape=item["shape"];
                        bool capsule=shape.TryGet("capsule",out var config);
                        if(!capsule && !shape.TryGet("sphere",out config))continue;
                        var start=config.TryGet("offset",out var offset)?Point(offset,true):Vector3.zero;
                        var end=capsule && config.TryGet("tail",out var tail)?Point(tail,true):start;
                        Shape(node,start,end,config["radius"].GetSingle(),capsule);
                    }
                if(extension.Key.GetString()=="VRM" && extension.Value.TryGet("secondaryAnimation",out var dynamics) &&
                    dynamics.TryGet("colliderGroups",out var colliderGroups))foreach(var group in colliderGroups.ArrayItems()){
                        if(!group.TryGet("colliders",out var list))continue;
                        foreach(var collider in list.ArrayItems()){
                            var center=collider.TryGet("offset",out var offset)?Point(offset,false):Vector3.zero;
                            Shape(group["node"].GetInt32(),center,center,collider["radius"].GetSingle(),false);
                        }
                    }
                if(extension.Value.TryGet("blendShapeMaster",out var master) && master.TryGet("blendShapeGroups",out var groups))
                    foreach(var group in groups.ArrayItems())if(group.TryGet("name",out var name))expressions.Add(name.GetString());
                if(extension.Value.TryGet("expressions",out var expr))foreach(var category in expr.ObjectItems())
                    foreach(var item in category.Value.ObjectItems())expressions.Add(item.Key.GetString());
                if(!extension.Value.TryGet("humanoid",out var human) || !human.TryGet("humanBones",out var bones))continue;
                if(extension.Key.GetString()=="VRM") foreach(var bone in bones.ArrayItems()) Role(bone["node"].GetInt32(),bone["bone"].GetString());
                if(extension.Key.GetString()=="VRMC_vrm") foreach(var bone in bones.ObjectItems()) Role(bone.Value["node"].GetInt32(),bone.Key.GetString());
            }
            RigMeasurements.FromGltf(data,rigProfile,world);
            rigProfile.morphs=expressions.ToArray();rigProfile.shapes=shapes.ToArray();return new[]{rigProfile};
        }

        static RigShape RoundShape(string joint,int index,Vector3 start,Vector3 end,float radius,bool capsule,int number)
        {
            var center=(start+end)*.5f;var axis=(end-start).normalized;
            return new RigShape{name=joint+" "+(number+1),role=joint,jointIndex=index,shape=capsule?"capsule":"sphere",
                center=new double[]{center.x,center.y,center.z},axis=new double[]{axis.x,axis.y,axis.z},
                radius=radius,height=Vector3.Distance(start,end)+radius*2};
        }
        internal static RigShape[] SpringShapes(GameObject root)
        {
            var indices=root.GetComponentsInChildren<Transform>(true).Select((t,i)=>(t,i)).ToDictionary(p=>p.t,p=>p.i);
            var result=new List<RigShape>();
            foreach(var c in root.GetComponentsInChildren<UniVRM10.VRM10SpringBoneCollider>(true)){
                bool capsule=c.ColliderType==UniVRM10.VRM10SpringBoneColliderTypes.Capsule;
                if(!capsule && c.ColliderType!=UniVRM10.VRM10SpringBoneColliderTypes.Sphere)continue;
                result.Add(RoundShape(c.name,indices[c.transform],c.Offset,capsule?c.Tail:c.Offset,c.Radius,capsule,result.Count));
            }
            return result.ToArray();
        }

        static void ValidateHierarchy(int[] parents)
        {
            var done=new bool[parents.Length];
            for(int i=0;i<parents.Length;i++)
            {
                var chain=new Stack<int>();for(int n=i;n>=0 && !done[n];n=parents[n])
                {if(chain.Count>=parents.Length)throw new InvalidDataException("リグ階層が循環しています。");chain.Push(n);}
                while(chain.Count>0)done[chain.Pop()]=true;
            }
        }
        internal static RigProfile FromHierarchy(GameObject root,Dictionary<HumanBodyBones,Transform> human)
        {
            var nodes=root.GetComponentsInChildren<Transform>(true);var indices=nodes.Select((t,i)=>(t,i)).ToDictionary(p=>p.t,p=>p.i);
            var profile=Build(root.name,"",nodes.Select(t=>t.parent!=null && indices.TryGetValue(t.parent,out int i)?i:-1).ToArray(),
                nodes.Select(t=>t.name).ToArray(),nodes.Select(t=>Matrix4x4.TRS(t.localPosition,t.localRotation,t.localScale)).ToArray());
            foreach(var role in human)if(indices.TryGetValue(role.Value,out int index))profile.joints[index].role=role.Key.ToString();
            return profile;
        }
        internal static RigProfile Build(string name,string source,int[] parents,string[] names,Matrix4x4[] local)=>Build(name,source,parents,names,local,out _);
        internal static RigProfile Build(string name,string source,int[] parents,string[] names,Matrix4x4[] local,out Matrix4x4[] world)
        {
            var joints=new RigJoint[parents.Length];world=new Matrix4x4[parents.Length];
            var chain=new Stack<int>();
            for(int i=0;i<parents.Length;i++)
            {
                for(int n=i;n>=0 && joints[n]==null;n=parents[n])
                {
                    if(chain.Count>=parents.Length) throw new InvalidDataException("リグの親子関係が循環しています。");
                    chain.Push(n);
                }
                while(chain.Count>0)
                {
                    int n=chain.Pop(), parent=parents[n];
                    world[n]=parent<0?local[n]:world[parent]*local[n];
                    var point=world[n].MultiplyPoint3x4(Vector3.zero);
                    var q=world[n].rotation;var scale=world[n].lossyScale;
                    joints[n]=new RigJoint {name=names[n],parent=parent,x=point.x,y=point.y,z=point.z,
                        qx=q.x,qy=q.y,qz=q.z,qw=q.w,scaleX=scale.x,scaleY=scale.y,scaleZ=scale.z};
                }
            }
            return new RigProfile{name=name,sourceId=source,joints=joints};
        }
    }
}
