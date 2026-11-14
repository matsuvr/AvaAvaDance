using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Ava.Extensions;
using UnityEngine.UIElements;
using PB=AvaAvaDance.Import.PrefabPhysBones;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        enum PhysicsEngine { PhysBone, Bullet }
        PhysicsEngine physicsEngine=PhysicsEngine.Bullet;

        [Serializable] sealed class BulletSetting
        {
            public string name;
            // Generated hair and skirt segments frequently overlap in the bind pose.
            // Group 2 collides with the floor and the rig's authored colliders (group 1).
            public int shape=2, mode=2, group=1, mask=65533;
            public float mass=1, friction=.5f, restitution, linearDamping=.15f, angularDamping=.35f;
            public bool free;
            public bool Valid => !string.IsNullOrEmpty(name) && shape>=0 && shape<=2 && mode>=0 && mode<=2 &&
                group>=0 && group<16 && mask>=0 && mask<=65535 && mass>=.001f && mass<=1000 &&
                friction>=0 && friction<=2 && restitution>=0 && restitution<=1 &&
                linearDamping>=0 && linearDamping<=1 && angularDamping>=0 && angularDamping<=1;
        }

        sealed class BulletLink
        {
            internal DynamicLink Link;
            internal int Id, Anchor=-1;
            internal Vector3 LocalCenter,Size;
            internal Quaternion LocalRotation;
            internal Quaternion LocalSwing=Quaternion.identity;
            internal BulletSetting Settings;
            internal Transform MotionReference;
            internal BulletWorld.Pose PreviousReference;
            internal BulletWorld.Pose Solved;
            internal Transform Joint => Link.Bone.Joint;
            internal BulletWorld.Pose Target => new BulletWorld.Pose(Joint.TransformPoint(LocalCenter),Joint.rotation*LocalRotation);
            internal BulletWorld.Pose ReferencePose => new BulletWorld.Pose(MotionReference.position,
                Link.WorldImmobile ? Quaternion.identity : MotionReference.rotation);

            internal void FollowReference(BulletWorld world)
            {
                var current=ReferencePose;
                var turn=Quaternion.Slerp(Quaternion.identity,current.Rotation*Quaternion.Inverse(PreviousReference.Rotation),Link.Immobile);
                var origin=Vector3.Lerp(PreviousReference.Position,current.Position,Link.Immobile);
                world.Transport(Id,Anchor,new BulletWorld.Pose(origin-turn*PreviousReference.Position,turn));
                PreviousReference=current;
            }
        }
        BulletWorld bullet;
        readonly List<BulletLink> bulletLinks=new List<BulletLink>();
        readonly Dictionary<PB.Collider,int> bulletColliders=new Dictionary<PB.Collider,int>();
        int bulletTick=-1;
        double bulletRemainder;
        bool idleBullet;
        void ClearBullet()
        {
            bullet?.Dispose();bullet=null;bulletLinks.Clear();bulletColliders.Clear();
            bulletTick=-1;bulletRemainder=0;idleBullet=false;
        }

        static BulletWorld.Pose ColliderPose(PB.Collider c)
        {
            ColliderGeometry(c,out var a,out var b,out _);
            var up=c.Anchor.TransformVector(c.Up).normalized;
            if(c.Shape==2 && c.Inside)up=-up;
            return new BulletWorld.Pose((a+b)*.5f,Quaternion.FromToRotation(Vector3.up,up));
        }

        void BuildBullet()
        {
            bullet=new BulletWorld();
            try {
                if(physicalFloor)bullet.Add(new BulletWorld.Body {Shape=3,Pose=new BulletWorld.Pose(Vector3.down,Quaternion.identity),
                    Friction=.75f,Group=1,Mask=65535});
                foreach(var model in calculationOrder)AddBulletActor(model);
            } catch {ClearBullet();throw;}
        }

        void AddBulletActor(Actor model)
        {
            var byJoint=new Dictionary<Transform,int>();
            int first=bulletLinks.Count;
            foreach(var link in model.dynamicLinks) {
                var joint=link.Bone.Joint;
                var settings=model.modelOverrides.bullet.Find(s=>s.name==link.Bone.Track.Name)??new BulletSetting {name=link.Bone.Track.Name};
                bool aligned=settings.mode==2 && !settings.free && model.authoredLinks.ContainsKey(joint);
                if(aligned && !link.Animated && !link.Bone.Track.Current.PhysicsOff)joint.localRotation=link.BindRotation;
                Vector3 delta=link.TipPosition-joint.position;
                float length=delta.magnitude;
                if(length<.00001f)continue;
                float radius=Mathf.Max(.002f,link.WorldRadius>0?link.WorldRadius:length*.12f);
                // Y is the long axis. Keep the bind rotation about Y from the authored bone.
                Quaternion rotation=Quaternion.FromToRotation(joint.up,delta/length)*joint.rotation;
                Vector3 center=joint.position+delta*.5f;
                var pose=new BulletWorld.Pose(center,rotation);
                var size=settings.shape==1?new Vector3(radius,length*.5f,radius):
                    new Vector3(radius,Mathf.Max(0,length-2*radius),0);
                int id=bullet.Add(new BulletWorld.Body {Pose=pose,Size=size,Shape=settings.shape,Mass=settings.mode==0?0:settings.mass,
                    LinearDamping=settings.linearDamping,AngularDamping=settings.angularDamping,Friction=settings.friction,
                    Restitution=settings.restitution,GravityScale=link.Gravity,Group=1<<settings.group,Mask=settings.mask});
                var body=new BulletLink {Id=id,Link=link,Size=size,LocalCenter=joint.InverseTransformPoint(center),Settings=settings,
                    LocalRotation=Quaternion.Inverse(joint.rotation)*rotation,Solved=pose};
                if(aligned) {
                    body.MotionReference=link.WorldImmobile ? model.avatar.transform : link.Reference;
                    body.PreviousReference=body.ReferencePose;
                }
                byJoint.Add(joint,id);bulletLinks.Add(body);
                foreach(var c in link.Colliders) {
                    // Bullet's convex shapes represent outside collisions. Plane bounds can be inverted.
                    if(c.Inside && c.Shape!=2)continue;
                    if(!bulletColliders.TryGetValue(c,out int collider)) {
                        ColliderGeometry(c,out var a,out var b,out float r);
                        collider=bullet.Add(new BulletWorld.Body {Pose=ColliderPose(c),Shape=c.Shape==2?3:c.Shape==1?2:0,
                            Size=new Vector3(Mathf.Max(.001f,r),(b-a).magnitude,0),Friction=.5f,Group=1,Mask=65535,Restricted=1});
                        bulletColliders.Add(c,collider);
                    }
                    bullet.Allow(collider,id);
                }
            }
            for(int i=first;i<bulletLinks.Count;i++) {
                var b=bulletLinks[i];var link=b.Link;
                if(b.Settings.mode==0 || b.Settings.free)continue;
                var joint=b.Joint;var target=b.Target;
                var pivot=new BulletWorld.Pose(joint.position,link.Limit==0?target.Rotation:LimitFrame(link,(link.TipPosition-joint.position).normalized));
                // Aligned PhysBone segments inherit their parent's swing on the skeleton.
                // Pin each at its animated pivot so collisions on one strand cannot torque its siblings.
                if(b.MotionReference!=null || !byJoint.TryGetValue(joint.parent,out int parent)) {
                    parent=bullet.Add(new BulletWorld.Body {Shape=0,Size=Vector3.one*.002f,Pose=target,Group=0,Mask=0});
                    b.Anchor=parent;
                }
                Vector3 degrees=link.Limit==0?new Vector3(175,85,175):
                    new Vector3(link.Pitch,0,link.Limit==2?0:link.Limit==1?link.Pitch:link.Yaw);
                degrees=Vector3.Min(degrees,Vector3.one*175);
                float length=(link.TipPosition-joint.position).magnitude;
                float spring=b.Settings.mass*length*length*(link.Pull*40+link.Stiffness*60);
                float damping=1-.85f*link.Spring;
                if(b.MotionReference!=null) {
                    // Convert the 60 Hz PhysBone pull to an angular spring about the end of a rod.
                    float inertia=b.Settings.mass*length*length/3f;
                    float frequency=60f*Mathf.Sqrt(.14f*(link.Pull+link.Stiffness));
                    spring=inertia*frequency*frequency;
                    damping*=2*inertia*frequency;
                }
                bullet.Joint(parent,b.Id,pivot,degrees*Mathf.Deg2Rad,spring,damping);
            }
        }

        void BulletStage(EvaluationStage stage,float time,bool step)
        {
            foreach(var model in calculationOrder)EmitEvaluation(stage,time,step,model.id);
        }
        void SampleBulletBones(float time)
        {
            BulletStage(EvaluationStage.BeforeTrackSample,time,true);
            foreach(var model in calculationOrder) {
                foreach(var b in model.bones)b.Track.Seek(time);
                model.modelSwitch?.Seek(time);
            }
            BulletStage(EvaluationStage.AfterTrackSampleBeforeIK,time,true);ForEachActor(calculationOrder,SolveIK);
            BulletStage(EvaluationStage.AfterIKBeforePhysics,time,true);
        }

        void StepBullet(int tick)
        {
            foreach(var body in bulletLinks) {
                if(body.MotionReference!=null && !body.Link.Animated && !body.Link.Bone.Track.Current.PhysicsOff)
                    body.Joint.localRotation=body.Link.BindRotation;
                if(body.MotionReference!=null && body.Link.Immobile>0)body.FollowReference(bullet);
                var pose=body.Target;
                if(body.Anchor>=0)bullet.Drive(body.Anchor,true,pose);
                bullet.Drive(body.Id,body.Settings.mode==0 || body.Link.Bone.Track.Current.PhysicsOff,pose);
            }
            foreach(var pair in bulletColliders)bullet.Drive(pair.Value,true,ColliderPose(pair.Key));
            var g=gravityTrack.Current;
            Vector3 gravity=g.Direction.normalized*g.Acceleration;
            if(g.Noisy)gravity+=new Vector3(Mathf.Sin(tick*.713f),0,Mathf.Sin(tick*.431f))*(.098f*g.NoiseAmount);
            bullet.Step(gravity);
            foreach(var body in bulletLinks)body.Solved=bullet.Read(body.Id);
            ApplyBulletPose(true);
        }

        static void ApplyPhysBoneSwing(BulletLink b,bool advance)
        {
            var joint=b.Joint;
            var link=b.Link;
            var restRotation=link.Animated ? joint.localRotation : link.BindRotation;
            if(advance) {
                joint.localRotation=restRotation;
                var rest=link.TipPosition;
                var direction=b.Solved.Rotation*Vector3.up;
                // PhysBone limits the tip direction in the posed parent's frame. Roll about
                // the capsule axis does not move the tip and would twist the skinned mesh.
                if(link.Limit!=0)direction=LimitDirection(link,LimitFrame(link,(rest-joint.position).normalized),direction);
                AimTip(joint,rest,joint.position+direction);
                var swing=Quaternion.Inverse(restRotation)*joint.localRotation;
                // Opposing contacts can project onto opposite edges of a cone in one step.
                // Bound tip travel to half a segment per 60 Hz tick, as in StepDynamics.
                b.LocalSwing=Quaternion.RotateTowards(b.LocalSwing,swing,.5f*Mathf.Rad2Deg);
            }
            joint.localRotation=restRotation*b.LocalSwing;
        }

        void ApplyBulletPose(bool advance=false)
        {
            foreach(var b in bulletLinks) {
                if(b.Settings.mode==0 || b.Link.Bone.Track.Current.PhysicsOff) {
                    if(advance)b.LocalSwing=Quaternion.identity;
                    continue;
                }
                var joint=b.Joint;
                if(b.MotionReference!=null) {
                    ApplyPhysBoneSwing(b,advance);
                    continue;
                }
                joint.rotation=b.Solved.Rotation*Quaternion.Inverse(b.LocalRotation);
                if(b.Settings.mode==1)joint.position=b.Solved.Position-joint.TransformVector(b.LocalCenter);
            }
        }
        bool BulletEnabled(bool running) => (physicsMode!=PhysicsMode.None || running && forceOnPlayback) &&
            (running || physicsMode==PhysicsMode.Always || physicsMode==PhysicsMode.Trace) &&
            calculationOrder.Any(m=>m.dynamicLinks.Count>0);

        void EvaluateBullet(float time,bool running)
        {
            if(BulletEnabled(running)) {
                int target=Mathf.FloorToInt(time*2+.00001f);
                if(idleBullet || target<bulletTick)ClearBullet();
                if(bullet==null){SampleBulletBones(0);BuildBullet();bulletTick=0;BulletStage(EvaluationStage.AfterPhysics,0,true);}
                while(bulletTick<target){
                    ++bulletTick;float sample=bulletTick*.5f;
                    SampleBulletBones(sample);gravityTrack.Seek(sample);StepBullet(bulletTick);BulletStage(EvaluationStage.AfterPhysics,sample,true);
                }
            } else if(bullet!=null)ClearBullet();
            BulletStage(EvaluationStage.BeforeTrackSample,time,false);
            foreach(var m in calculationOrder)foreach(var t in m.modelTracks)t.Seek(time);
            BulletStage(EvaluationStage.AfterTrackSampleBeforeIK,time,false);ForEachActor(calculationOrder,SolveIK);
            BulletStage(EvaluationStage.AfterIKBeforePhysics,time,false);if(bullet!=null)ApplyBulletPose();
            BulletStage(EvaluationStage.AfterPhysics,time,false);
        }
        void IdleBullet()
        {
            if(physicsMode!=PhysicsMode.Always || modal!=null || exporting || !BulletEnabled(false))return;
            bulletRemainder+=Mathf.Min(Time.unscaledDeltaTime,.1f);
            while(bulletRemainder>=1.0/60){
                bulletRemainder-=1.0/60;
                ForEachActor(calculationOrder,RefreshRigPreview);
                if(bullet==null){BuildBullet();bulletTick=frame*2;}
                idleBullet=true;StepBullet(++bulletTick);
            }
        }

        Action BuildBulletControls(VisualElement body,Func<DynamicLink> selection)
        {
            var controls=Box(body,"bullet-properties");
            controls.Add(new Label("Bullet剛体"));
            var shape=new DropdownField("形状",new List<string>{"球","箱","カプセル"},2);
            var mode=new DropdownField("動作",new List<string>{"ボーン追従","物理演算","物理演算＋位置合わせ"},2);
            var mass=new FloatField("質量 (kg)"){isDelayed=true};
            var friction=new Slider("摩擦",0,2){showInputField=true};
            var bounce=new Slider("反発",0,1){showInputField=true};
            var linear=new Slider("移動減衰",0,1){showInputField=true};
            var angular=new Slider("回転減衰",0,1){showInputField=true};
            var group=new IntegerField("衝突グループ (1～16)"){isDelayed=true};
            var mask=new IntegerField("衝突マスク (0～65535)"){isDelayed=true};
            var free=new Toggle("親とのジョイントを外す");
            foreach(var field in new VisualElement[]{shape,mode,mass,friction,bounce,linear,angular,group,mask,free})controls.Add(field);
            void Sync() {
                var link=selection();controls.SetEnabled(link!=null);if(link==null)return;
                var s=actor.modelOverrides.bullet.Find(item=>item.name==link.Bone.Track.Name)??new BulletSetting();
                shape.SetValueWithoutNotify(shape.choices[s.shape]);mode.SetValueWithoutNotify(mode.choices[s.mode]);
                mass.SetValueWithoutNotify(s.mass);friction.SetValueWithoutNotify(s.friction);bounce.SetValueWithoutNotify(s.restitution);
                linear.SetValueWithoutNotify(s.linearDamping);angular.SetValueWithoutNotify(s.angularDamping);
                group.SetValueWithoutNotify(s.group+1);mask.SetValueWithoutNotify(s.mask);free.SetValueWithoutNotify(s.free);
            }
            void Save() {
                var link=selection();if(link==null)return;
                var s=new BulletSetting {name=link.Bone.Track.Name,shape=shape.index,mode=mode.index,mass=mass.value,
                    friction=friction.value,restitution=bounce.value,linearDamping=linear.value,angularDamping=angular.value,
                    group=group.value-1,mask=mask.value,free=free.value};
                if(s.Valid) {
                    actor.modelOverrides.bullet.RemoveAll(item=>item.name==s.name);actor.modelOverrides.bullet.Add(s);
                    InvalidateDynamics();RefreshRigPreview();
                } else {status.text="質量は0.001～1000 kg、グループは1～16、マスクは0～65535です。";Sync();}
            }
            shape.RegisterValueChangedCallback(_=>Save());mode.RegisterValueChangedCallback(_=>Save());
            foreach(var f in new[]{friction,bounce,linear,angular})f.RegisterValueChangedCallback(_=>Save());
            mass.RegisterValueChangedCallback(_=>Save());group.RegisterValueChangedCallback(_=>Save());
            mask.RegisterValueChangedCallback(_=>Save());free.RegisterValueChangedCallback(_=>Save());
            return Sync;
        }

        void SetPhysicsEngine(PhysicsEngine engine)
        {
            if(engine==physicsEngine)return;
            StopPlayback();ForEachActor(calculationOrder,InvalidateDynamics);ClearBullet();
            physicsEngine=engine;SetFrame(frame);
        }

        void ResetBodies()
        {
            ClearBullet();
            ForEachActor(calculationOrder,()=>{InvalidateDynamics();RefreshRigPreview();});
            if(physicsEngine==PhysicsEngine.Bullet && BulletEnabled(playing)){
                BuildBullet();bulletTick=frame*2;
            }
        }

        void RestoreSolvedPose()
        {
            if(physicsEngine==PhysicsEngine.Bullet){if(bullet!=null)ApplyBulletPose();return;}
            ForEachActor(calculationOrder,()=>{if(UseSimulation(false))StepDynamics(actor.simTick,false);});
        }
    }
}
