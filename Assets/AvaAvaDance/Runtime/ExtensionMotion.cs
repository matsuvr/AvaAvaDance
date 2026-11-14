using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ava.Extensions;
using UnityEngine;
using N=System.Numerics;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        IEnumerable<(string owner,MotionTrack track)> EditableTracks()
        {
            foreach(var item in drawOrder)foreach(var track in item.modelTracks)yield return (item.id,track);
            foreach(var track in sceneTracks)yield return (props.FirstOrDefault(p=>p.Track==track)?.Data.id??"scene",track);
        }

        static N.Vector3 ToSdk(Vector3 v)=>new N.Vector3(v.x,v.y,v.z);
        static Vector3 FromSdk(N.Vector3 v)=>new Vector3(v.X,v.Y,v.Z);
        static KeyValue ToKey(object pose, EaseCurve morph=default,KeySource source=null)
        {
            var key=new KeyValue();
            CurveSet curves=null;
            if(pose is BonePose b){
                key.Channel=TrackChannel.Bone;key.Position=ToSdk(b.Position);
                key.Rotation=new N.Quaternion(b.Rotation.x,b.Rotation.y,b.Rotation.z,b.Rotation.w);key.PhysicsOff=b.PhysicsOff;curves=b.Curves;
            }else if(pose is CameraPose c){
                key.Channel=TrackChannel.Camera;key.Position=ToSdk(c.Center);key.Euler=ToSdk(c.Angles);
                key.Distance=c.Distance;key.ViewAngle=c.ViewAngle;key.Perspective=c.Perspective;key.Disabled=c.Inactive;curves=c.Curves;
            }else if(pose is float f){
                key.Channel=TrackChannel.Morph;key.Weight=f;if(morph.custom)curves=new CurveSet{channels=new[]{morph}};
            }else if(pose is AccessoryPose a){
                key.Channel=TrackChannel.Accessory;key.Position=ToSdk(a.Position);key.Euler=ToSdk(a.Rotation);
                key.Scale=a.Scale;key.Opacity=a.Opacity;key.Hidden=a.Hidden;key.ParentBone=a.Parent;key.ParentActor=a.Model;key.ParentBoneId=a.ParentId;
            }else if(pose is LightPose l){
                key.Channel=TrackChannel.Light;key.Color=new N.Vector4(l.Color.r,l.Color.g,l.Color.b,l.Color.a);key.Direction=ToSdk(l.Direction);
            }else if(pose is SwitchPose s){
                key.Channel=TrackChannel.ModelSwitch;key.Hidden=s.Hidden;key.DisabledIK=s.DisabledIK?.ToArray();
            }else if(pose is GravityPose g){
                key.Channel=TrackChannel.Gravity;key.Direction=ToSdk(g.Direction);key.Acceleration=g.Acceleration;key.Noisy=g.Noisy;key.Noise=g.NoiseAmount;
            }else if(pose is ShadowPose shadow){
                key.Channel=TrackChannel.Shadow;key.ShadowMode=shadow.Mode;key.Range=shadow.Range;
            }else throw new ArgumentException("トラックの種類を取得できません。");
            key.Curves=curves?.channels?.Select(e=>new BezierCurve{Custom=e.custom,First=new N.Vector2(e.a.x,e.a.y),Second=new N.Vector2(e.b.x,e.b.y)}).ToArray();
            key.SourceName=source?.name?.ToArray();key.SourceIK=source?.ik?.ToArray();
            key.RawInterpolation=curves?.raw?.ToArray();key.ModifiedChannels=curves?.modified??0;
            return key;
        }

        static CurveSet ReadCurves(KeyEdit edit,MotionTrack track)
        {
            if(edit.Curves==CurveAction.Clear)return null;
            if(edit.Curves==CurveAction.Preserve){
                switch(track.KeyAt(edit.Frame)){
                    case BonePose b:return b.Curves;
                    case CameraPose c:return c.Curves;
                    case float _ when track.CurveAt(edit.Frame).custom:return new CurveSet{channels=new[]{track.CurveAt(edit.Frame)}};
                    default:return null;
                }
            }
            var k=edit.Value;
            int count=k.Channel==TrackChannel.Bone?4:k.Channel==TrackChannel.Camera?6:k.Channel==TrackChannel.Morph?1:0;
            int rawSize=k.Channel==TrackChannel.Bone?64:k.Channel==TrackChannel.Camera?24:0;
            // Bone and camera snapshots use up to six stored axis slots.
            if(edit.Curves!=CurveAction.Replace || (k.Curves?.Length??0)>(count==4?6:count) ||
               k.RawInterpolation!=null && (rawSize==0 || k.RawInterpolation.Length!=rawSize) ||
               k.ModifiedChannels<0 || k.ModifiedChannels>63)throw new InvalidDataException("補間データが不正です。");
            var result=new CurveSet{raw=k.RawInterpolation?.ToArray(),modified=k.ModifiedChannels,channels=k.Curves?.Select(c=>new EaseCurve{
                custom=c.Custom,a=new Vector2(c.First.X,c.First.Y),b=new Vector2(c.Second.X,c.Second.Y)}).ToArray()};
            if(!CurveSet.Valid(result))throw new InvalidDataException("補間の制御点が不正です。");
            return result;
        }

        static object FromKey(KeyEdit edit,MotionTrack track)
        {
            var k=edit.Value;
            if(k==null)return null;
            if(k.Channel!=ToKey(track.CurrentValue).Channel)throw new InvalidDataException("トラックとキーの種類が異なります。");
            CurveSet curves=ReadCurves(edit,track);
            object value;
            switch(k.Channel){
                case TrackChannel.Bone:value=new BonePose{Position=FromSdk(k.Position),Rotation=new Quaternion(k.Rotation.X,k.Rotation.Y,k.Rotation.Z,k.Rotation.W),PhysicsOff=k.PhysicsOff,Curves=curves};break;
                case TrackChannel.Camera:value=new CameraPose{Center=FromSdk(k.Position),Angles=FromSdk(k.Euler),Distance=k.Distance,ViewAngle=k.ViewAngle,Perspective=k.Perspective,Inactive=k.Disabled,Curves=curves};break;
                case TrackChannel.Morph:value=k.Weight;break;
                case TrackChannel.Accessory:value=new AccessoryPose{Position=FromSdk(k.Position),Rotation=FromSdk(k.Euler),Scale=k.Scale,Opacity=k.Opacity,Hidden=k.Hidden,Parent=k.ParentBone,Model=k.ParentActor,ParentId=k.ParentBoneId};break;
                case TrackChannel.Light:value=new LightPose{Direction=FromSdk(k.Direction),Color=new Color(k.Color.X,k.Color.Y,k.Color.Z,k.Color.W)};break;
                case TrackChannel.ModelSwitch:value=new SwitchPose{Hidden=k.Hidden,DisabledIK=k.DisabledIK?.ToArray()};break;
                case TrackChannel.Gravity:value=new GravityPose{Acceleration=k.Acceleration,Direction=FromSdk(k.Direction),Noisy=k.Noisy,NoiseAmount=k.Noise};break;
                case TrackChannel.Shadow:value=new ShadowPose{Mode=k.ShadowMode,Range=k.Range};break;
                default:throw new InvalidDataException("キーの種類が不正です。");
            }
            if(!ValidPose(value) || k.Channel==TrackChannel.Light && !float.IsFinite(k.Color.W))throw new InvalidDataException("キーの値が不正です。");
            if(value is float weight && CurveSet.Get(curves,0).custom)return new MorphKey{Weight=weight,Curve=curves.channels[0]};
            return value;
        }

        MotionSnapshot SnapshotMotion()
        {
            return new MotionSnapshot{Document=documentGeneration,Frame=frame,Selection=selectedTrack?.Id,
                DrawOrder=drawOrder.Select(a=>a.id).ToArray(),CalculationOrder=calculationOrder.Select(a=>a.id).ToArray(),
                Tracks=EditableTracks().Select(entry=>{
                    var track=entry.track;var current=ToKey(track.CurrentValue);
                    return new TrackSnapshot{Owner=entry.owner,Id=track.Id,Name=track.Name,Channel=current.Channel,Revision=track.Revision,
                        Current=current,Frames=track.Frames.ToArray(),Values=track.Frames.Select(f=>ToKey(track.KeyAt(f),track.CurveAt(f),track.SourceAt(f))).ToArray()};
                }).ToArray()};
        }
        KeyValue SampleMotion(string owner,string id,float sample)
        {
            if(!float.IsFinite(sample) || sample<0 || sample>MaxFrame)throw new ArgumentOutOfRangeException(nameof(sample));
            var track=EditableTracks().First(t=>t.owner==owner && t.track.Id==id).track;
            return ToKey(track.SampleValue(sample));
        }
        void ApplyMotionChanges(long document,KeyEdit[] edits)
        {
            if(document!=documentGeneration)throw new InvalidOperationException("プロジェクトが切り替わっています。");
            if(edits==null)throw new ArgumentNullException(nameof(edits));
            var tracks=EditableTracks().ToDictionary(t=>(t.owner,t.track.Id),t=>t.track);
            var changes=new Dictionary<(MotionTrack,int),KeyChange>();
            foreach(var edit in edits)
            {
                if(edit==null || !tracks.TryGetValue((edit.Owner,edit.Track),out var target) || target.Revision!=edit.Revision)
                    throw new InvalidOperationException("対象のトラックが変更されています。");
                if(edit.Frame<0 || edit.Frame>MaxFrame || edit.Frame==0 && edit.Value==null)throw new ArgumentOutOfRangeException(nameof(edits));
                var value=FromKey(edit,target);KeySource source=null;
                if(!Enum.IsDefined(typeof(CurveAction),edit.SourceMetadata))throw new InvalidDataException("入力情報の操作が不正です。");
                if(value!=null && edit.SourceMetadata==CurveAction.Replace){
                    var name=edit.Value.SourceName;var ik=edit.Value.SourceIK;
                    if(name!=null && (name.Length!=15 || !(target is MotionTrack<BonePose> || target is MotionTrack<float>)) ||
                        ik!=null && (ik.Length%21!=0 || ik.Length>21*2048 || !(target is MotionTrack<SwitchPose>)))throw new InvalidDataException("キーの元データが不正です。");
                    if(name!=null || ik!=null)source=new KeySource{name=name?.ToArray(),ik=ik?.ToArray()};
                }
                changes[(target,edit.Frame)]=new KeyChange{Track=target,Frame=edit.Frame,DocumentFrame=frame,After=value,AfterSource=source,ReplaceSource=edit.SourceMetadata!=CurveAction.Preserve};
            }
            CommitKeys(changes.Values);
        }
    }
}
