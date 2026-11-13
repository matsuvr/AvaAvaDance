using System;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class LimbIK
        {
            internal Transform Upper, Lower, Tip;
            internal BoneBinding Handle;
            internal Vector3 BendLocal;
        }

        enum PhysicsMode { OnOff, Always, Trace, None }





        PhysicsMode physicsMode;
        bool physicalFloor = true, forceOnPlayback, showBodies;



        DropdownField ikList;
        Toggle ikOn, ikOff;
        Button ikRegister, bonePhysicsButton;

        void BuildRigControls(VisualElement model)
        {
            ikList = Choice(model, new[] { "IKなし" });
            ikList.RegisterValueChangedCallback(_ => { if (modelMode && CurrentIK!=null) SelectTrack(CurrentIK.Handle.Track); else SyncRigControls(); });
            var row = Box(model, "ik-controls", "row");
            ikOn = new Toggle("ON"); ikOff = new Toggle("OFF");
            row.Add(ikOn); row.Add(ikOff);
            ikOn.RegisterValueChangedCallback(e => PreviewIK(e.newValue));
            ikOff.RegisterValueChangedCallback(e => PreviewIK(!e.newValue));
            ikRegister = Action(model, "登録", false, () => { if (actor.modelSwitch != null) RegisterKey(actor.modelSwitch); });
        }
        LimbIK CurrentIK => ikList.index >= 0 && ikList.index < actor.ikChains.Count ? actor.ikChains[ikList.index] : null;
        void PreviewIK(bool enabled)
        {
            if (CurrentIK == null) return;
            StopPlayback();
            if(ribbonProfile && !enabled && actor.modelSwitch.Current.Enabled(CurrentIK.Handle.Track.Name)){ReleaseIKToDraft(CurrentIK);SyncRigControls();return;}
            SetIKEnabled(CurrentIK,enabled);
            InvalidateDynamics(); RefreshRigPreview(); SyncRigControls();
        }
        void SetIKEnabled(LimbIK chain,bool enabled)
        {
            var state=actor.modelSwitch.Current;
            string name=chain.Handle.Track.Name;
            var disabled=new List<string>(state.DisabledIK??Array.Empty<string>());
            disabled.Remove(name);
            if(!enabled)disabled.Add(name);
            state.DisabledIK=disabled.ToArray();actor.modelSwitch.Preview(state);
        }
        void SyncRigControls()
        {
            if (ikList == null) return;
            if (actor.selectedBone != null) {
                int i = actor.ikChains.FindIndex(c => c.Handle == actor.selectedBone);
                if (i >= 0) ikList.SetValueWithoutNotify(actor.ikChains[i].Handle.Track.Name);
            }
            bool available = modelMode && CurrentIK != null;
            ikList.SetEnabled(modelMode && actor.ikChains.Count > 0);
            ikOn.SetEnabled(available); ikOff.SetEnabled(available); ikRegister.SetEnabled(modelMode && actor.modelSwitch!=null);
            ikOn.SetValueWithoutNotify(available && actor.modelSwitch.Current.Enabled(CurrentIK.Handle.Track.Name));
            ikOff.SetValueWithoutNotify(available && !actor.modelSwitch.Current.Enabled(CurrentIK.Handle.Track.Name));
            bool dynamic = actor.selectedBone != null && actor.dynamicLinks.Any(d => d.Bone == actor.selectedBone);
            bonePhysicsButton.SetEnabled(dynamic);
            bonePhysicsButton.EnableInClassList("active-tool", dynamic && !actor.selectedBone.Track.Current.PhysicsOff);
        }

        void BindRig(string meta,Dictionary<Transform,DynamicLink> dynamics)
        {
            var owner=actor;
            actor.modelSwitch=new MotionTrack<SwitchPose>("表示・IK",new SwitchPose(),(a,b,t)=>a,state=>{
                if(owner.avatar!=null && owner.avatar.activeSelf==state.Hidden)owner.avatar.SetActive(!state.Hidden);
                if(owner==actor)visibilityToggle.SetValueWithoutNotify(!state.Hidden);
            });
            actor.modelTracks.Insert(0,actor.modelSwitch);
            var mapped = AvaAvaDance.Import.HumanoidPreview.BoneMap(meta);
            Transform Find(string human, params string[] aliases) {
                if (actor.vrm != null && Enum.TryParse<HumanBodyBones>(human, out var id) && actor.humanBones.TryGetValue(id, out var joint)) return joint;
                if (mapped.TryGetValue(human,out var name)) aliases = new[] { name }.Concat(aliases).ToArray();
                foreach (string alias in aliases) {
                    var binding = actor.bones.Find(b => string.Equals(b.Joint.name, alias, StringComparison.OrdinalIgnoreCase));
                    if (binding != null) return binding.Joint;
                }
                return null;
            }
            foreach (bool left in new[] { true, false }) {
                string side = left ? "Left" : "Right", jp = left ? "左" : "右", lr = left ? "L" : "R";
                Transform hip = Find(side+"UpperLeg", side+"UpperLeg","UpperLeg_"+lr,"Upper_leg_"+lr,jp+"足");
                Transform knee = Find(side+"LowerLeg", side+"LowerLeg","LowerLeg_"+lr,"Lower_leg_"+lr,jp+"ひざ");
                Transform foot = Find(side+"Foot", side+"Foot","Foot_"+lr,jp+"足首");
                Transform toe = Find(side+"Toes", side+"Toes","Toe_"+lr,jp+"つま先");
                if (hip != null && knee != null && foot != null && knee.IsChildOf(hip) && foot.IsChildOf(knee)) {
                    var leg = AddIK(jp+"足ＩＫ", hip, knee, foot, actor.avatar.transform);
                    if (toe != null && toe.IsChildOf(foot)) AddIK(jp+"つま先ＩＫ", foot, null, toe, leg.Handle.Joint);
                }
            }
            foreach (var bone in actor.bones) {
                if(!dynamics.TryGetValue(bone.Joint,out var link))continue;
                link.Bone=bone;
                actor.authoredLinks.Add(bone.Joint,link);
                actor.dynamicLinks.Add(link);
            }
            RefreshRigLists();
        }

        LimbIK AddIK(string label, Transform upper, Transform lower, Transform tip, Transform parent)
        {
            string unique = label;
            for (int n=2; actor.modelTracks.Any(t=>t.Name==unique); n++) unique=label+" ("+n+")";
            var target = new GameObject(unique).transform;
            target.SetParent(parent,false); target.SetPositionAndRotation(tip.position,tip.rotation);
            Vector3 rest = target.localPosition; Quaternion rotation = target.localRotation;
            var track = new MotionTrack<BonePose>(unique,BonePose.Rest,BonePose.Blend,p=>{
                target.localPosition=rest+p.Position; target.localRotation=rotation*p.Rotation;
            });
            var binding = new BoneBinding { Joint=target, Track=track, RestRotation=rotation, EditingBasis=Quaternion.identity };
            var chain = new LimbIK { Upper=upper, Lower=lower, Tip=tip, Handle=binding };
            if (lower != null) {
                var bend = Vector3.ProjectOnPlane(lower.position-upper.position, (tip.position-upper.position).normalized);
                if (bend.sqrMagnitude < 1e-7f) bend = Vector3.ProjectOnPlane(front,(tip.position-upper.position).normalized);
                chain.BendLocal = upper.parent.InverseTransformDirection(bend.normalized);
            }
            actor.ikChains.Add(chain); actor.bones.Add(binding); actor.modelTracks.Add(track);
            return chain;
        }

        void RefreshRigLists()
        {
            ikList.choices = actor.ikChains.Select(c=>c.Handle.Track.Name).ToList();
            ikList.SetValueWithoutNotify(ikList.choices.FirstOrDefault() ?? "IKなし");
            boneList.choices = actor.bones.Select(b=>b.Track.Name).ToList();RefreshPropParents();
        }

        void AddSelectedIK(BoneBinding binding)
        {
            var tip = binding?.Joint;
            if (tip == null || !actor.bones.Any(b=>b.Joint==tip.parent) || !actor.bones.Any(b=>b.Joint==tip.parent.parent)
                || actor.customIK.Contains(binding.Track.Name) || actor.ikChains.Any(c=>c.Handle==binding || c.Tip==tip)) return;
            actor.customIK.Add(binding.Track.Name);
            var chain=AddIK(binding.Track.Name+"ＩＫ",tip.parent.parent,tip.parent,tip,actor.avatar.transform);
            RefreshRigLists(); SelectTrack(chain.Handle.Track); InvalidateDynamics();
        }

        void SolveIK()
        {
            ApplyPelvisControls();
            foreach (var c in actor.ikChains) {
                if (!actor.modelSwitch.Current.Enabled(c.Handle.Track.Name)) continue;
                Vector3 target = c.Handle.Joint.position;
                if (c.Lower == null) { Aim(c.Upper,c.Tip.position,target); continue; }
                var root=c.Upper.position;
                float a=Vector3.Distance(root,c.Lower.position), b=Vector3.Distance(c.Lower.position,c.Tip.position);
                if (a<1e-5f || b<1e-5f) continue;
                Vector3 dir=target-root;
                if (dir.sqrMagnitude<1e-10f) dir=c.Tip.position-root;
                dir.Normalize();
                float d=Mathf.Clamp(Vector3.Distance(root,target), Mathf.Abs(a-b)+1e-6f,a+b-1e-6f);
                float along=(a*a-b*b+d*d)/(2*d);
                Vector3 bend=Vector3.ProjectOnPlane(c.Upper.parent.TransformDirection(c.BendLocal),dir);
                if (bend.sqrMagnitude<1e-8f) bend=Vector3.ProjectOnPlane(c.Upper.right,dir);
                Vector3 elbow=root+along*dir+Mathf.Sqrt(Mathf.Max(0,a*a-along*along))*bend.normalized;
                Aim(c.Upper,c.Lower.position,elbow); Aim(c.Lower,c.Tip.position,root+d*dir);
                c.Tip.rotation=c.Handle.Joint.rotation;
            }
        }
        static void Aim(Transform joint, Vector3 endpoint, Vector3 target)
        {
            Vector3 a=endpoint-joint.position,b=target-joint.position;
            if (a.sqrMagnitude>1e-12f && b.sqrMagnitude>1e-12f) joint.rotation=Quaternion.FromToRotation(a,b)*joint.rotation;
        }

        void AddDynamic(BoneBinding bone)
        {
            if (actor.dynamicLinks.Any(d=>d.Bone==bone) || actor.ikChains.Any(c=>c.Handle==bone)) return;
            if(!actor.authoredLinks.TryGetValue(bone.Joint,out var link)) {
                var tip = actor.bones.FirstOrDefault(b=>b.Joint.parent==bone.Joint && (b.Joint.position-bone.Joint.position).sqrMagnitude>1e-8f);
                if (tip==null) return;
                link=new DynamicLink { Bone=bone, Children=new[]{tip.Joint}, Reference=bone.Joint.parent,
                    Pull=.35f,Spring=.25f,Gravity=.15f,Immobile=.6f,Limit=1,Pitch=20,Animated=true };
            }
            actor.dynamicLinks.Add(link);
            actor.dynamicLinks.Sort((a,b)=>actor.bones.IndexOf(a.Bone).CompareTo(actor.bones.IndexOf(b.Bone)));
        }

        static void ClearSimulation(Actor item)
        {
            item.simTick=-1;item.simulationCache.Clear();foreach(var link in item.dynamicLinks)link.State=default;
        }
        void InvalidateDynamics(){ClearBullet();ClearSimulation(actor);}
        List<Actor> InvalidateChangedActors(IEnumerable<KeyChange> changes)
        {
            var tracks=new HashSet<MotionTrack>(changes.Select(c=>c.Track));
            var affected=drawOrder.Where(a=>a.modelTracks.Any(tracks.Contains) || tracks.Contains(gravityTrack)).ToList();
            ClearBullet();foreach(var item in affected)ClearSimulation(item);
            return affected;
        }
        void RestoreSimulation(int tick)
        {
            int index=actor.simulationCache.Count-1;
            while(index>=0 && actor.simulationCache.Keys[index]>tick)index--;
            if(index<0) {
                if(actor.simTick>tick || actor.simTick<0) {actor.simTick=-1;foreach(var d in actor.dynamicLinks)d.State=default;}
                return;
            }
            int at=actor.simulationCache.Keys[index];
            if(actor.simTick<=tick && actor.simTick>=at)return;
            var particles=actor.simulationCache.Values[index];actor.simTick=at;
            for(int i=0;i<actor.dynamicLinks.Count;i++) {
                actor.dynamicLinks[i].State=particles[i];
            }
        }
        void CacheSimulation()
        {
            if(actor.simTick%60!=0 || actor.simulationCache.ContainsKey(actor.simTick))return;
            if(actor.simulationCache.Count>=120)actor.simulationCache.RemoveAt(0);
            var particles=new ParticleState[actor.dynamicLinks.Count];
            for(int i=0;i<particles.Length;i++)particles[i]=actor.dynamicLinks[i].State;
            actor.simulationCache.Add(actor.simTick,particles);
        }
        bool UseSimulation(bool running) => actor.dynamicLinks.Count>0 && (physicsMode!=PhysicsMode.None || (running && forceOnPlayback))
            && (running || physicsMode==PhysicsMode.Always || physicsMode==PhysicsMode.Trace);

        void RefreshRigPreview()
        {
            foreach (var b in actor.bones) b.Track.Preview(b.Track.Current);
            SolveIK();ApplyLinks(); RedrawOverlay();
        }

        void EvaluateAnimation(float time, bool running)
        {
            evaluationTime=time;
            SyncVideo(time);
            if(physicsEngine==PhysicsEngine.Bullet)EvaluateBullet(time,running);
            else ForEachActor(calculationOrder,()=>EvaluateActor(time,running));
            ApplyLinks();foreach(var track in sceneTracks)track.Seek(time);
            EmitEvaluation(EvaluationStage.AfterSceneAndAccessories,time);
            visibilityToggle.SetValueWithoutNotify(actor.avatar!=null && actor.avatar.activeSelf);
        }
        void EvaluateActor(float time,bool running)
        {
            bool simulate=UseSimulation(running);
            int tick=Mathf.FloorToInt(time*2 + .00001f);
            if (simulate) {
                RestoreSimulation(tick);
                while (actor.simTick<tick) {
                    actor.simTick++;
                    float sample=actor.simTick*.5f;
                    EmitEvaluation(EvaluationStage.BeforeTrackSample,sample,true,actor.id);
                    foreach (var b in actor.bones) b.Track.Seek(sample);
                    actor.modelSwitch?.Seek(sample);
                    gravityTrack.Seek(sample);EmitEvaluation(EvaluationStage.AfterTrackSampleBeforeIK,sample,true,actor.id);SolveIK();
                    EmitEvaluation(EvaluationStage.AfterIKBeforePhysics,sample,true,actor.id);StepDynamics(actor.simTick,true);
                    EmitEvaluation(EvaluationStage.AfterPhysics,sample,true,actor.id);CacheSimulation();
                }
            } else InvalidateDynamics();
            EmitEvaluation(EvaluationStage.BeforeTrackSample,time,false,actor.id);gravityTrack.Seek(time);
            foreach (var track in actor.modelTracks) track.Seek(time);
            EmitEvaluation(EvaluationStage.AfterTrackSampleBeforeIK,time,false,actor.id);SolveIK();
            EmitEvaluation(EvaluationStage.AfterIKBeforePhysics,time,false,actor.id);
            if (simulate) StepDynamics(tick,false);
            EmitEvaluation(EvaluationStage.AfterPhysics,time,false,actor.id);
        }

        void IdleDynamics()
        {
            if (physicsMode!=PhysicsMode.Always || actor.dynamicLinks.Count==0 || modal!=null || exporting) return;
            actor.idleRemainder+=Mathf.Min(Time.unscaledDeltaTime,.1f);
            while(actor.idleRemainder>=1f/60) {
                actor.idleRemainder-=1f/60; RefreshRigPreview(); StepDynamics(++actor.idleTick,true); actor.simTick=-1;
            }
        }

        void ToggleDynamicBone(BoneBinding b)
        {
            if (b==null) return;
            if (actor.dynamicLinks.RemoveAll(d=>d.Bone==b)>0) {
                actor.extraDynamic.Remove(b.Track.Name); if(!actor.excludedDynamic.Contains(b.Track.Name))actor.excludedDynamic.Add(b.Track.Name);
            } else {
                AddDynamic(b); actor.excludedDynamic.Remove(b.Track.Name);
                if(actor.dynamicLinks.Any(d=>d.Bone==b) && !actor.extraDynamic.Contains(b.Track.Name)) actor.extraDynamic.Add(b.Track.Name);
            }
            InvalidateDynamics(); RefreshRigPreview(); SyncRigControls();
        }

        void SelectPhysicsKeys(bool onlyEnabled)
        {
            if(actor.avatar==null)return;
            ChangeMode(true);
            if(actor.dynamicLinks.Count>0)SelectTrack(actor.dynamicLinks[0].Bone.Track);
            selectedKeys.Clear();rangeSelected=true;
            foreach(var link in actor.dynamicLinks) {
                foreach(int key in link.Bone.Track.Frames)
                    if(!onlyEnabled || !((BonePose)link.Bone.Track.KeyAt(key)).PhysicsOff)selectedKeys.Add((link.Bone.Track,key));
            }
            timeline.MarkDirtyRepaint();RefreshKeyButtons();
        }

        void ConvertPhysicsKeys()
        {
            var edits=new List<KeyChange>();
            foreach(var key in EditKeys()) {
                if(!actor.dynamicLinks.Any(d=>d.Bone.Track==key.track) || !(key.track.KeyAt(key.frame) is BonePose p))continue;
                p.PhysicsOff=!p.PhysicsOff;
                edits.Add(new KeyChange{Track=key.track,Frame=key.frame,After=p});
            }
            CommitKeys(edits);
        }

        void DrawBodies(Painter2D p)
        {
            if(!showBodies || actor.avatar==null || !actor.avatar.activeSelf)return;
            bool Point(Vector3 world,out Vector2 screen) {
                var v=previewCamera.WorldToViewportPoint(world);
                screen=new Vector2(v.x*overlay.contentRect.width,(1-v.y)*overlay.contentRect.height);
                return v.z>previewCamera.nearClipPlane;
            }
            void Body(Vector3 a,Vector3 b,float radius,Color color) {
                if(!Point(a,out var start) || !Point(b,out var end) || !Point(a+previewCamera.transform.right*radius,out var edge))return;
                float r=Mathf.Max(1,(edge-start).magnitude);
                Vector2 normal=Vector2.Perpendicular((end-start).normalized)*r;
                Line(p,start+normal,end+normal,color);Line(p,start-normal,end-normal,color);
                p.BeginPath();p.Arc(start,r,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
                p.BeginPath();p.Arc(end,r,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
            }
            foreach(var c in actor.dynamicLinks.SelectMany(link=>link.Colliders).Distinct()) {
                ColliderGeometry(c,out var a,out var b,out float radius);
                Body(a,b,radius,new Color(.8f,.35f,.1f,.65f));
            }
            var color=new Color(.2f,.7f,.3f,.8f);
            foreach(var link in actor.dynamicLinks) {
                if(physicsEngine!=PhysicsEngine.Bullet) {Body(link.Bone.Joint.position,link.TipPosition,link.WorldRadius,color);continue;}
                var joint=link.Bone.Joint;var delta=link.TipPosition-joint.position;
                float length=delta.magnitude;if(length<.00001f)continue;
                var b=bulletLinks.Find(item=>item.Link==link);
                var settings=b?.Settings??actor.modelOverrides.bullet.Find(item=>item.name==link.Bone.Track.Name);
                var orientation=b?.Solved.Rotation??(Quaternion.FromToRotation(joint.up,delta/length)*joint.rotation);
                var center=b?.Solved.Position??(joint.position+delta*.5f);
                float r=b?.Size.x??Mathf.Max(.002f,link.WorldRadius>0?link.WorldRadius:length*.12f);
                int shape=settings?.shape??2;
                if(shape==1) {
                    Vector3 Corner(int i)=>center+orientation*new Vector3((i&1)==0?-r:r,(i&2)==0?-length*.5f:length*.5f,(i&4)==0?-r:r);
                    for(int i=0;i<8;i++)for(int bit=1;bit<=4;bit*=2) {
                        if((i&bit)==0 && Point(Corner(i),out var a) && Point(Corner(i|bit),out var z))Line(p,a,z,color);
                    }
                } else {
                    var axis=shape==0?Vector3.zero:orientation*Vector3.up*Mathf.Max(0,length*.5f-r);
                    Body(center-axis,center+axis,r,color);
                }
            }
        }

        void ClearRig()
        {
            actor.modelSwitch=null;actor.ikChains.Clear(); actor.dynamicLinks.Clear(); actor.authoredLinks.Clear(); actor.customIK.Clear(); actor.extraDynamic.Clear(); actor.excludedDynamic.Clear();
            InvalidateDynamics(); actor.idleTick=0; actor.idleRemainder=0;
        }
    }
}
