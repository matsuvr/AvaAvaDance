using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AvaAvaDance.Import
{
    internal static class HumanoidPreview
    {
        // ModelImporter records the avatar's bone mapping even when the FBX uses localized names.
        internal static bool TryPose(GameObject avatar, string meta, out Vector3 facing)
        {
            facing=Vector3.forward;
            if (meta == null || meta.Length > 4 * 1024 * 1024) return false;
            var names = BoneMap(meta);
            var nodes=new Dictionary<string,Transform>();
            foreach (var t in avatar.GetComponentsInChildren<Transform>()) nodes[t.name]=t;
            Transform Bone(string name) => names.TryGetValue(name,out var mapped) && nodes.TryGetValue(mapped,out var t) ? t : null;
            var left=Bone("LeftUpperArm"); var right=Bone("RightUpperArm");
            if (!left || !right) return false;
            var side=Vector3.ProjectOnPlane(right.position-left.position,Vector3.up).normalized;
            if(side.sqrMagnitude<0.5f) return false;
            facing=Vector3.Cross(side,Vector3.up);
            LowerArm(left,Bone("LeftLowerArm"),-side);
            LowerArm(right,Bone("RightLowerArm"),side);
            return true;
        }

        internal static Dictionary<string,string> BoneMap(string meta)
        {
            var names=new Dictionary<string,string>();
            if (meta == null || meta.Length > 4 * 1024 * 1024) return names;
            using (var reader=new StringReader(meta))
            {
                string bone=null,line;
                while ((line=reader.ReadLine())!=null)
                {
                    string field=line.TrimStart();
                    if (field.StartsWith("- boneName: ",StringComparison.Ordinal)) bone=field.Substring(12);
                    else if (bone!=null && field.StartsWith("humanName: ",StringComparison.Ordinal))
                    { names[field.Substring(11)]=bone; bone=null; }
                }
            }
            return names;
        }

        private static void LowerArm(Transform shoulder, Transform elbow, Vector3 outward)
        {
            if (!elbow) return;
            var direction=(elbow.position-shoulder.position).normalized;
            // Relax a horizontal bind pose only. Preserve an existing posed arm.
            if (Vector3.Dot(direction,outward)>0.85f)
                shoulder.rotation=Quaternion.FromToRotation(direction,(outward*.48f+Vector3.down).normalized)*shoulder.rotation;
        }
    }
}
