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
            body.Add(new Label("既存キーを変えずに位置と回転を調整します。"));
            FloatField[] VectorFields(string name, string title, Vector3 value)
            {
                body.Add(new Label(title));
                return Enumerable.Range(0, 3).Select(axis => {
                    var field = new FloatField("XYZ"[axis].ToString()) {
                        name = name + "-" + "xyz"[axis], value = value[axis], isDelayed = true
                    };
                    body.Add(field); return field;
                }).ToArray();
            }
            Vector3 Read(FloatField[] fields) => new Vector3(fields[0].value, fields[1].value, fields[2].value);
            void Set(FloatField[] fields, Vector3 value) { for (int i = 0; i < 3; i++) fields[i].value = value[i]; }
            var position = VectorFields("model-placement-position", "位置（m）", owner.placementPosition);
            var angles = VectorFields("model-placement-rotation", "回転（度）", owner.placementAngles);
            var error = new Label(); body.Add(error);
            Action(body, "初期化", true, () => { Set(position, Vector3.zero); Set(angles, Vector3.zero); });
            Action(body, "適用", true, () => {
                if (!drawOrder.Contains(owner) || owner.avatar == null) { CloseDialog(); return; }
                var afterPosition = Read(position); var afterAngles = Read(angles);
                if (!FinitePlacement(afterPosition) || !FinitePlacement(afterAngles)) { error.text = "有限の数値を指定してください。"; return; }
                var beforePosition = owner.placementPosition; var beforeAngles = owner.placementAngles;
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
