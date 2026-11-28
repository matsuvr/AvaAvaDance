using System;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;
using UniGLTF;
using UnityEngine;

namespace AvaAvaDance
{
    // Measurements are attached to the rest skeleton and follow that joint during motion sampling.
    internal sealed class RigMeasurements
    {
        sealed class Point
        {
            internal string Role,Name;
            internal int Joint;
            internal Vector3 Direction,Position;
            internal float Score=float.NegativeInfinity;
        }
        readonly RigProfile rig;
        readonly Dictionary<int,List<Point>> points=new Dictionary<int,List<Point>>();

        internal RigMeasurements(RigProfile rig)
        {
            this.rig=rig;
            void Add(string role,string name,RigJoint joint,Vector3 axis,params string[] names)
            {
                if(joint==null || axis.sqrMagnitude<1e-10f || rig.Find(role,new[]{name}.Concat(names).ToArray())!=null)return;
                int i=Array.IndexOf(rig.joints,joint);
                if(!points.TryGetValue(i,out var list))points[i]=list=new List<Point>();
                list.Add(new Point{Role=role,Name=name,Joint=i,Direction=axis.normalized});
            }
            Add("headTop","頭頂実体",rig.Find("head","頭"),Vector3.up,"頭頂");
            foreach(var side in new[]{"left","right"}){
                var prefix=side=="left"?"左":"右";
                var foot=rig.Find(side+"Foot",prefix+"足首");Add(side+"Sole",prefix+"足底実体",foot,Vector3.down);
                var toe=rig.Find(side+"Toes",prefix+"つま先");
                if(foot!=null && toe!=null)Add(side+"ToeTip",prefix+"つま先実体",toe,
                    new Vector3((float)(toe.x-foot.x),0,(float)(toe.z-foot.z)));
                var wrist=rig.Find(side+"Hand",prefix+"手首");
                if(wrist==null)continue;
                foreach(var digit in new[]{("Thumb","親"),("Index","人"),("Middle","中"),("Ring","薬"),("Little","小")}){
                    var finger=rig.Find(side+digit.Item1+"Distal",prefix+digit.Item2+"指"+(digit.Item1=="Thumb"?"２":"３"));
                    if(finger==null)continue;
                    Add(side+digit.Item1+"Tip",prefix+digit.Item2+"指先実体",finger,
                        new Vector3((float)(wrist.x-finger.x),(float)(wrist.y-finger.y),(float)(wrist.z-finger.z)));
                }
            }
        }

        internal bool Uses(int joint)=>points.ContainsKey(joint);
        internal void Sample(int joint,Vector3 position)
        {
            if(!points.TryGetValue(joint,out var list))return;
            foreach(var point in list){
                float score=Vector3.Dot(position,point.Direction);
                if(score<=point.Score || !float.IsFinite(score))continue;
                point.Score=score;point.Position=position;
            }
        }

        internal void Finish()
        {
            var joints=rig.joints.ToList();
            foreach(var point in points.OrderBy(p=>p.Key).SelectMany(p=>p.Value)){
                if(float.IsNegativeInfinity(point.Score))continue;
                var p=point.Position;var parent=joints[point.Joint];
                joints.Add(new RigJoint{name=point.Name,role=point.Role,parent=point.Joint,x=p.x,y=p.y,z=p.z,
                    qx=parent.qx,qy=parent.qy,qz=parent.qz,qw=parent.qw,origin="weightedVertex"});
            }
            rig.joints=joints.ToArray();rig.measurementVersion=1;
        }

        internal static RigJoint[] EditRest(RigProfile source,RigJoint[] bones)
        {
            if(bones.Length==source.joints.Length)return bones;
            var result=bones.ToList();
            Matrix4x4 Pose(RigJoint j)=>Matrix4x4.TRS(new Vector3((float)j.x,(float)j.y,(float)j.z),new Quaternion(j.qx,j.qy,j.qz,j.qw),
                new Vector3(j.scaleX,j.scaleY,j.scaleZ));
            for(int i=bones.Length;i<source.joints.Length;i++){
                var point=source.joints[i];var parent=result[point.parent];
                var delta=Pose(parent)*Pose(source.joints[point.parent]).inverse;
                var p=delta.MultiplyPoint3x4(new Vector3((float)point.x,(float)point.y,(float)point.z));
                result.Add(new RigJoint{name=point.name,role=point.role,parent=point.parent,origin=point.origin,
                    x=p.x,y=p.y,z=p.z,qx=parent.qx,qy=parent.qy,qz=parent.qz,qw=parent.qw});
            }
            return result.ToArray();
        }

        internal static void FromScene(GameObject avatar,RigProfile profile)
        {
            var points=new RigMeasurements(profile);
            var index=avatar.GetComponentsInChildren<Transform>(true).Select((t,i)=>(t,i)).ToDictionary(p=>p.t,p=>p.i);
            foreach(var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
                var mesh=renderer.sharedMesh;if(mesh==null || !mesh.isReadable)continue;
                var bones=renderer.bones;var binds=mesh.bindposes;
                var joints=bones.Select(t=>t!=null && index.TryGetValue(t,out int i)?i:-1).ToArray();
                if(!joints.Any(points.Uses))continue;
                var matrices=bones.Select((t,i)=>t!=null && i<binds.Length?t.localToWorldMatrix*binds[i]:Matrix4x4.identity).ToArray();
                var vertices=mesh.vertices;var weights=mesh.boneWeights;
                for(int i=0;i<weights.Length;i++){
                    var w=weights[i];
                    void Measure(int link,float weight){
                        if(weight>0 && (uint)link<binds.Length && (uint)link<joints.Length && points.Uses(joints[link]))
                            points.Sample(joints[link],matrices[link].MultiplyPoint3x4(vertices[i]));
                    }
                    Measure(w.boneIndex0,w.weight0);Measure(w.boneIndex1,w.weight1);
                    Measure(w.boneIndex2,w.weight2);Measure(w.boneIndex3,w.weight3);
                }
            }
            points.Finish();
        }

        internal static void FromGltf(GltfData data,RigProfile profile,Matrix4x4[] world)
        {
            var points=new RigMeasurements(profile);
            foreach(var node in data.GLTF.nodes){
                if(node.mesh<0 || node.skin<0)continue;
                var skin=data.GLTF.skins[node.skin];var joints=skin.joints;
                if(joints==null || !joints.Any(points.Uses))continue;
                var matrices=joints.Select(j=>world[j]).ToArray();
                if(skin.inverseBindMatrices>=0){
                    var binds=data.GetArrayFromAccessor<Matrix4x4>(skin.inverseBindMatrices);
                    if(binds.Length!=joints.Length)throw new System.IO.InvalidDataException("スキンの逆バインド行列数が不正です。");
                    for(int i=0;i<matrices.Length;i++)matrices[i]*=binds[i].ReverseX();
                }
                foreach(var primitive in data.GLTF.meshes[node.mesh].primitives){
                    var a=primitive.attributes;if(a.POSITION<0 || a.JOINTS_0<0 || a.WEIGHTS_0<0)continue;
                    var vertices=data.GetArrayFromAccessor<Vector3>(a.POSITION);
                    var (indices,count)=JointsAccessor.GetAccessor(data,a.JOINTS_0);
                    var (weights,weightCount)=WeightsAccessor.GetAccessor(data,a.WEIGHTS_0);
                    if(count!=vertices.Length || weightCount!=count)throw new System.IO.InvalidDataException("スキンの頂点数が不正です。");
                    for(int i=0;i<count;i++){
                        var v=vertices[i];v.x=-v.x;var ids=indices(i);var w=weights(i);
                        void Measure(int link,float weight){
                            if(weight>0 && (uint)link<joints.Length && points.Uses(joints[link]))
                                points.Sample(joints[link],matrices[link].MultiplyPoint3x4(v));
                        }
                        Measure(ids.x,w.x);Measure(ids.y,w.y);Measure(ids.z,w.z);Measure(ids.w,w.w);
                    }
                }
            }
            points.Finish();
        }
    }
}
