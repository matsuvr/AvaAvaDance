using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const float SceneUnit = .1f;
        MotionTrack<CameraPose> cameraTrack;
        MotionTrack<LightPose> lightTrack;
        Light previewLamp;
        Vector3 front = Vector3.forward;
        CameraPose modelView = CameraPose.Initial;
        bool useCameraView;
        readonly FloatField[] transformFields = new FloatField[8];
        readonly Slider[] lightSliders = new Slider[6];
        SliderInt fov;
        Toggle perspective, gridToggle;
        IntegerField bookmark;
        GameObject floor;
        Mesh floorMesh;
        Material floorMaterial;

        void BuildTransformBar(VisualElement parent)
        {
            var bar = Box(parent, "transform-bar", "row", "transform-bar");
            channelStatus = Box(parent, "", "row", "channel-legend");
            foreach (var item in new[] { ("登録済み", "channel-key"), ("未登録変更", "channel-draft"), ("編集不可", "channel-locked") })
            {
                var legend = new Label(item.Item1);
                legend.AddToClassList(item.Item2);
                channelStatus.Add(legend);
            }
            for (int i = 0; i < transformFields.Length; i++)
            {

                int axis = i;
                var field = new FloatField(i == ScaleChannel ? "Scale" : i == DistanceChannel ? "距離" : (i < 3 ? "Translate " : "Rotate ") + "XYZ"[i%3])
                    { isDelayed = true, formatString = "0.##" };
                transformFields[i] = field; bar.Add(field);
                BindChannelField(field, axis);
            }
        }

        void BuildFrameBookmark(VisualElement bar)
        {
            Action(bar,"セット",true,()=>bookmark.value=frame);
            bookmark=new IntegerField {tooltip="移動先フレーム",isDelayed=true}; bar.Add(bookmark);
            Action(bar,"Go",true,()=>SetFrame(bookmark.value)).tooltip="G";
        }

        void BuildSceneControls(VisualElement bottom)
        {
            camera = Panel(bottom, "camera", "カメラ操作");
            var options = Box(camera, "camera-options", "row");
            Action(options, "初期化", true, () => EditCameraPose(CameraPose.Initial));
            perspective = new Toggle("パース"); options.Add(perspective);
            perspective.RegisterValueChangedCallback(e =>
            {
                var pose = cameraTrack.Current; pose.Perspective = e.newValue; EditCameraPose(pose);
            });
            camera.Add(new Label("視野角"));
            fov = new SliderInt(1, 135) { showInputField = true };
            camera.Add(fov);
            BindDraftSlider(fov, () => cameraTrack, () => cameraTrack.Target == null);
            fov.RegisterValueChangedCallback(e =>
            {
                var pose = cameraTrack.Current; pose.ViewAngle = e.newValue;
                navigationView = null;
                EditDraftSlider(fov, cameraTrack, pose, cameraTrack.Target == null);
            });
            Action(camera, "登録", true, () => RegisterKey(cameraTrack));

            light = Panel(bottom, "light", "照明操作");
            BuildLightSelector(light);
            string[] labels = { "赤", "緑", "青", "X", "Y", "Z" };
            for (int i = 0; i < labels.Length; i++)
            {
                int axis = i;
                var slider = new Slider(labels[i], i < 3 ? 0 : -1, i < 3 ? 255 : 1) { showInputField = true };
                lightSliders[i] = slider; light.Add(slider);
                BindDraftSlider(slider, () => EditingLight);
                slider.RegisterValueChangedCallback(e =>
                {
                    if (!float.IsFinite(e.newValue)) { SyncControls(); return; }
                    StopPlayback();
                    var target = EditingLight;
                    var pose = target.Current;
                    if (axis < 3) pose.Color[axis] = Mathf.Round(Mathf.Clamp(e.newValue, 0, 255)) / 255f;
                    else pose.Direction[axis - 3] = Mathf.Clamp(e.newValue, -1, 1);
                    EditDraftSlider(slider, target, pose);
                    if (selectedTrack != target) SelectTrack(target);
                });
            }
            var actions = Box(light, "light-actions", "row");
            Action(actions, "初期化", true, () => { var target=EditingLight; SetDraftValues(new Dictionary<MotionTrack,object>{{target,LightPose.Initial}}, true); SelectTrack(target); });
            Action(actions, "登録", true, () => RegisterKey(EditingLight));
        }

        void BuildViewControls(VisualElement parent)
        {
            var views = Box(parent, "views", "view-buttons");
            string[] names = { "正面", "背面", "上面", "左面", "右面", "下面" };
            Vector2[] angles = { Vector2.zero, new Vector2(0,180), new Vector2(90,0),
                new Vector2(0,-90), new Vector2(0,90), new Vector2(-90,0) };
            for (int i = 0; i < names.Length; i++)
            {
                Vector2 angle = angles[i];
                Action(views, names[i], true, () => SetViewAngles(angle.x, angle.y));
            }
            var options = Box(parent, "display-options", "row");
            Action(options, "カメラ", true, () =>
            {
                if (modelMode) ShowAnimatedCamera();
                else PreviewCameraPose(cameraTrack.Current);
            });
            gridToggle = new Toggle("座標軸") { value = true }; options.Add(gridToggle);
            gridToggle.RegisterValueChangedCallback(e => floor.SetActive(e.newValue));
        }

        void BuildSceneTracks()
        {
            cameraTrack = new MotionTrack<CameraPose>("カメラ", CameraPose.Initial, CameraPose.Blend, _ => ApplyView());
            lightTrack = new MotionTrack<LightPose>("照明", LightPose.Initial, LightPose.Blend, ApplyLight);
            sceneTracks.Add(cameraTrack); sceneTracks.Add(lightTrack); sceneSelection = cameraTrack;
            cameraTrack.Seek(0); lightTrack.Seek(0);
        }

        void ApplyLight(LightPose lightPose)
        {
            ApplySceneLight(previewLamp, lightPose);
        }

        static void ApplySceneLight(Light lamp, LightPose lightPose)
        {
            if (lamp == null) return;
            lamp.color = lightPose.Color;
            var direction = lightPose.Direction;
            lamp.intensity = direction.sqrMagnitude < .000001f ? 0 : 1.6f;
            if (lamp.intensity > 0)
                lamp.transform.rotation = Quaternion.LookRotation(direction,
                    Mathf.Abs(direction.normalized.y) > .999f ? Vector3.forward : Vector3.up);
        }

        CameraPose ActiveView => modelMode && !playing && !exporting && !useCameraView && !viewOptions.follow ? modelView : cameraTrack.Current;

        void ApplyView()
        {
            if (previewCamera == null || cameraTrack == null) return;
            var pose = !playing && !exporting ? ViewportView : cameraTrack.Current;
            Vector3 center = (pose.Center - CameraPose.Initial.Center) * SceneUnit;
            center += ViewTargetFollowOffset();
            var basis = Quaternion.LookRotation(-front, Vector3.up);
            var rotation = basis * Quaternion.Euler(pose.Angles);
            previewCamera.transform.SetPositionAndRotation(center - rotation * Vector3.forward * (pose.Distance * SceneUnit), rotation);
            previewCamera.orthographic = !pose.Perspective;
            previewCamera.fieldOfView = pose.ViewAngle;
            previewCamera.orthographicSize = Mathf.Max(.01f, Mathf.Abs(pose.Distance * SceneUnit) * Mathf.Tan(.5f * pose.ViewAngle * Mathf.Deg2Rad));
            RedrawOverlay();
        }

        void EditCameraPose(CameraPose pose)
        {
            navigationView = null;
            SetDraftValues(new Dictionary<MotionTrack, object> { [cameraTrack] = pose }, cameraTrack.Target == null);
        }

        void PreviewCameraPose(CameraPose pose)
        {
            StopPlayback();
            navigationView = null;
            cameraTrack.Preview(pose);
            SelectTrack(cameraTrack);
        }

        void PreviewView(CameraPose pose)
        {
            navigationView = null;
            if (modelMode)
            {
                StopPlayback(); useCameraView = false; modelView = pose; ApplyView();
            }
            else PreviewCameraPose(pose);
        }

        void SetViewAngles(float pitch, float yaw)
        {
            var pose = ActiveView;
            pose.Angles = new Vector3(pitch, yaw, 0); PreviewView(pose);
        }

        // Used by the standalone front/back render check.
        void SetViewYaw(float yaw) => SetViewAngles(0, yaw);

        float BonePositionUnit => WorkspacePreferences.GetInt("Workspace.BoneUnit", ribbonProfile ? 1 : 0) == 1 ? 1f : SceneUnit;

        void EditTransform(int axis, float value)
        {
            StopPlayback();
            if (!float.IsFinite(value)) { SyncControls(); return; }
            value = Mathf.Clamp(value, -100000f, 100000f);
            if (modelMode)
            {
                if (actor.selectedBone == null) return;
                var pose = actor.selectedBone.Track.Current;
                if (axis < 3) pose.Position[axis] = value * BonePositionUnit;
                else if (axis < 6) { var angles = SignedAngles(pose.Rotation); angles[axis-3] = value; pose.Rotation = Quaternion.Euler(angles); }
                PreviewBone(pose);
            }
            else
            {
                var pose = cameraTrack.Current;
                if (axis < 3) pose.Center[axis] = value;
                else if (axis < 6) pose.Angles[axis-3] = value;
                else pose.Distance = value;
                PreviewCameraPose(pose);
            }
        }

        void SyncControls()
        {
            if (cameraTrack == null) return;
            var cam = cameraTrack.Current;
            SyncLightSelector();
            var lamp = EditingLight.Current;
            SyncChannelBox();
            perspective.SetEnabled(cameraTrack.Target == null);
            fov.SetEnabled(cameraTrack.Target == null);
            perspective.SetValueWithoutNotify(cam.Perspective);
            fov.SetValueWithoutNotify(Mathf.RoundToInt(cam.ViewAngle));
            for (int i = 0; i < 6; i++)
                lightSliders[i].SetValueWithoutNotify(i < 3 ? Mathf.Round(lamp.Color[i] * 255) : lamp.Direction[i-3]);
            SyncBoneControls(); SyncMorphControls(); SyncCurve(); SyncEnvironment(); SyncProp();
        }

        void MakeFloor()
        {
            floor = new GameObject("座標軸");
            floor.transform.SetParent(previewRig.transform, false);
            floor.layer = 30;
            var vertices = new List<Vector3>(); var colors = new List<Color>();
            void Segment(Vector3 a, Vector3 b, Color color)
            { vertices.Add(a); vertices.Add(b); colors.Add(color); colors.Add(color); }
            for (int i = -10; i <= 10; ++i)
            {
                var gray = new Color32(77, 83, 91, 255);
                Segment(new Vector3(-5,-1,i*.5f), new Vector3(5,-1,i*.5f), i == 0 ? new Color32(40, 44, 49, 255) : gray);
                Segment(new Vector3(i*.5f,-1,-5), new Vector3(i*.5f,-1,5), i == 0 ? new Color32(40, 44, 49, 255) : gray);
            }
            Segment(new Vector3(0,-1,0), new Vector3(0,5,0), new Color32(110, 149, 107, 255));
            floorMesh = new Mesh { name = "座標軸" }; floorMesh.SetVertices(vertices); floorMesh.SetColors(colors);
            int[] indices = new int[vertices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            floorMesh.SetIndices(indices, MeshTopology.Lines, 0);
            floor.AddComponent<MeshFilter>().sharedMesh = floorMesh;
            floorMaterial = new Material(Resources.Load<Shader>("FloorGrid"));
            floor.AddComponent<MeshRenderer>().sharedMaterial = floorMaterial;
        }
    }
}
