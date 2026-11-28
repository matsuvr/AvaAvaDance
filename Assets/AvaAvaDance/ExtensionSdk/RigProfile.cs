using System;
using System.Linq;

namespace Ava.Extensions
{
    [Serializable] public sealed class RigJoint
    {
        public string name, role, origin, ikRoot;
        public int parent;
        public double x,y,z;
        public float qx,qy,qz,qw=1,scaleX=1,scaleY=1,scaleZ=1;
    }
    [Serializable] public sealed class RigShape
    {
        public string name,role,shape="sphere";
        public double radius,height;
        public double[] center,axis,boxAxes;
        public int jointIndex=-1;
    }

    [Serializable] public sealed class RigProfile
    {
        public string format="AvaAvaDance rig 1", name, sourceId,sourceHash;
        public RigJoint[] editRest;
        public string[] morphs;
        public RigShape[] shapes;
        public int schemaVersion=1,measurementVersion;
        public double metersPerMotionUnit=.1;
        public string motionEncoding="AvaLocalV1",cameraEncoding="AvaCameraV1";
        public RigJoint[] joints;
        public static string MotionAlias(string role)
        {
            if(string.IsNullOrEmpty(role))return "";
            role=role.Replace(" ","").ToLowerInvariant();
            string side=role.StartsWith("left")?"左":role.StartsWith("right")?"右":"";
            if(side.Length>0)role=role.Substring(side=="左"?4:5);
            string name=role switch {"hips"=>"センター","spine"=>"上半身","chest"=>"上半身2","neck"=>"首","head"=>"頭","shoulder"=>"肩",
                "upperarm"=>"腕","armtwist"=>"腕捩","lowerarm"=>"ひじ","wristtwist"=>"手捩","hand"=>"手首","footik"=>"足ＩＫ","toesik"=>"つま先ＩＫ","upperleg"=>"足","lowerleg"=>"ひざ","foot"=>"足首","toes"=>"つま先","eye"=>"目",_=>""};
            if(name==""){
                string finger=role.Contains("thumb")?"親":role.Contains("index")?"人":role.Contains("middle")?"中":role.Contains("ring")?"薬":role.Contains("little")?"小":"";
                if(finger!="")
                {
                    string part=role.EndsWith("distal")?"３":role.EndsWith("intermediate")?"２":role.EndsWith("proximal")?"１":"０";
                    name=finger+"指"+part;
                }
            }
            return name==""?"":side+name;
        }
        public RigJoint Find(string role, params string[] aliases)
        {
            return joints.FirstOrDefault(j=>!string.IsNullOrEmpty(role) && string.Equals(j.role,role,StringComparison.OrdinalIgnoreCase)) ??
                joints.FirstOrDefault(j=>aliases.Append(role).Contains(j.name,StringComparer.OrdinalIgnoreCase));
        }
    }
}
