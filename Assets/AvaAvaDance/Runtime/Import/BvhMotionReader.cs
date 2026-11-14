using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UniHumanoid;
using UnityEngine;

namespace AvaAvaDance.Import
{
    internal static class BvhMotionReader
    {
        sealed class Joint
        {
            internal string Name;
            internal int Parent, First;
            internal Vector3 Offset;
            internal BvhChannel[] Channels;
            internal bool Absolute;

            internal void Pose(float[] values, int at, out Vector3 p, out Quaternion q)
            {
                p = Offset;
                q = Quaternion.identity;
                foreach (var channel in Channels)
                {
                    int k = (int)channel;
                    float value = values[at++];
                    if (k < 3) p[k] = value + (Absolute ? 0 : Offset[k]);
                    else q *= Quaternion.AngleAxis(value, k==3 ? Vector3.right : k==4 ? Vector3.up : Vector3.forward);
                }
                p.x = -p.x;
                q.y = -q.y; q.z = -q.z;
            }
        }

        static InvalidDataException Bad() => new InvalidDataException("BVH の階層・チャンネルまたはフレームデータが不正です。");
        static float Number(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : throw Bad();
        static string[] Fields(string s) => s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        internal static HumanoidSkeletonMotion Read(TextReader reader)
        {
            string Line()
            {
                string s;
                do { s = reader.ReadLine(); if (s == null) throw Bad(); } while (string.IsNullOrWhiteSpace(s));
                return s.Trim();
            }
            if (Line() != "HIERARCHY") throw Bad();
            var joints = new List<Joint>();
            int width = 0;
            void ReadJoint(string header, int parent, int depth)
            {
                string prefix = parent<0 ? "ROOT " : "JOINT ";
                if (depth > 128 || joints.Count >= 2048 || !header.StartsWith(prefix) || Line() != "{") throw Bad();
                var offset = Fields(Line());
                if (offset.Length != 4 || offset[0] != "OFFSET") throw Bad();
                var fields = Fields(Line());
                if (fields.Length < 2 || fields[0] != "CHANNELS" || !int.TryParse(fields[1],out int count) ||
                    count < 0 || count > 6 || fields.Length != count+2) throw Bad();
                var channels = fields.Skip(2).Select(s => Enum.TryParse<BvhChannel>(s,out var c) && Enum.IsDefined(typeof(BvhChannel),c) ? c : throw Bad()).ToArray();
                if (channels.Distinct().Count() != count) throw Bad();
                int index = joints.Count;
                joints.Add(new Joint {Name=header.Substring(prefix.Length).Trim(), Parent=parent, First=width, Channels=channels,
                    Offset=new Vector3(Number(offset[1]), Number(offset[2]), Number(offset[3]))});
                width += count;
                for (string child = Line(); child != "}"; child = Line())
                {
                    if (child == "End Site") { if (Line() != "{" || !Line().StartsWith("OFFSET ") || Line() != "}") throw Bad(); }
                    else ReadJoint(child,index,depth+1);
                }
            }
            ReadJoint(Line(),-1,0);
            if (Line() != "MOTION") throw Bad();
            string After(string field)
            {
                string line = Line();
                if (!line.StartsWith(field,StringComparison.Ordinal)) throw Bad();
                return line.Substring(field.Length).Trim();
            }
            if (!int.TryParse(After("Frames:"), out int frames) || frames <= 0 || width == 0 || (long)frames*width > 16000000) throw Bad();
            double step = Number(After("Frame Time:"));
            if (!(step > 0)) throw Bad();
            var values = new float[frames*width];
            for (int f=0; f<frames; f++)
            {
                var fields = Fields(Line());
                if (fields.Length != width) throw Bad();
                for (int i=0; i<width; i++) values[f*width+i] = Number(fields[i]);
            }

            var rest = new Matrix4x4[joints.Count];
            for (int n=0; n<joints.Count; n++)
            {
                var j = joints[n];
                var offset = j.Offset;
                rest[n] = Matrix4x4.Translate(new Vector3(-offset.x,offset.y,offset.z));
                // Some exporters write absolute local positions (also on child joints).
                // Select the convention nearest the bind offset on the translated axes.
                float absolute = 0, relative = 0;
                for (int c=0; c<j.Channels.Length; c++)
                {
                    int axis = (int)j.Channels[c];
                    if (axis >= 3) continue;
                    float v = values[j.First+c];
                    absolute += Mathf.Pow(v-offset[axis],2); relative += v*v;
                }
                j.Absolute = absolute < relative;
            }
            void Sample(double seconds, Matrix4x4[] local)
            {
                double sample = Math.Min(seconds/step, frames-1);
                int a = (int)sample, b = Math.Min(a+1,frames-1);
                float t = (float)(sample-a);
                for (int i=0; i<local.Length; i++)
                {
                    var j = joints[i];
                    j.Pose(values,a*width+j.First,out var p,out var q);
                    j.Pose(values,b*width+j.First,out var p2,out var q2);
                    local[i] = Matrix4x4.TRS(Vector3.Lerp(p,p2,t),Quaternion.Slerp(q,q2,t),Vector3.one);
                }
            }
            return new HumanoidSkeletonMotion(joints.Select(j=>j.Name).ToArray(),joints.Select(j=>j.Parent).ToArray(),rest,(frames-1)*step,Sample);
        }
    }
}
