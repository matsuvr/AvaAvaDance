using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // A parent outside the animated hierarchy composes placement after all
        // local animation, IK and physics, without rewriting any motion key.
        void RestoreModelPlacement(Actor owner, Vector3 position, Vector3 angles)
        {
            if (!FinitePlacement(position) || !FinitePlacement(angles))
                throw new ArgumentException("モデル配置には有限の数値を指定してください。");
            if (owner.avatar == null) return;
            if (owner.placementRoot == null)
            {
                if (position == Vector3.zero && angles == Vector3.zero) return;
                var root = owner.avatar.transform;
                var placement = new GameObject("モデル配置 " + owner.label).transform;
                placement.SetParent(root.parent, false);
                root.SetParent(placement, false);
                owner.placementRoot = placement;
            }
            var previous = owner.placementRoot.localToWorldMatrix;
            owner.placementRoot.localPosition = position;
            owner.placementRoot.localRotation = Quaternion.Euler(angles);
            owner.rigToScene = owner.placementRoot.localToWorldMatrix * previous.inverse * owner.rigToScene;
            owner.placementPosition = position; owner.placementAngles = angles;
        }

        static bool FinitePlacement(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        void ApplyModelPlacement(Actor owner, Vector3 position, Vector3 angles)
        {
            RestoreModelPlacement(owner, position, angles);
            ClearBullet();
            foreach (var model in calculationOrder) ClearSimulation(model);
            ForEachActor(calculationOrder, RefreshRigPreview);
            ApplyView(); RedrawOverlay();
        }

        void ShowModelPlacement()
        {
            if (actor.avatar == null) return;
            StopPlayback(); var owner = actor;
            ShowDialog("モデル全体の配置", out var body);
            body.Add(new Label("既存キーを変更しない位置・回転オフセットです。位置はメートル、回転は度。"));
            var position = new Vector3Field("位置") { name = "model-placement-position", value = owner.placementPosition };
            var angles = new Vector3Field("回転") { name = "model-placement-rotation", value = owner.placementAngles };
            body.Add(position); body.Add(angles);
            var error = new Label(); body.Add(error);
            Action(body, "初期化", true, () => { position.value = Vector3.zero; angles.value = Vector3.zero; });
            Action(body, "適用", true, () => {
                if (!drawOrder.Contains(owner) || owner.avatar == null) { CloseDialog(); return; }
                if (!FinitePlacement(position.value) || !FinitePlacement(angles.value)) { error.text = "有限の数値を指定してください。"; return; }
                var beforePosition = owner.placementPosition; var beforeAngles = owner.placementAngles;
                var afterPosition = position.value; var afterAngles = angles.value;
                if (beforePosition != afterPosition || beforeAngles != afterAngles)
                {
                    ApplyModelPlacement(owner, afterPosition, afterAngles);
                    string ownerId=owner.id;
                    PushOperation(owner.modelSwitch, (_, forward) => {
                        var currentOwner=drawOrder.FirstOrDefault(a=>a.id==ownerId);
                        if (currentOwner?.avatar != null) ApplyModelPlacement(currentOwner, forward ? afterPosition : beforePosition, forward ? afterAngles : beforeAngles);
                    });
                }
                CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }
    }
}
