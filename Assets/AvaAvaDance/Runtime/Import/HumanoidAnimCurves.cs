using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityEngine;

namespace AvaAvaDance.Import
{
    // Read Unity's serialized Animator float bindings. m_EditorCurves is an editor-only duplicate.
    internal sealed class HumanoidAnimCurves
    {
        readonly AnimationCurve[] curves = new AnimationCurve[HumanTrait.MuscleCount+7];
        internal float Duration { get; private set; }
        static readonly Dictionary<string,int> bindings = Bindings();
        static Dictionary<string,int> Bindings()
        {
            var names = new Dictionary<string,int>(StringComparer.Ordinal);
            var muscles = HumanTrait.MuscleName;
            for (int i=0; i<muscles.Length; i++)
            {
                names[muscles[i]] = i;
                // Unity serializes e.g. "Left Index 1 Stretched" as "LeftHand.Index.1 Stretched".
                var parts = muscles[i].Split(' ');
                if (parts.Length >= 3 && Array.IndexOf(new[]{"Thumb","Index","Middle","Ring","Little"},parts[1])>=0)
                    names[parts[0]+"Hand."+parts[1]+"."+string.Join(" ",parts,2,parts.Length-2)] = i;
            }
            for (int i=0; i<7; i++) names[(i<3?"RootT.":"RootQ.")+"xyzw"[i<3?i:i-3]] = muscles.Length+i;
            return names;
        }

        internal void Sample(float time, ref HumanPose pose, Vector3 restPosition, Quaternion restRotation)
        {
            int n = pose.muscles.Length;
            for (int i=0; i<n; i++) pose.muscles[i] = curves[i]?.Evaluate(time) ?? 0;
            for (int i=0; i<3; i++) restPosition[i] = curves[n+i]?.Evaluate(time) ?? restPosition[i];
            for (int i=0; i<4; i++) restRotation[i] = curves[n+3+i]?.Evaluate(time) ?? restRotation[i];
            if (!float.IsFinite(restPosition.sqrMagnitude) || !(Quaternion.Dot(restRotation,restRotation)>1e-8f))
                throw new InvalidDataException("anim のルート姿勢が不正です。");
            pose.bodyPosition=restPosition; pose.bodyRotation=restRotation.normalized;
        }

        void Bind(string attribute, bool animator, bool root, List<Keyframe> keys)
        {
            if (!animator || !root || attribute==null || !bindings.TryGetValue(attribute,out int id) || keys.Count==0) return;
            if (curves[id]!=null) throw new InvalidDataException("anim のカーブが重複しています。 "+attribute);
            float previous = -1;
            foreach (var k in keys)
            {
                if (!float.IsFinite(k.time) || k.time<0 || k.time<=previous || !float.IsFinite(k.value))
                    throw new InvalidDataException("anim のキー時刻または値が不正です。");
                previous = k.time;
            }
            curves[id] = new AnimationCurve(keys.ToArray());
            Duration = Mathf.Max(Duration,previous);
        }

        static float Number(string s, bool tangent=false)
        {
            if (tangent && (s==".inf" || s=="Infinity")) return float.PositiveInfinity;
            if (tangent && (s=="-.inf" || s=="-Infinity")) return float.NegativeInfinity;
            if (float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out float v) && float.IsFinite(v)) return v;
            throw new InvalidDataException("anim の数値が不正です。");
        }

        internal static HumanoidAnimCurves Read(FileStream input)
        {
            int first=input.ReadByte();
            input.Position=0;
            if (first==0) return ReadBinary(input);
            using var reader=new StreamReader(input);
            return ReadText(reader);
        }

        static HumanoidAnimCurves ReadBinary(FileStream input)
        {
            var manager=new AssetsManager();
            try
            {
                var file=manager.LoadAssetsFile(input,false);
                if (!file.file.Metadata.TypeTreeEnabled)
                    throw new InvalidDataException("型情報のないバイナリ .anim は Unity の Force Text で保存してください。");
                var info=file.file.GetAssetsOfType(AssetClassID.AnimationClip).Single();
                var asset=manager.GetBaseField(file,info);
                var clip=new HumanoidAnimCurves();
                var stop=asset["m_AnimationClipSettings"]["m_StopTime"];
                if (!stop.IsDummy) clip.Duration=stop.AsFloat;
                foreach (var binding in asset["m_FloatCurves"]["Array"].Children)
                {
                    string attribute=binding["attribute"].AsString;
                    if (!bindings.ContainsKey(attribute) || binding["classID"].AsInt!=95 || binding["path"].AsString.Length!=0) continue;
                    var keys=new List<Keyframe>();
                    foreach (var k in binding["curve"]["m_Curve"]["Array"].Children)
                    {
                        var key=new Keyframe(k["time"].AsFloat,k["value"].AsFloat,k["inSlope"].AsFloat,k["outSlope"].AsFloat);
                        if (!k["weightedMode"].IsDummy)
                        {
                            key.inWeight=k["inWeight"].AsFloat; key.outWeight=k["outWeight"].AsFloat;
                            key.weightedMode=(WeightedMode)k["weightedMode"].AsInt;
                        }
                        keys.Add(key);
                    }
                    clip.Bind(attribute,true,true,keys);
                }
                clip.RequireHumanoid();
                return clip;
            }
            finally { manager.UnloadAll(); }
        }

        void RequireHumanoid()
        {
            if (!Array.Exists(curves,c=>c!=null)) throw new InvalidDataException("Unity Humanoid の筋肉またはルートカーブを含む .anim を選択してください。");
        }

        static HumanoidAnimCurves ReadText(TextReader reader)
        {
            var clip = new HumanoidAnimCurves();
            bool inClip=false, inCurves=false, animator=false, root=false, haveTime=false, haveValue=false;
            var keys = new List<Keyframe>();
            Keyframe key = default;
            string attribute=null;
            int budget=2000000;
            void EndKey()
            {
                if (!haveTime) return;
                if (!haveValue || --budget<0) throw new InvalidDataException("anim のキーデータが不正または大きすぎます。");
                keys.Add(key); haveTime=false; haveValue=false;
            }
            void EndCurve()
            {
                EndKey(); clip.Bind(attribute,animator,root,keys);
                keys.Clear(); attribute=null; animator=false; root=false;
            }
            for (string line=reader.ReadLine(); line!=null; line=reader.ReadLine())
            {
                if (line=="AnimationClip:") { inClip=true; continue; }
                if (!inClip) continue;
                if (line.StartsWith("  m_",StringComparison.Ordinal))
                {
                    if (inCurves) EndCurve();
                    inCurves=line.TrimEnd()=="  m_FloatCurves:";
                    if (line.StartsWith("  m_EditorCurves:",StringComparison.Ordinal)) break;
                    continue;
                }
                if (!inCurves)
                {
                    if (line.StartsWith("    m_StopTime:",StringComparison.Ordinal))
                        clip.Duration=Mathf.Max(clip.Duration,Number(line.Substring(line.IndexOf(':')+1)));
                    continue;
                }
                if (line.StartsWith("  - ",StringComparison.Ordinal)) { EndCurve(); continue; }
                if (line.StartsWith("        ",StringComparison.Ordinal))
                {
                    int colon=line.IndexOf(':',8);
                    if (colon<0) continue;
                    string field=line.Substring(8,colon-8), value=line.Substring(colon+1).Trim();
                    switch (field)
                    {
                        case "time": EndKey(); key=new Keyframe(Number(value),0); haveTime=true; break;
                        case "value": key.value=Number(value); haveValue=true; break;
                        case "inSlope": key.inTangent=Number(value,true); break;
                        case "outSlope": key.outTangent=Number(value,true); break;
                        case "weightedMode": key.weightedMode=(WeightedMode)(int)Number(value); break;
                        case "inWeight": key.inWeight=Number(value); break;
                        case "outWeight": key.outWeight=Number(value); break;
                    }
                }
                else if (line.StartsWith("    attribute:",StringComparison.Ordinal)) attribute=line.Substring(14).Trim();
                else if (line.StartsWith("    classID:",StringComparison.Ordinal)) animator=line.Substring(12).Trim()=="95";
                else if (line.StartsWith("    path:",StringComparison.Ordinal)) root=line.Substring(9).Trim().Length==0;
            }
            if (inCurves) EndCurve();
            clip.RequireHumanoid();
            return clip;
        }
    }
}
