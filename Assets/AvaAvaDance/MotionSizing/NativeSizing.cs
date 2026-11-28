using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionSizing
{
    public sealed class NativeSizingOptions
    {
        public float MovementScale=1,FootOffset=0,FloorHeight=0,ContactDistance=.05f;
        public bool MatchFeet=true,AlignHands=true,AlignFingers=false,AvoidBody=false,Twist=true,Camera=false;
        public string MorphRules;
    }
    public sealed class NativeSizingRequest
    {
        public string SourceOwner,TargetOwner;
        public int FirstFrame=0,LastFrame=-1;
        public Matrix4x4 SourceToScene=Matrix4x4.Identity,TargetToScene=Matrix4x4.Identity;
        public NativeSizingOptions Options=new NativeSizingOptions();
    }
    public sealed class NativeSizingResult
    {
        public long Document;
        public KeyEdit[] Edits;
        public string[] Messages;
    }
    public static partial class NativeMotionSizing
    {
        sealed class Output
        {
            internal readonly Timeline Timeline;
            internal readonly SortedDictionary<int,KeyValue> Values=new SortedDictionary<int,KeyValue>();
            internal bool Bake,Changed,Step;
            internal Output(Timeline timeline){Timeline=timeline;}
            internal void Add(int frame,KeyValue value,bool bake=false)
            {
                Bake|=bake;
                if(!EqualValue(Timeline.Sample(frame),value)){Values[frame]=Copy(value);Changed=true;}
            }
        }
        static bool Movement(string role,string name)
        {
            switch(Role(role)) {case "hips":case "root":case "center":case "groove":case "leftfootik":case "rightfootik":case "lefttoesik":case "righttoesik":return true;}
            return name=="全ての親" || name=="センター" || name=="グルーブ";
        }
        static Vector3 WorldDelta(Rig rig,int joint,Vector3 delta)
        {
            int p=rig.Joints[joint].parent;return p<0?delta:Vector3.Transform(delta*Scale(rig.Joints[p]),Basis(rig.Joints[p]));
        }
        static Vector3 LocalDelta(Rig rig,int joint,Vector3 delta)
        {
            int p=rig.Joints[joint].parent;return p<0?delta:Divide(Vector3.Transform(delta,Quaternion.Inverse(Basis(rig.Joints[p]))),Scale(rig.Joints[p]));
        }
        static int Depth(Rig rig,int joint)
        {
            int depth=0;for(;joint>=0;joint=rig.Joints[joint].parent)if(++depth>rig.Joints.Length)throw new ArgumentException("リグの親子関係が循環しています。");return depth;
        }

        /// <summary>
        /// Sizes editable native base tracks. Input keys already belong to SourceOwner's
        /// editing rig, regardless of the format from which they were imported.
        /// The caller applies the returned edits in one editor/undo transaction.
        /// </summary>
        public static NativeSizingResult Calculate(MotionSnapshot snapshot,RigProfile source,RigProfile target,NativeSizingRequest request,CancellationToken stop) =>
            Calculate(snapshot, source, target, request, stop, null);

        public static NativeSizingResult Calculate(MotionSnapshot snapshot, RigProfile source, RigProfile target,
            NativeSizingRequest request, CancellationToken stop, TrackSnapshot[] fileTracks)
        {
            stop.ThrowIfCancellationRequested();
            if(snapshot?.Tracks==null || request==null)throw new ArgumentNullException(snapshot==null?nameof(snapshot):nameof(request));
            if(string.IsNullOrEmpty(request.SourceOwner) || string.IsNullOrEmpty(request.TargetOwner))throw new ArgumentException("元と適用先のモデルを選択してください。");
            var options=request.Options??new NativeSizingOptions();
            if(!float.IsFinite(options.MovementScale) || options.MovementScale<0 || !float.IsFinite(options.FootOffset) ||
                !float.IsFinite(options.FloorHeight) || !float.IsFinite(options.ContactDistance) || options.ContactDistance<0)
                throw new ArgumentException("移動倍率・接触距離は0以上、各補正値は有限の数値で指定してください。");
            if(request.FirstFrame<0 || request.LastFrame< -1)throw new ArgumentException("フレーム範囲が不正です。");
            var timelines=snapshot.Tracks.Select(t=>new Timeline(t)).ToArray();
            var sourceTracks = fileTracks == null ? timelines.Where(t=>t.Track.Owner==request.SourceOwner).ToArray()
                : fileTracks.Select(track => new Timeline(track)).ToArray();
            var targetTracks=timelines.Where(t=>t.Track.Owner==request.TargetOwner).ToArray();
            if(sourceTracks.Length==0 || targetTracks.Length==0)throw new ArgumentException("モデルのモーショントラックがありません。");
            var a=new Rig(source,sourceTracks);var b=new Rig(target,targetTracks);bool same=request.SourceOwner==request.TargetOwner;
            int sourceEnd=sourceTracks.Max(t=>t.End),targetEnd=targetTracks.Max(t=>t.End);
            int first=request.FirstFrame,last=request.LastFrame<0?sourceEnd:request.LastFrame;
            if(last<first)throw new ArgumentException("終了フレームは開始フレーム以降を指定してください。");
            const int lastEditableFrame=600000;
            if(last>lastEditableFrame)throw new ArgumentException("アプリの最終フレーム（600000）を超えています。");
            float ratio=same?1:b.LegLength()/a.LegLength();
            var facing=same?Quaternion.Identity:Unit(b.Facing()*Quaternion.Inverse(a.Facing()));
            var mapping=new Dictionary<int,int>();
            // Two instances of one asset can contain unnamed or identically named bones.
            bool sameIndices = same || !string.IsNullOrEmpty(source.sourceHash) && source.sourceHash == target.sourceHash &&
                a.Joints.Select(j => (j.parent,j.name,j.role)).SequenceEqual(b.Joints.Select(j => (j.parent,j.name,j.role)));
            for (int i = 0; i < a.Joints.Length; i++) {
                if (a.Tracks[i] == null) continue;
                int dest = sameIndices ? i : b.Match(a, i);
                if (dest >= 0 && b.Tracks[dest] != null && !mapping.ContainsKey(dest)) mapping.Add(dest, i);
            }
            if(mapping.Count==0)throw new ArgumentException("対応する編集可能ボーンがありません。");
            var ordered=mapping.OrderBy(pair=>Depth(b,pair.Key)).ToArray();
            var mixedMovement=new HashSet<int>();
            bool Mixed(Func<Vector3,Vector3> transform){
                var x=transform(Vector3.UnitX);var y=transform(Vector3.UnitY);var z=transform(Vector3.UnitZ);
                return Math.Abs(x.Y)+Math.Abs(x.Z)+Math.Abs(y.X)+Math.Abs(y.Z)+Math.Abs(z.X)+Math.Abs(z.Y)>1e-6f;
            }
            if(same && options.MovementScale!=1)foreach(var pair in ordered){
                int src=pair.Value;if(!Movement(a.Joints[src].role??a.Tracks[src].Track.Role,a.Joints[src].name) && a.Joints[src].parent>=0)continue;
                if(Mixed(v=>LocalDelta(b,pair.Key,WorldDelta(a,src,v)*new Vector3(options.MovementScale,1,options.MovementScale))))mixedMovement.Add(pair.Key);
            }
            var output=new Dictionary<Timeline,Output>();
            Output For(Timeline t){if(!output.TryGetValue(t,out var result))output.Add(t,result=new Output(t));return result;}
            var messages=new List<string>();
            var feet=new List<(int au,int al,int af,int bu,int bl,int bf)>();
            if(options.MatchFeet || options.FootOffset!=0)foreach(string side in new[]{"left","right"}){
                int au=a.Find(side+"UpperLeg"),al=a.Find(side+"LowerLeg"),af=a.Find(side+"Foot"),bu=b.Find(side+"UpperLeg"),bl=b.Find(side+"LowerLeg"),bf=b.Find(side+"Foot");
                if(a.Chain(au,al,af) && b.Chain(bu,bl,bf))feet.Add((au,al,af,bu,bl,bf));
                else messages.Add((side=="left"?"左":"右")+"脚のFK補正は、太もも・ひざ・足首の対応トラックがないため省略しました。");
            }
            var morphs=MorphMapping(sourceTracks,targetTracks,options.MorphRules,same,messages,fileTracks != null);
            // A saved input and the live destination can have different key times even on the same rig.
            var fileSwitch = same && fileTracks != null ? sourceTracks.FirstOrDefault(t=>t.Track.Channel==TrackChannel.ModelSwitch) : null;
            var targetSwitch = targetTracks.FirstOrDefault(t=>t.Track.Channel==TrackChannel.ModelSwitch);
            var obstacles=options.AvoidBody?BodyShapes(b):Array.Empty<BodyShape>();
            int limitedFeet=0;
            for(int f=first;f<=last;f++)
            {
                stop.ThrowIfCancellationRequested();
                var before=new Pose(a,f);var after=new Pose(b,f);
                if(fileSwitch!=null && targetSwitch!=null)For(targetSwitch).Add(f,fileSwitch.Sample(f),true);
                foreach(var pair in a.Controls)if(b.Controls.TryGetValue(pair.Key,out var destination)){
                    var value=Copy(before.Controls[pair.Key]);
                    bool mixed=false;
                    if(Movement(pair.Key,pair.Value.Track.Name)){
                        var sourceRoot=a.Joints.First(j=>j.parent<0);var targetRoot=b.Joints.First(j=>j.parent<0);
                        var world=Vector3.Transform(value.Position*Scale(sourceRoot),Basis(sourceRoot));
                        world=Vector3.Transform(world,facing);
                        world*=new Vector3(ratio*options.MovementScale,ratio,ratio*options.MovementScale);
                        value.Position=Divide(Vector3.Transform(world,Quaternion.Inverse(Basis(targetRoot))),Scale(targetRoot));
                        if(!same)value.Rotation=Unit(Quaternion.Inverse(Basis(targetRoot))*facing*Basis(sourceRoot)*value.Rotation*Quaternion.Inverse(Basis(sourceRoot))*Quaternion.Inverse(facing)*Basis(targetRoot));
                        else if(options.MovementScale!=1)mixed=Mixed(v=>Divide(Vector3.Transform(Vector3.Transform(v*Scale(sourceRoot),Basis(sourceRoot))*new Vector3(options.MovementScale,1,options.MovementScale),Quaternion.Inverse(Basis(targetRoot))),Scale(targetRoot)));
                    }
                    after.Controls[pair.Key]=value;For(destination).Add(f,value,!same || mixed);
                }
                foreach(var pair in ordered){
                    int dst=pair.Key,src=pair.Value;var key=Copy(before.Values[src]);
                    bool movement=a.Joints[src].parent<0 || Movement(a.Joints[src].role??a.Tracks[src].Track.Role,a.Joints[src].name);
                    if(!same || movement && options.MovementScale!=1){
                        var delta=WorldDelta(a,src,key.Position);
                        delta=Vector3.Transform(delta,facing);
                        if(movement)delta*=new Vector3(ratio*options.MovementScale,ratio,ratio*options.MovementScale);
                        key.Position=LocalDelta(b,dst,delta);
                    }
                    after.Values[dst]=key;after.Invalidate();
                    if(!same){var desired=facing*before.World(src).q*Quaternion.Inverse(a.Calibration(src))*Quaternion.Inverse(facing)*b.Calibration(dst);after.SetWorldRotation(dst,desired);}
                }
                var transformed=after.Values.Select(Copy).ToArray();
                foreach(var foot in feet){
                    var sourceFoot=before.World(foot.af);var targetFoot=after.World(foot.bf);
                    int sourceHips=a.Find("hips"),targetHips=b.Find("hips");
                    Vector3 goal=targetFoot.p;
                    if(!same && sourceHips>=0 && targetHips>=0)
                        goal=after.World(targetHips).p+Vector3.Transform(sourceFoot.p-before.World(sourceHips).p,facing)*ratio;
                    string side=Role(a.Joints[foot.af].role).StartsWith("right",StringComparison.Ordinal)?"right":"left";
                    if(options.MatchFeet){
                        float clearance=Support(before,foot.af,side).Y;
                        if(clearance<=options.ContactDistance){
                            // Preserve authored clearance: floor adjustment does not flatten a toe/heel gesture.
                            float offset=Support(after,foot.bf,side).Y-targetFoot.p.Y;
                            goal.Y=options.FloorHeight+Math.Max(0,clearance)*ratio-offset;
                        }
                    }
                    if(options.FootOffset!=0){
                        int other=b.Find((side=="left"?"right":"left")+"Foot");
                        var lateral=other>=0?Point(b.Joints[foot.bf])-Point(b.Joints[other]):(side=="left"?Vector3.UnitX:-Vector3.UnitX);
                        lateral.Y=0;if(lateral.LengthSquared()>1e-12f)goal+=Vector3.Normalize(lateral)*options.FootOffset;
                    }
                    if(!Near(goal,targetFoot.p) && !after.Solve(foot.bu,foot.bl,foot.bf,goal))limitedFeet++;
                }
                if(options.AlignHands || options.AlignFingers)AlignNativeHands(before,after,options,facing);
                if(options.AvoidBody)AvoidNativeBody(after,obstacles,options.ContactDistance);
                if(options.Twist)DistributeNativeTwist(after);
                for(int i=0;i<b.Tracks.Length;i++)if(b.Tracks[i]!=null){
                    bool corrected=!EqualValue(transformed[i],after.Values[i]);
                    if(mapping.ContainsKey(i) || corrected)For(b.Tracks[i]).Add(f,after.Values[i],!same || corrected || mixedMovement.Contains(i));
                }
                foreach(var morph in morphs){
                    var value=morph.target.Sample(f);value.Weight=0;
                    foreach(var term in morph.terms)value.Weight+=term.track.Sample(f).Weight*term.factor;
                    if(morph.terms.Count==1){var sourceKey=morph.terms[0].track.Sample(f);value.Curves=sourceKey.Curves;}
                    For(morph.target).Add(f,value,morph.terms.Count!=1 || !same);
                }
                if(options.Camera)foreach(var camera in timelines.Where(t=>t.Track.Channel==TrackChannel.Camera)){
                    var value=camera.Sample(f);if(!value.Disabled){SizeNativeCamera(before,after,value,request.SourceToScene,request.TargetToScene);For(camera).Add(f,value,true);}
                }
                if(f==int.MaxValue)break;
            }
            if(!same)DisableTargetLegIk(targetTracks,b,mapping,first,last,For);
            var edits=new Dictionary<(string owner,string track,int frame),KeyEdit>();
            void Add(Timeline t,int f,KeyValue value){
                if(t.Inactive(f))return;
                edits[(t.Track.Owner,t.Track.Id,f)]=new KeyEdit{Owner=t.Track.Owner,Track=t.Track.Id,Revision=t.Track.Revision,Frame=f,Value=Copy(value),Curves=CurveAction.Replace};
            }
            foreach(var item in output.Values.Where(o=>o.Changed)){
                stop.ThrowIfCancellationRequested();var timeline=item.Timeline;
                bool bake=!item.Step && (fileTracks != null || item.Bake || !timeline.Has(first) || !timeline.Has(last));
                IEnumerable<int> frames=bake?Enumerable.Range(first,last-first+1):timeline.Frames.Where(f=>f>=first && f<=last).Append(first).Append(last).Distinct().OrderBy(f=>f);
                foreach(int f in frames){
                    var key=item.Values.TryGetValue(f,out var value)?Copy(value):timeline.Sample(f);if(bake)Linear(key);
                    Add(timeline,f,key);
                }
                if(first>0){int boundary=first-1;while(boundary>0 && timeline.Inactive(boundary))boundary--;timeline.SplitAt(boundary,out var guard,out _,out _);Add(timeline,boundary,guard);}
                if(last<lastEditableFrame && (Math.Max(sourceEnd,targetEnd)>last || timeline.End>last)){
                    int boundary=last+1;while(boundary<lastEditableFrame && timeline.Inactive(boundary))boundary++;
                    timeline.SplitAt(boundary,out var guard,out int next,out var continuation);
                    Linear(guard);Add(timeline,boundary,guard);
                    if(next>boundary && continuation!=null)Add(timeline,next,continuation);
                }
            }
            if(limitedFeet>0)messages.Add("脚の長さを超える接地点は、届く範囲へ補正しました（"+limitedFeet+"箇所）。");
            messages.Add(edits.Count==0?"変更はありません。":first+"～"+last+"フレーム、"+edits.Count+"キーを内部モーションへ適用します。");
            stop.ThrowIfCancellationRequested();
            return new NativeSizingResult{Document=snapshot.Document,Edits=edits.Values.OrderBy(e=>e.Owner,StringComparer.Ordinal).ThenBy(e=>e.Track,StringComparer.Ordinal).ThenBy(e=>e.Frame).ToArray(),Messages=messages.Distinct().ToArray()};
        }
        static Vector3 Support(Pose pose,int foot,string side)
        {
            int sole=pose.Rig.Find(side+"Sole");
            // Without a mesh-derived sole marker, use the rest ground below the foot.
            // It follows the ankle orientation, retaining toe/heel lifts.
            var rest=pose.Rig.Joints[foot];var w=pose.World(foot);
            var local=Vector3.Transform(new Vector3(0,-(float)rest.y,0),Quaternion.Inverse(Basis(rest)));
            var p=sole>=0?pose.World(sole).p:w.p+Vector3.Transform(local,w.q);
            int toeTip=pose.Rig.Find(side+"ToeTip"),toe=pose.Rig.Find(side+"Toes");
            if(toeTip>=0){var tip=pose.World(toeTip).p;if(tip.Y<p.Y)p=tip;}
            else if(toe>=0){var t=pose.World(toe);var tr=pose.Rig.Joints[toe];var offset=Vector3.Transform(new Vector3(0,-(float)tr.y,0),Quaternion.Inverse(Basis(tr)));
                var tip=sole>=0?t.p:t.p+Vector3.Transform(offset,t.q);if(tip.Y<p.Y)p=tip;}
            return p;
        }
        static void DisableTargetLegIk(Timeline[] tracks,Rig target,Dictionary<int,int> mapping,int first,int last,Func<Timeline,Output> output)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(string side in new[]{"left","right"}){
                int thigh=target.Find(side+"UpperLeg"),knee=target.Find(side+"LowerLeg");if(!mapping.ContainsKey(thigh) || !mapping.ContainsKey(knee))continue;
                foreach(string type in new[]{"footik","toesik"}){
                    if(target.Controls.TryGetValue(side+type,out var control))names.Add(control.Track.Name);
                    int index=target.Find(side+type);if(index>=0 && target.Tracks[index]!=null)names.Add(target.Tracks[index].Track.Name);
                }
            }
            if(names.Count==0)return;
            foreach(var track in tracks.Where(t=>t.Track.Channel==TrackChannel.ModelSwitch))
                foreach(int f in track.Frames.Where(f=>f>=first && f<=last).Append(first).Append(last).Distinct()){
                    var value=track.Sample(f);value.DisabledIK=(value.DisabledIK??Array.Empty<string>()).Union(names).ToArray();var result=output(track);result.Step=true;result.Add(f,value);
                }
        }
    }
}
