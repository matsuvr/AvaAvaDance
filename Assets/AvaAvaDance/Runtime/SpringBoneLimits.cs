using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniGLTF.SpringBoneJobs;
using UniVRM10;
using UniVRM10.FastSpringBones;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] struct SpringBoneLimit
        {
            public int joint;
            public AnglelimitTypes mode;
            public float pitch, yaw;
            public Quaternion rotation;

            internal bool Valid => (uint)mode <= (uint)AnglelimitTypes.Spherical &&
                pitch >= 0 && pitch <= Mathf.PI && yaw >= 0 && yaw <= Mathf.PI / 2 &&
                Mathf.Abs(Quaternion.Dot(rotation, rotation) - 1) < .001f;

            internal static SpringBoneLimit Read(int index, VRM10SpringBoneJoint component) => new SpringBoneLimit {
                joint = index, mode = component.m_anglelimitType, pitch = component.m_pitch,
                yaw = component.m_yaw, rotation = component.m_limitSpaceOffset
            };

            internal void Write(VRM10SpringBoneJoint component)
            {
                component.m_anglelimitType = mode;
                component.m_pitch = pitch;
                component.m_yaw = yaw;
                component.m_limitSpaceOffset = rotation;
            }
        }

        sealed class EditableSpringJoint
        {
            internal VRM10SpringBoneJoint Component;
            internal SpringBoneLimit Original;
        }

        FastSpringBoneCombinedBuffer springBoneLimitBuffer;

        static void BindSpringBoneLimits(Actor owner)
        {
            foreach (var spring in owner.vrm.SpringBone.Springs)
                for (int i = 0; i + 1 < spring.Joints.Count; i++) {
                    var joint = spring.Joints[i];
                    if (joint != null && owner.rigJointIndices.TryGetValue(joint.transform, out int index))
                        owner.springJoints.TryAdd(index, new EditableSpringJoint {
                            Component = joint, Original = SpringBoneLimit.Read(index, joint)
                        });
                }
        }

        static void RestoreSpringBoneLimits(Actor owner, SpringBoneLimit[] limits)
        {
            if (limits == null || limits.Length == 0) return;
            var joints = new HashSet<int>();
            foreach (var limit in limits)
                if (!limit.Valid || !owner.springJoints.ContainsKey(limit.joint) || !joints.Add(limit.joint))
                    throw new InvalidDataException("スプリングボーンの角度制限設定が不正です。");

            foreach (var limit in limits) limit.Write(owner.springJoints[limit.joint].Component);
            owner.springBoneLimits = limits;
            owner.springBoneLimitsDirty = true;
        }

        static void SetSpringBoneLimit(Actor owner, EditableSpringJoint joint, SpringBoneLimit limit)
        {
            limit.Write(joint.Component);
            // Replace the array so an asynchronous project capture retains its snapshot.
            var limits = owner.springBoneLimits.Where(saved => saved.joint != limit.joint);
            if (!limit.Equals(joint.Original)) limits = limits.Append(limit);
            owner.springBoneLimits = limits.OrderBy(saved => saved.joint).ToArray();
            owner.springBoneLimitsDirty = true;
        }

        void SyncSpringBoneLimits()
        {
            if (!drawOrder.Any(owner => owner.springBoneLimitsDirty || owner.springBoneLimits.Length != 0)) {
                springBoneLimitBuffer = null;
                return;
            }
            var combiner = FastSpringBoneService.Instance.BufferCombiner;
            // UniVRM copies the original joint settings when it combines models.
            // Complete that copy and reapply overrides before its LateUpdate runs the solver.
            combiner.ReconstructIfDirty(default).Complete();
            var combined = combiner.Combined;
            if (combined == null) return;
            bool rebuilt = !ReferenceEquals(springBoneLimitBuffer, combined);
            foreach (var owner in drawOrder) {
                if (owner.springBoneLimitsDirty) {
                    foreach (var joint in owner.springJoints.Values)
                        combined.SetJointLevel(joint.Component.transform, joint.Component.Blittable);
                    owner.springBoneLimitsDirty = false;
                } else if (rebuilt) {
                    foreach (var limit in owner.springBoneLimits) {
                        var joint = owner.springJoints[limit.joint].Component;
                        combined.SetJointLevel(joint.transform, joint.Blittable);
                    }
                }
            }
            springBoneLimitBuffer = combined;
        }

        void ShowSpringBoneLimits()
        {
            var owner = actor;
            var joints = owner.springJoints.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            if (joints.Length == 0) return;
            string Text(string japanese, string english) => englishMenus ? english : japanese;
            ShowDialog(Text("スプリングボーンの角度制限", "Spring Bone Angle Limits"), out var body);
            var jointField = new DropdownField(Text("関節", "Joint"),
                joints.Select(j => $"{j.Component.name} ({j.Original.joint})").ToList(),
                Math.Max(0, Array.FindIndex(joints, j => j.Component.transform == owner.selectedBone?.Joint))) {
                name = "spring-bone-joint"
            };
            var mode = new DropdownField(Text("制限", "Limit"), new List<string> {
                Text("なし", "None"), Text("円錐", "Cone"), Text("ヒンジ", "Hinge"), Text("球面", "Spherical")
            }, 0) { name = "spring-bone-limit-mode" };
            var pitch = new Slider(0, 180) { name = "spring-bone-pitch", showInputField = true };
            var yaw = new Slider(Text("ヨー（度）", "Yaw (°)"), 0, 90) { name = "spring-bone-yaw", showInputField = true };
            var rotation = new Vector3Field(Text("制限軸の回転（度）", "Limit axis rotation (°)")) { name = "spring-bone-rotation" };
            body.Add(jointField); body.Add(mode); body.Add(pitch); body.Add(yaw); body.Add(rotation);
            var error = new Label(); body.Add(error);

            EditableSpringJoint Selected() => joints[jointField.index];
            void UpdateFields()
            {
                var type = (AnglelimitTypes)mode.index;
                pitch.label = type == AnglelimitTypes.Spherical ? Text("ピッチ（度）", "Pitch (°)") : Text("角度（度）", "Angle (°)");
                pitch.style.display = rotation.style.display = type == AnglelimitTypes.None ? DisplayStyle.None : DisplayStyle.Flex;
                yaw.style.display = type == AnglelimitTypes.Spherical ? DisplayStyle.Flex : DisplayStyle.None;
            }
            void LoadFields()
            {
                var joint = Selected();
                var limit = SpringBoneLimit.Read(joint.Original.joint, joint.Component);
                mode.index = (int)limit.mode;
                pitch.SetValueWithoutNotify(limit.pitch * Mathf.Rad2Deg);
                yaw.SetValueWithoutNotify(limit.yaw * Mathf.Rad2Deg);
                rotation.SetValueWithoutNotify(limit.rotation.eulerAngles);
                error.text = "";
                UpdateFields();
            }
            mode.RegisterValueChangedCallback(_ => UpdateFields());
            jointField.RegisterValueChangedCallback(_ => LoadFields());
            Action(body, Text("適用", "Apply"), true, () => {
                if (actor != owner || owner.avatar == null) { CloseDialog(); return; }
                var joint = Selected();
                var limit = SpringBoneLimit.Read(joint.Original.joint, joint.Component);
                limit.mode = (AnglelimitTypes)mode.index;
                if (limit.mode != AnglelimitTypes.None) {
                    limit.pitch = pitch.value * Mathf.Deg2Rad;
                    if (limit.mode == AnglelimitTypes.Spherical) limit.yaw = yaw.value * Mathf.Deg2Rad;
                    if (!FinitePlacement(rotation.value)) {
                        error.text = Text("回転には有限の数値を指定してください。", "Enter finite values for the axis rotation.");
                        return;
                    }
                    if (!rotation.value.Equals(limit.rotation.eulerAngles)) limit.rotation = Quaternion.Euler(rotation.value);
                }
                if (!limit.Valid) { error.text = Text("角度の設定が不正です。", "Invalid angle settings."); return; }
                SetSpringBoneLimit(owner, joint, limit);
                LoadFields();
            }).name = "spring-bone-apply";
            Action(body, Text("モデルの設定に戻す", "Restore model setting"), true, () => {
                if (actor != owner || owner.avatar == null) { CloseDialog(); return; }
                var joint = Selected();
                SetSpringBoneLimit(owner, joint, joint.Original);
                LoadFields();
            }).name = "spring-bone-restore";
            Action(body, Text("閉じる", "Close"), true, CloseDialog);
            LoadFields();
        }
    }
}
