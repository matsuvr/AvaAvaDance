using System;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        CameraPose? navigationView;
        long navigationDocument;

        CameraPose ViewportView => navigationDocument == documentGeneration && navigationView.HasValue
            ? navigationView.Value : ActiveView;

        void NavigateViewport(CameraPose pose)
        {
            StopPlayback();
            navigationView = pose;
            navigationDocument = documentGeneration;
            ApplyView();
        }

        void ShowAnimatedCamera()
        {
            StopPlayback();
            navigationView = null;
            useCameraView = true;
            ApplyView();
        }

        void FrameViewport(bool selection)
        {
            if (previewCamera == null || importing || exporting || KeysCaptured()) return;
            Bounds? bounds = null;
            void Include(Bounds next)
            {
                if (!bounds.HasValue) bounds = next;
                else { var combined = bounds.Value; combined.Encapsulate(next); bounds = combined; }
            }
            void IncludeObject(GameObject root, bool visibleOnly)
            {
                if (root == null || visibleOnly && !root.activeInHierarchy) return;
                bool found = false;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(!visibleOnly))
                {
                    if (!renderer.enabled || visibleOnly && renderer.forceRenderingOff) continue;
                    Include(renderer.bounds); found = true;
                }
                if (!found) Include(new Bounds(root.transform.position, Vector3.one * .2f));
            }

            if (selection)
            {
                foreach (var owner in drawOrder)
                {
                    if (selectedRows.Contains(owner.modelSwitch)) IncludeObject(owner.avatar, false);
                    foreach (var bone in owner.bones)
                    {
                        if (bone.Joint == null || !(selectedRows.Contains(bone.Track) || owner == actor && boneSelection.Contains(bone)))
                            continue;
                        float size = bone.Joint.parent == null ? .15f :
                            Mathf.Clamp(Vector3.Distance(bone.Joint.position, bone.Joint.parent.position), .08f, .5f);
                        Include(new Bounds(bone.Joint.position, Vector3.one * size));
                    }
                }
                foreach (var prop in props.Where(prop => selectedRows.Contains(prop.Track))) IncludeObject(prop.Object, false);
                if (selectedTrack == cameraTrack)
                    Include(new Bounds((cameraTrack.Current.Center - CameraPose.Initial.Center) * SceneUnit, Vector3.one));
            }
            if (!selection || !bounds.HasValue)
            {
                foreach (var owner in drawOrder) IncludeObject(owner.avatar, true);
                foreach (var prop in props) IncludeObject(prop.Object, true);
            }
            if (!bounds.HasValue) return;

            var pose = ViewportView;
            var rotation = Quaternion.LookRotation(-front, Vector3.up) * Quaternion.Euler(pose.Angles);
            float vertical = Mathf.Tan(.5f * pose.ViewAngle * Mathf.Deg2Rad);
            float horizontal = vertical * Mathf.Max(.01f, previewCamera.aspect);
            float distance = .05f;
            var extents = bounds.Value.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                var offset = Quaternion.Inverse(rotation) * new Vector3((corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y, (corner & 4) == 0 ? -extents.z : extents.z);
                float fit = 1.2f * Mathf.Max(Mathf.Abs(offset.x) / horizontal, Mathf.Abs(offset.y) / vertical);
                distance = Mathf.Max(distance, Mathf.Max(fit - (pose.Perspective ? offset.z : 0),
                    previewCamera.nearClipPlane - offset.z + .01f));
            }
            pose.Center = CameraPose.Initial.Center + (bounds.Value.center - ViewTargetFollowOffset()) / SceneUnit;
            pose.Distance = distance / SceneUnit;
            NavigateViewport(pose);
        }

        void DragViewportCamera(Vector2 delta, int button, bool alt, bool ctrl, bool shift)
        {
            var pose = alt ? ViewportView : ActiveView;
            if (button == 1 && (ctrl || alt))
            {
                float change = alt ? delta.x - delta.y : delta.y;
                pose.Distance = Mathf.Clamp(pose.Distance * Mathf.Exp(change * .012f), .1f, 10000);
            }
            else if (button == 2 || button == 1 && shift)
            {
                var move = -previewCamera.transform.right * delta.x + previewCamera.transform.up * delta.y;
                pose.Center += move * WorldPerPixel() / SceneUnit;
            }
            else
            {
                pose.Angles.y += delta.x * .5f;
                pose.Angles.x = Mathf.Clamp(pose.Angles.x - delta.y * .5f, -90, 90);
            }
            if (alt) NavigateViewport(pose);
            else PreviewView(pose);
        }
    }
}
