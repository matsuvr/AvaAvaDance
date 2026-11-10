using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PB = AvaAvaDance.Import.PrefabPhysBones;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        struct ParticleState
        {
            internal Vector3 Position, Velocity, ReferencePosition;
            internal Quaternion ReferenceRotation;
            internal float LengthRatio;
            internal bool Ready;
        }

        sealed class DynamicLink
        {
            internal BoneBinding Bone;
            internal Transform[] Children;
            internal Transform Reference;
            internal Vector3 Endpoint, AxisUp=Vector3.up, AxisForward=Vector3.forward;
            internal Quaternion BindRotation, LimitRotation=Quaternion.identity;
            internal float Pull, Spring, Stiffness, Gravity, Falloff, Immobile, Radius, Pitch, Yaw;
            internal float StretchMotion, MaxStretch, MaxSquish, EndpointRatio=1;
            internal int Limit, Version=1;
            internal bool Animated, WorldImmobile;
            internal PB.Collider[] Colliders=Array.Empty<PB.Collider>();
            internal ParticleState State;

            internal Vector3 TipPosition {
                get {
                    if(Children.Length==0)return Bone.Joint.TransformPoint(Endpoint*EndpointRatio);
                    var sum=Vector3.zero;
                    foreach(var t in Children)sum+=t.position;
                    return sum/Children.Length;
                }
            }
            internal float WorldRadius => Radius*Bone.Joint.TransformVector(AxisUp).magnitude;

            internal void ApplyLength(float ratio)
            {
                EndpointRatio=ratio;
                if(ratio!=1)foreach(var child in Children)child.localPosition*=ratio;
            }
        }

        Dictionary<Transform,DynamicLink> BuildPhysBoneLinks(PB data)
        {
            var links=new Dictionary<Transform,DynamicLink>();
            var roots=new HashSet<PB.Node>(data.Chains.Select(c=>c.Root));
            foreach(var chain in data.Chains) {
                var children=new Dictionary<PB.Node,PB.Node[]>();
                int Visit(PB.Node n,int depth) {
                    var next=n.Children.Where(c=>c.Joint!=null && c.Active && !chain.Ignore.Contains(c)
                        && !(chain.IgnoreOtherRoots && roots.Contains(c))).ToArray();
                    children.Add(n,next);
                    int deepest=depth;
                    foreach(var child in next)deepest=Math.Max(deepest,Visit(child,depth+1));
                    if(next.Length==0 && chain.Endpoint!=Vector3.zero)deepest++;
                    return deepest;
                }
                if(chain.Ignore.Contains(chain.Root))continue;
                int maxDepth=Visit(chain.Root,0);
                void Bind(PB.Node n,int depth) {
                    var next=children[n];
                    foreach(var child in next)Bind(child,depth+1);
                    if(links.ContainsKey(n.Joint) || (next.Length>1 && chain.MultiChild==0) ||
                        (next.Length==0 && chain.Endpoint==Vector3.zero))return;
                    // A depth curve runs from the first simulated segment to the last.
                    float t=Mathf.Clamp01((float)depth/Mathf.Max(1,maxDepth-1));
                    var f=chain.Parameters;
                    float Val(string name,float fallback=0) => f.Value(name,fallback,t);
                    Vector3 e=f.Vector("limitRotation");
                    e=Vector3.Scale(e,new Vector3(f.Multiplier("limitRotationXCurve",t),f.Multiplier("limitRotationYCurve",t),f.Multiplier("limitRotationZCurve",t)));
                    var link=new DynamicLink {
                        Children=(chain.MultiChild==1 ? next.Take(1) : next.AsEnumerable()).Select(c=>c.Joint).ToArray(),
                        Endpoint=n.ToJoint.MultiplyVector(chain.Endpoint),
                        AxisUp=n.ToJoint.MultiplyVector(Vector3.up), AxisForward=n.ToJoint.MultiplyVector(Vector3.forward),
                        Reference=chain.Root.Joint.parent ?? actor.avatar.transform, BindRotation=n.Joint.localRotation,
                        Pull=Mathf.Clamp01(Val("pull",.2f)), Spring=Mathf.Clamp01(Val("spring",.2f)),
                        Stiffness=f.Int("integrationType")==1 ? Mathf.Clamp01(Val("stiffness")) : 0,
                        Gravity=Mathf.Clamp(Val("gravity"),-1,1),Falloff=Mathf.Clamp01(Val("gravityFalloff")),
                        Immobile=Mathf.Clamp01(Val("immobile")),WorldImmobile=f.Int("immobileType")==1,
                        Radius=Mathf.Max(0,Val("radius")),Limit=f.Int("limitType"),LimitRotation=Quaternion.Euler(e),
                        Pitch=Mathf.Clamp(Val("maxAngleX",45),0,180),Yaw=Mathf.Clamp(Val("maxAngleZ",45),0,180),
                        Animated=f.Int("isAnimated")!=0,Version=f.Int("version"),Colliders=chain.Colliders,
                        StretchMotion=Mathf.Clamp01(Val("stretchMotion")),MaxStretch=Mathf.Max(0,Val("maxStretch")),MaxSquish=Mathf.Clamp01(Val("maxSquish"))
                    };
                    links.Add(n.Joint,link);
                }
                Bind(chain.Root,0);
            }
            return links;
        }

        static Quaternion LimitFrame(DynamicLink link,Vector3 restDirection)
        {
            Vector3 up=link.Bone.Joint.TransformVector(link.AxisUp).normalized;
            Vector3 forward=link.Bone.Joint.TransformVector(link.AxisForward).normalized;
            if(Vector3.Cross(forward,up).sqrMagnitude<1e-10f)return Quaternion.FromToRotation(Vector3.up,restDirection);
            return Quaternion.FromToRotation(up,restDirection)*Quaternion.LookRotation(forward,up)*link.LimitRotation;
        }

        static Vector3 LimitDirection(DynamicLink link,Quaternion frame,Vector3 direction)
        {
            if(link.Limit==0)return direction;
            Vector3 local=Quaternion.Inverse(frame)*direction;
            if(link.Limit==1)return frame*Vector3.RotateTowards(Vector3.up,local,link.Pitch*Mathf.Deg2Rad,0);
            float pitch=Mathf.Clamp(Mathf.Atan2(local.z,local.y)*Mathf.Rad2Deg,-link.Pitch,link.Pitch)*Mathf.Deg2Rad;
            float yaw=link.Limit==2 ? 0 : Mathf.Clamp(Mathf.Asin(Mathf.Clamp(local.x,-1,1))*Mathf.Rad2Deg,-link.Yaw,link.Yaw)*Mathf.Deg2Rad;
            return frame*new Vector3(Mathf.Sin(yaw),Mathf.Cos(yaw)*Mathf.Cos(pitch),Mathf.Cos(yaw)*Mathf.Sin(pitch));
        }

        static void ColliderGeometry(PB.Collider c,out Vector3 a,out Vector3 b,out float radius)
        {
            var center=c.Anchor.TransformPoint(c.Center);
            Vector3 up=c.Anchor.TransformVector(c.Up);
            radius=c.Radius*up.magnitude;
            var halfAxis=c.Shape==1 ? Mathf.Max(0,c.Height*.5f-c.Radius)*up : Vector3.zero;
            a=center-halfAxis; b=center+halfAxis;
        }

        static Vector3 Collide(PB.Collider c,Vector3 pivot,Vector3 tip,float particleRadius,Vector3 restDirection)
        {
            ColliderGeometry(c,out var a,out var b,out float radius);
            if(c.Shape==2) {
                Vector3 normal=c.Anchor.TransformVector(c.Up).normalized*(c.Inside ? -1 : 1);
                return tip+normal*Mathf.Max(0,particleRadius-Vector3.Dot(tip-a,normal));
            }
            Vector3 axis=b-a;
            float axisSquared=axis.sqrMagnitude;
            float fraction=1;
            Vector3 edge=tip-pivot;
            Vector3 pivotCenter=a+axis*Mathf.Clamp01(Vector3.Dot(pivot-a,axis)/Mathf.Max(axisSquared,1e-12f));
            float contactRadius=radius+particleRadius;
            if(!c.BonesAsSpheres && !c.Inside && edge.sqrMagnitude>1e-12f && (pivot-pivotCenter).sqrMagnitude>contactRadius*contactRadius) {
                // Closest points on the two finite segments (the other can be a sphere).
                float cross=Vector3.Dot(edge,axis), projection=Vector3.Dot(edge,pivot-a);
                float denom=edge.sqrMagnitude*axisSquared-cross*cross;
                float along=denom>1e-12f ? Mathf.Clamp01((cross*Vector3.Dot(axis,pivot-a)-projection*axisSquared)/denom) : 0;
                float other=axisSquared>1e-12f ? Mathf.Clamp01(Vector3.Dot(axis,pivot+edge*along-a)/axisSquared) : 0;
                along=Mathf.Clamp01((cross*other-projection)/edge.sqrMagnitude);
                fraction=along;
            }
            Vector3 point=pivot+fraction*edge;
            Vector3 center=a+axis*Mathf.Clamp01(Vector3.Dot(point-a,axis)/Mathf.Max(axisSquared,1e-12f));
            var delta=point-center;
            float distance=delta.magnitude;
            float bound=c.Inside ? Mathf.Max(0,radius-particleRadius) : radius+particleRadius;
            if(c.Inside ? distance<=bound : distance>=bound)return tip;
            Vector3 correction=(distance>1e-7f ? delta/distance : restDirection)*((bound-distance)/Mathf.Max(.2f,fraction));
            return tip+Vector3.ClampMagnitude(correction,edge.magnitude*.25f);
        }

        static void AimTip(Transform joint,Vector3 rest,Vector3 target)
        {
            // Work in the parent's scaled space, including mirrored FBX armatures.
            Vector3 from=joint.parent.InverseTransformVector(rest-joint.position);
            Vector3 to=joint.parent.InverseTransformVector(target-joint.position);
            if(from.sqrMagnitude>1e-16f && to.sqrMagnitude>1e-16f)
                joint.localRotation=Quaternion.FromToRotation(from,to)*joint.localRotation;
        }

        void StepDynamics(int tick,bool integrate)
        {
            var g=gravityTrack.Current;
            var gravity=g.Direction.normalized*(g.Acceleration/9.8f);
            if(g.Noisy)gravity+=new Vector3(Mathf.Sin(tick*.713f),0,Mathf.Sin(tick*.431f))*(g.NoiseAmount*.01f);
            foreach(var link in actor.dynamicLinks) {
                var joint=link.Bone.Joint;
                link.EndpointRatio=1;
                if(link.Bone.Track.Current.PhysicsOff) {
                    if(integrate)link.State=default;
                    continue;
                }
                if(!link.Animated)joint.localRotation=link.BindRotation;
                Vector3 rest=link.TipPosition, pivot=joint.position;
                float length=(rest-pivot).magnitude;
                if(length<1e-6f)continue;
                var s=link.State;
                if(integrate) {
                    var reference=link.WorldImmobile ? actor.avatar.transform : link.Reference;
                    Vector3 referencePosition=reference.position;
                    Quaternion rotation=link.WorldImmobile ? Quaternion.identity : reference.rotation;
                    Vector3 restDir=(rest-pivot)/length;
                    Vector3 current=rest, next=rest;
                    if(s.Ready) {
                        Quaternion turn=rotation*Quaternion.Inverse(s.ReferenceRotation);
                        Vector3 transported=referencePosition+turn*(s.Position-s.ReferencePosition);
                        current=Vector3.Lerp(s.Position,transported,link.Immobile);
                        Vector3 velocity=Vector3.Lerp(s.Velocity,turn*s.Velocity,link.Immobile);
                        // Forces are relative to segment length. A short fringe and a long
                        // skirt therefore have the same response to the same gravity setting.
                        float attenuation=1-link.Falloff*Mathf.Clamp01(Vector3.Dot(restDir,(current-pivot).normalized));
                        float pull=link.Pull*.14f;
                        Vector3 gravityOffset=gravity*(link.Gravity*attenuation*length);
                        Vector3 acceleration=(rest-current)*pull+gravityOffset*(link.Version==0 ? .04f : pull);
                        next=current+Vector3.ClampMagnitude(velocity,length*.5f)*(.45f+.53f*link.Spring)*(1-link.Stiffness)+acceleration;
                        next=Vector3.Lerp(next,rest,link.Stiffness*.18f);
                    }
                    var frame=link.Limit==0 ? Quaternion.identity : LimitFrame(link,restDir);
                    float radius=link.WorldRadius;
                    bool flexible=link.Version>0 && (link.MaxStretch>0 || link.MaxSquish>0);
                    float targetLength=flexible ? Mathf.Lerp(length,(next-pivot).magnitude,link.StretchMotion) : length;
                    Vector3 predictedVelocity=next-current;
                    bool contact=false;
                    for(int pass=0;pass<3;pass++) {
                        Vector3 before=next;
                        foreach(var c in link.Colliders)next=Collide(c,pivot,next,radius,restDir);
                        if(physicalFloor)next.y=Mathf.Max(next.y,-1+radius);
                        contact|=(before-next).sqrMagnitude>length*length*1e-10f;
                        Vector3 dir=next-pivot;
                        if(flexible)targetLength=Mathf.Clamp(targetLength+(dir.magnitude-(before-pivot).magnitude),length*(1-link.MaxSquish),length*(1+link.MaxStretch));
                        dir=dir.sqrMagnitude>1e-12f ? dir.normalized : restDir;
                        next=pivot+LimitDirection(link,frame,dir)*targetLength;
                    }
                    AimTip(joint,rest,next);
                    s.LengthRatio=targetLength/length;
                    link.ApplyLength(s.LengthRatio);
                    // Cache the achievable tip, not a penetration correction as free velocity.
                    s.Position=link.TipPosition;
                    var motion=s.Position-current;
                    if(contact) {
                        // A constraint can eject a tip even at rest. Only carry the part
                        // of the predicted step which survived collision, with contact drag.
                        Vector3 tangent=Vector3.ProjectOnPlane(predictedVelocity,(s.Position-pivot).normalized);
                        motion=tangent.sqrMagnitude<1e-14f ? Vector3.zero :
                            tangent*Mathf.Clamp01(Vector3.Dot(motion,tangent)/tangent.sqrMagnitude)*.5f;
                    }
                    s.Velocity=Vector3.ClampMagnitude(motion,length*.5f);
                    s.ReferencePosition=referencePosition;s.ReferenceRotation=rotation;s.Ready=true;
                    link.State=s;
                } else if(s.Ready) {
                    AimTip(joint,rest,s.Position);
                    link.ApplyLength(s.LengthRatio);
                }
            }
        }
    }
}
