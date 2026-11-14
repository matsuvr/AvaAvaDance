using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly struct HumanoidBindRotation
        {
            internal readonly Quaternion Local, WorldBasis;

            internal HumanoidBindRotation(Transform joint)
            {
                Local=joint.localRotation;
                WorldBasis=RotationBasis(joint);
            }

            // Rotation axes are axial vectors. For a reflected FBX bind frame O,
            // -O is a proper rotation and has the same conjugation on rotations.
            internal static Quaternion RotationBasis(Transform joint)
            {
                // localRotation acts before the joint's own scale in Unity's TRS.
                if (joint.parent==null) return joint.localRotation;
                var matrix=joint.parent.localToWorldMatrix*Matrix4x4.Rotate(joint.localRotation);
                float sign=matrix.determinant<0 ? -1f : 1f;
                return Quaternion.LookRotation(sign*(Vector3)matrix.GetColumn(2),sign*(Vector3)matrix.GetColumn(1));
            }
        }

        sealed class Actor
        {
            internal Ava.Extensions.RigProfile rigProfile;
            internal EffectController controller;
            internal BoneBinding logicalPelvis;
            internal readonly List<MotionTrack<BonePose>> logicalControls=new List<MotionTrack<BonePose>>(),pelvisControls=new List<MotionTrack<BonePose>>();
            internal VmdSidecar vmdSidecar=new VmdSidecar();
            internal string id = Guid.NewGuid().ToString("N"), label;
            internal GameObject avatar;
            internal UniVRM10.Vrm10Instance vrm;
            internal Transform gaze;
            internal Dictionary<HumanBodyBones, Transform> humanBones;
            internal Dictionary<Transform, HumanoidBindRotation> bindPose;
            internal Dictionary<Transform, Vector3> bindPositions;
            internal Quaternion humanForward;
            internal Matrix4x4 hipsParentInverse;
            internal float hipsHeight;
            internal List<UnityEngine.Object> importedResources = new List<UnityEngine.Object>();
            internal List<Import.MissingMaterialShader> missingShaders = new();
            internal List<Asset> modelAssets = new List<Asset>();
            internal string modelGuid;
            internal string prefabGuid;
            internal List<MotionTrack> modelTracks = new List<MotionTrack>();
            internal MotionTrack modelSelection;
            internal List<BoneBinding> bones = new List<BoneBinding>();
            internal BoneBinding selectedBone;
            internal MotionTrack<SwitchPose> modelSwitch;
            internal List<LimbIK> ikChains = new List<LimbIK>();
            internal List<DynamicLink> dynamicLinks = new List<DynamicLink>();
            internal List<string> customIK = new List<string>();
            internal List<string> extraDynamic = new List<string>();
            internal List<string> excludedDynamic = new List<string>();
            internal Dictionary<Transform,DynamicLink> authoredLinks = new Dictionary<Transform,DynamicLink>();
            internal SortedList<int,ParticleState[]> simulationCache = new SortedList<int,ParticleState[]>();
            internal int simTick = -1;
            internal int idleTick;
            internal float idleRemainder;
            internal Renderer[] shadowCasters = Array.Empty<Renderer>();
            internal readonly HashSet<Renderer> sourceShadowReceivers = new();
            internal bool modelSelfShadow = true;
            internal ModelOverrides modelOverrides = new ModelOverrides();
            internal Dictionary<ToonSetting,Texture2D> toonImages = new Dictionary<ToonSetting,Texture2D>();
            internal Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();
            internal List<Material> ghosts = new List<Material>();
        }
        Actor actor = new Actor();
        readonly List<Actor> drawOrder = new List<Actor>(), calculationOrder = new List<Actor>();

        void BeginActor(GameObject avatar)
        {
            StopPlayback();
            string name=avatar.name;
            for(int n=2;drawOrder.Any(m=>m.label==name) || name==SceneModeName;n++) name=avatar.name+" ("+n+")";
            actor=new Actor {avatar=avatar,label=name};
            drawOrder.Add(actor);calculationOrder.Add(actor);
            foreach(var group in morphGroups)group.Tracks=new List<MotionTrack<float>>();
        }

        void RefreshActorUI()
        {
            foreach(var g in morphGroups)g.Tracks.Clear();
            foreach(var t in actor.modelTracks.OfType<MotionTrack<float>>())
                morphGroups[MorphCategory(MorphName(t))].Tracks.Add(t);
            foreach(var g in morphGroups) {
                g.List.choices=g.Tracks.Select(MorphName).ToList();
                g.List.SetValueWithoutNotify(g.List.choices.FirstOrDefault()??"なし");
            }
            RefreshRigLists();
            removeButton.SetEnabled(actor.avatar!=null);
            viewportNote.EnableInClassList("hidden",actor.avatar!=null);
        }
        void ChooseMode(string label)
        {
            var next=drawOrder.Find(m=>m.label==label);
            if(next!=null && actor!=next){StopPlayback();actor=next;RefreshActorUI();}
            ChangeMode(next!=null);
        }
        void CycleModel(bool reverse)
        {
            if(drawOrder.Count==0)return;
            int index=modelMode?drawOrder.IndexOf(actor)+1:0,count=drawOrder.Count+1;
            int next=(index+count+(reverse?-1:1))%count;
            ChooseMode(next==0?SceneModeName:drawOrder[next-1].label);
        }
        static string MorphName(MotionTrack<float> t)=>t.Name.StartsWith("表情：",StringComparison.Ordinal)?t.Name.Substring(3):t.Name;
        void ForEachActor(List<Actor> order,Action operation)
        {
            var current=actor;
            try {foreach(var item in order){actor=item;operation();}}
            finally {actor=current;}
        }
        void ApplyDrawOrder()
        {
            for(int i=0;i<drawOrder.Count;i++)foreach(var r in drawOrder[i].shadowCasters)r.sortingOrder=i;
        }
        void ModelOrder(bool drawing)
        {
            var order=drawing?drawOrder:calculationOrder;
            ShowDialog(drawing?"モデル描画順":"モデル計算順",out var body);
            var list=new DropdownField(order.Select(a=>a.label).ToList(),Math.Max(0,order.IndexOf(actor)));body.Add(list);
            void Move(int step) {
                int i=list.index,j=Mathf.Clamp(i+step,0,order.Count-1);
                if(i==j)return;
                var item=order[i];order.RemoveAt(i);order.Insert(j,item);
                list.choices=order.Select(a=>a.label).ToList();list.SetValueWithoutNotify(item.label);
                if(drawing)ApplyDrawOrder();else ForEachActor(order,InvalidateDynamics);
            }
            Action(body,"上へ",true,()=>Move(-1));Action(body,"下へ",true,()=>Move(1));Action(body,"閉じる",true,()=>{CloseDialog();ChangeMode(modelMode);});
        }
        void ClearAllActors(bool refresh = true)
        {
            foreach(var item in drawOrder.ToArray()){actor=item;ClearAvatar(refresh: false);}
            actor=new Actor();
            if (refresh)
            {
                RefreshActorUI();
                BindShadowRenderers();
                RefreshMissingShaderButton();
            }
        }
    }
}
