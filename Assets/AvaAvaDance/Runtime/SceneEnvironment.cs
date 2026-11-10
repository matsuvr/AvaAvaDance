using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    [Serializable] internal struct ShadowPose
    {
        public int Mode;
        public float Range;
        public static ShadowPose Initial => new ShadowPose { Mode = 1, Range = 8875 };
        public static ShadowPose Blend(ShadowPose a, ShadowPose b, float t) =>
            new ShadowPose { Mode = a.Mode, Range = Mathf.Lerp(a.Range, b.Range, t) };
    }

    [Serializable] internal struct GravityPose
    {
        public float Acceleration, NoiseAmount;
        public Vector3 Direction;
        public bool Noisy;
        public static GravityPose Initial => new GravityPose { Acceleration = 9.8f, Direction = Vector3.down, NoiseAmount = 10 };
        public static GravityPose Blend(GravityPose a, GravityPose b, float t) => new GravityPose {
            Direction = Vector3.Lerp(a.Direction, b.Direction, t), Acceleration = Mathf.Lerp(a.Acceleration, b.Acceleration, t),
            Noisy = a.Noisy, NoiseAmount = Mathf.Lerp(a.NoiseAmount, b.NoiseAmount, t)
        };
    }

    [Serializable] internal struct SwitchPose
    {
        public bool Hidden;
        public string[] DisabledIK;
        public bool Enabled(string name) => DisabledIK==null || Array.IndexOf(DisabledIK,name)<0;
    }

    public sealed partial class DanceApp
    {
        MotionTrack<ShadowPose> shadowTrack;
        MotionTrack<GravityPose> gravityTrack;
        VisualElement shadowPanel, modal;
        Button[] shadowModes = new Button[3];
        SliderInt shadowRange;
        GameObject shadowFloor;
        Material groundMaterial;

        bool groundShadow = true, showSelfShadow = true, transparentGround = true;
        float groundBrightness = 1;
        Toggle modelSelfToggle;

        VisualElement ShowDialog(string title, out VisualElement body)
        {
            CloseDialog();
            StopPlayback();
            var root = GetComponent<UIDocument>().rootVisualElement;
            modal = Box(root, "modal", "modal");
            root.Q("workspace")?.SetEnabled(false);
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("dialog-body"); modal.Add(scroll);
            body = scroll.contentContainer; body.name = "dialog-body";
            body.Add(new Label(title) { name = "dialog-title" });
            modal.focusable = true; modal.Focus();
            return modal;
        }
        void CloseDialog() { if(modal==null)return; modal.RemoveFromHierarchy(); modal=null; var workspace=GetComponent<UIDocument>().rootVisualElement.Q("workspace"); workspace?.SetEnabled(true); workspace?.Focus(); }

        void BuildShadowPanel(VisualElement parent)
        {
            shadowPanel = Panel(parent, "self-shadow", "セルフ影操作");
            var row = Box(shadowPanel, "shadow-modes", "row");
            string[] names = { "影なし", "モード1", "モード2" };
            for (int i = 0; i < names.Length; i++) {
                int mode = i;
                shadowModes[i] = Action(row, names[i], true, () => {
                    var pose = shadowTrack.Current; pose.Mode = mode; shadowTrack.Preview(pose); SelectTrack(shadowTrack);
                });
            }
            shadowPanel.Add(new Label("影範囲"));
            shadowRange = new SliderInt(0, 9999) { showInputField = true }; shadowPanel.Add(shadowRange);
            shadowRange.RegisterValueChangedCallback(e => {
                var pose = shadowTrack.Current; pose.Range = e.newValue; shadowTrack.Preview(pose); SelectTrack(shadowTrack);
            });
            Action(shadowPanel, "登録", true, () => RegisterKey(shadowTrack));
        }

        void InitEnvironment()
        {
            groundMaterial = new Material(Resources.Load<Shader>("ShadowGround"));
            shadowFloor = new GameObject();
            var mesh = new Mesh { name="Shadow receiver" };
            mesh.vertices=new[]{new Vector3(-5,0,-5),new Vector3(-5,0,5),new Vector3(5,0,5),new Vector3(5,0,-5)};
            mesh.triangles=new[]{0,1,2,0,2,3}; mesh.RecalculateNormals();
            shadowFloor.AddComponent<MeshFilter>().sharedMesh=mesh;
            shadowFloor.name = "地面影"; shadowFloor.layer = 30;
            shadowFloor.transform.SetParent(previewRig.transform, false);
            shadowFloor.transform.position = new Vector3(0,-1.003f,0);
            shadowFloor.transform.localScale = new Vector3(10,1,10);
            var receiver = shadowFloor.AddComponent<MeshRenderer>();
            receiver.sharedMaterial = groundMaterial; receiver.shadowCastingMode = ShadowCastingMode.Off; receiver.receiveShadows = true;
            shadowTrack = new MotionTrack<ShadowPose>("セルフ影", ShadowPose.Initial, ShadowPose.Blend, ApplyShadow);
            gravityTrack = new MotionTrack<GravityPose>("重力", GravityPose.Initial, GravityPose.Blend, _ => { });
            sceneTracks.Add(shadowTrack); sceneTracks.Add(gravityTrack);
            InitShadowRendering(); ApplyShadow(ShadowPose.Initial);
        }

        void ApplyShadow(ShadowPose pose)
        {
            previewLamp.shadows = LightShadows.None;
            shadowFloor.SetActive(groundShadow);
            groundMaterial.SetColor("_GroundTint",viewOptions.shadowTint);
            groundMaterial.SetFloat("_GroundBrightness", Mathf.Clamp01(groundBrightness*.56f));
            groundMaterial.SetFloat("_GroundOpacity", transparentGround?.52f:1f);

        }

        void SyncEnvironment()
        {
            if (shadowTrack == null) return;
            var p = shadowTrack.Current;
            for (int i=0;i<3;i++) shadowModes[i].EnableInClassList("active-tool", p.Mode==i);
            shadowRange.SetValueWithoutNotify(Mathf.RoundToInt(p.Range));
            modelSelfToggle.SetValueWithoutNotify(actor.modelSelfShadow); modelSelfToggle.SetEnabled(actor.avatar != null);
            SyncRigControls();
        }

        void ShowGravity()
        {
            ShowDialog("重力設定", out var body);
            var g = gravityTrack.Current;
            var acceleration = new FloatField("加速度") { value = g.Acceleration }; body.Add(acceleration);
            var noise = new Toggle("ノイズ付加") { value = g.Noisy }; body.Add(noise);
            var amount = new SliderInt("ノイズ", 0, 100) { value = Mathf.RoundToInt(g.NoiseAmount), showInputField = true }; body.Add(amount);
            Slider[] axes = new Slider[3];
            for (int i=0;i<3;i++) { axes[i] = new Slider("向き " + "XYZ"[i], -1, 1) { value=g.Direction[i], showInputField=true }; body.Add(axes[i]); }
            void Preview() {
                g = new GravityPose { Acceleration=Mathf.Clamp(acceleration.value,0,1000),
                    Direction=new Vector3(axes[0].value,axes[1].value,axes[2].value), Noisy=noise.value, NoiseAmount=amount.value };
                if (!ValidPose(g)) return;
                gravityTrack.Preview(g); InvalidateDynamics();
            }
            acceleration.RegisterValueChangedCallback(_ => Preview());
            noise.RegisterValueChangedCallback(_ => Preview()); amount.RegisterValueChangedCallback(_ => Preview());
            foreach (var axis in axes) axis.RegisterValueChangedCallback(_ => Preview());
            var row = Box(body,"gravity-actions","row");
            Action(row,"登録",true,()=> { Preview(); ChangeMode(false); RegisterKey(gravityTrack); });
            Action(row,"閉じる",true,CloseDialog);
        }
    }
}
