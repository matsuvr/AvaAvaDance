using System;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Run in Unity: uses actual Transform parenting and camera evaluation.
        void CheckPlacementAndFollow()
        {
            void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException("Placement/follow: " + label); }
            var priorActor = actor;
            var priorFollowActor = viewFollowActor; var priorTarget = viewFollowTarget; var priorOrigin = viewFollowOrigin;
            var priorExporting = exporting;
            var parent = new GameObject("placement-smoke-parent");
            var root = new GameObject("placement-smoke-avatar"); root.transform.SetParent(parent.transform, false);
            var target = new GameObject("placement-smoke-bone"); target.transform.SetParent(root.transform, false);
            var model = new Actor { avatar = root, label = "placement smoke" };
            var initial = new Vector3(1, 2, 3); root.transform.localPosition = initial;
            try
            {
                drawOrder.Add(model); actor = model;
                RestoreModelPlacement(model, new Vector3(4, 0, 0), new Vector3(0, 90, 0));
                Check(root.transform.localPosition == initial, "animation-local position unchanged");
                Check(Quaternion.Angle(root.transform.localRotation, Quaternion.identity) < .001f, "animation-local rotation unchanged");
                var expected = new Vector3(4, 0, 0) + Quaternion.Euler(0, 90, 0) * initial;
                Check(Vector3.Distance(root.transform.position, expected) < .0001f, "offset composed outside animation");
                var wrapper = model.placementRoot;
                root.transform.localPosition = new Vector3(2, 0, 0);
                RestoreModelPlacement(model, new Vector3(4, 0, 0), new Vector3(0, 90, 0));
                Check(wrapper == model.placementRoot, "repeated edit does not nest parents");
                Check(Vector3.Distance(root.transform.position, new Vector3(4, 0, -2)) < .0001f, "animation change keeps placement");
                RestoreModelPlacement(model, Vector3.zero, Vector3.zero);
                Check(root.transform.position == root.transform.localPosition, "zero placement restores unoffset animation");
                bool rejected = false;
                try { RestoreModelPlacement(model, new Vector3(float.NaN, 0, 0), Vector3.zero); } catch (ArgumentException) { rejected = true; }
                Check(rejected && model.placementPosition == Vector3.zero, "invalid input is non-mutating");
                FollowViewTarget(false);
                root.transform.localPosition += Vector3.up;
                Check(Vector3.Distance(ViewTargetFollowOffset(), Vector3.up) < .0001f, "model target follows animation");
                actor = priorActor;
                Check(Vector3.Distance(ViewTargetFollowOffset(), Vector3.up) < .0001f, "changing selection keeps chosen target");
                exporting = true; Check(ViewTargetFollowOffset() == Vector3.zero, "movie camera is unaffected"); exporting = false;
                drawOrder.Remove(model); Check(ViewTargetFollowOffset() == Vector3.zero, "removed target is ignored");
            }
            finally
            {
                actor = priorActor; exporting = priorExporting;
                viewFollowActor = priorFollowActor; viewFollowTarget = priorTarget; viewFollowOrigin = priorOrigin;
                drawOrder.Remove(model); DestroyImmediate(parent); ApplyView();
            }
        }
    }
}
