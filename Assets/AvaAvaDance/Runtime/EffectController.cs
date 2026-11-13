using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class ControlBone
        {
            public string name,parent,shape;
            public Vector3 position,rotation;
            public float size=.1f;
        }
        [Serializable] sealed class ControlMorph {public string name;public float value;}
        [Serializable] sealed class EffectController
        {
            public int schemaVersion;
            public string alias,effect,coordinateSpace;
            public ControlBone[] bones;
            public ControlMorph[] morphs;
        }
        static bool IsController(string file)=>file.EndsWith(".avaeffect",StringComparison.OrdinalIgnoreCase);
        static EffectController Controller(byte[] bytes)
        {
            var control=JsonUtility.FromJson<EffectController>(new UTF8Encoding(false,true).GetString(bytes));
            if(control?.schemaVersion!=1)throw new InvalidDataException("制御モデルの版が不正です。");
            control.bones??=Array.Empty<ControlBone>();control.morphs??=Array.Empty<ControlMorph>();
            if(control.bones.Length>4096 || control.morphs.Length>4096)throw new InvalidDataException("制御項目が多すぎます。");
            var known=new HashSet<string>();
            foreach(var bone in control.bones)
                if(bone==null || string.IsNullOrEmpty(bone.name) || !string.IsNullOrEmpty(bone.parent) && !known.Contains(bone.parent) ||
                    !known.Add(bone.name) || !float.IsFinite(bone.position.sqrMagnitude+bone.rotation.sqrMagnitude) || !float.IsFinite(bone.size))
                    throw new InvalidDataException("制御ボーンの階層または姿勢が不正です。");
            if(control.morphs.Any(m=>m==null || string.IsNullOrEmpty(m.name) || !float.IsFinite(m.value)) ||
                control.morphs.Select(m=>m.name).Distinct().Count()!=control.morphs.Length)throw new InvalidDataException("制御モーフが不正です。");
            return control;
        }
        static GameObject BuildController(EffectController descriptor,string name,out List<Transform> joints)
        {
            var root=new GameObject(name);var map=new Dictionary<string,Transform>();joints=new List<Transform>();
            try {
                foreach(var b in descriptor.bones)
                {
                    var joint=new GameObject(b.name).transform;
                    joint.SetParent(string.IsNullOrEmpty(b.parent)?root.transform:map[b.parent],false);
                    joint.localPosition=b.position; joint.localRotation=Quaternion.Euler(b.rotation);map.Add(b.name,joint);joints.Add(joint);
                    if(string.IsNullOrEmpty(b.shape))continue;
                    if(!Enum.TryParse<PrimitiveType>(b.shape,true,out var shape))throw new InvalidDataException("制御モデルの形状が不正です。");
                    var primitive=GameObject.CreatePrimitive(shape);primitive.transform.SetParent(joint,false);
                    primitive.transform.localScale=Vector3.one*Mathf.Clamp(b.size,.001f,100f);
                }
                return root;
            }catch{Destroy(root);throw;}
        }

        float[] ControlValue(MaterialSlot self,ControlVariable request)
        {
            string name=request.Object;
            string ownerId=name=="(OffscreenOwner)"?self.OwnerSlot?.Split('/')[0]:self.ActorId;
            bool own=string.IsNullOrEmpty(name) || name=="(self)" || name=="(OffscreenOwner)";
            var targets=drawOrder.Select(a=>(id:a.id,file:a.modelAssets.FirstOrDefault(m=>m.Guid==a.modelGuid)?.Path,actor:a,prop:(Prop)null))
                .Concat(props.Select(p=>(id:p.Data.id,file:p.Data.EffectFile,actor:(Actor)null,prop:p))).ToList();
            int owner=targets.FindIndex(o=>o.id==ownerId);
            bool Matches(int n)=>own?targets[n].id==ownerId:string.Equals(targets[n].actor?.controller?.alias,name,StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targets[n].file),name,StringComparison.OrdinalIgnoreCase);
            int index=Enumerable.Range(0,Math.Max(owner,0)).Reverse().Concat(Enumerable.Range(0,targets.Count).Reverse()).FirstOrDefault(i=>Matches(i));
            var output=new float[16];
            void Matrix(Matrix4x4 m){for(int i=0;i<16;i++)output[i]=m[i];}
            void Vector(Vector3 v){output[0]=v.x;output[1]=v.y;output[2]=v.z;output[3]=1;}
            bool isMatrix=request.Type.Contains("4x4");
            bool isVector=request.Type=="float3" || request.Type=="float4";
            if(targets.Count==0 || !Matches(index))
            {
                bool accessory=Path.GetExtension(name??"").Equals(".x",StringComparison.OrdinalIgnoreCase);
                if(isMatrix)Matrix(Matrix4x4.Scale(Vector3.one*(accessory && string.IsNullOrEmpty(request.Item)?10:1)));
                else if(isVector && (!accessory || string.IsNullOrEmpty(request.Item)))output[3]=1;else if(string.IsNullOrEmpty(request.Item) && request.Type=="float")output[0]=accessory?10:1;
                return output;
            }
            var logical=EffectWorld;
            var target=targets[index];var transform=target.actor?.avatar.transform??target.prop.Object.transform;
            if(string.IsNullOrEmpty(request.Item))
            {
                if(isMatrix)Matrix(logical*transform.localToWorldMatrix);
                else if(isVector)Vector(logical.MultiplyPoint3x4(transform.position));
                else output[0]=request.Type=="bool"?BitConverter.Int32BitsToSingle(transform.gameObject.activeSelf?1:0):target.prop?.Track.Current.Scale*10??1;
            }
            else if(target.prop!=null)
            {
                var p=target.prop.Track.Current;var angle=p.Rotation*Mathf.Deg2Rad;
                switch(request.Item.ToUpperInvariant()){
                    case "XYZ":Vector(p.Position);break;case "RXYZ":Vector(angle);break;
                    case "X":output[0]=p.Position.x;break;case "Y":output[0]=p.Position.y;break;case "Z":output[0]=p.Position.z;break;
                    case "RX":output[0]=angle.x;break;case "RY":output[0]=angle.y;break;case "RZ":output[0]=angle.z;break;
                    case "SI":output[0]=p.Scale*10;break;case "TR":output[0]=p.Opacity;break;
                }
            }
            else
            {
                var bone=target.actor.bones.Find(b=>b.Track.Name==request.Item);
                if(bone!=null){if(isMatrix)Matrix(logical*bone.Joint.localToWorldMatrix);else Vector(logical.MultiplyPoint3x4(bone.Joint.position));}
                else output[0]=target.actor.modelTracks.OfType<MotionTrack<float>>().FirstOrDefault(m=>MorphName(m)==request.Item)?.Current??0;
            }
            return output;
        }
    }
}
