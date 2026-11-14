using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class RibbonRow
        {
            public string label,group;
            public MotionTrack track;
            public MotionLayer layer;
            public MotionTrack[] members;
            public bool HasKey(int at)
            {
                if(members!=null)return members.Any(t=>t.KeyAt(at)!=null);
                if(layer==null)return track.KeyAt(at)!=null;
                if(layer.clips.Count==0)return layer.At(at).HasValue;
                return layer.clips.Any(c=>layer.Map(c,at,out var local,out _) && Math.Abs(local-Mathf.Round(local))<.01f && layer.Source(c.source).At(Mathf.RoundToInt(local)).HasValue);
            }
        }
        readonly List<RibbonRow> ribbonRows=new List<RibbonRow>();
        string TrackFamily(MotionTrack track)
        {
            if(track is MotionTrack<float>)return "表情";
            if(actor.ikChains.Any(c=>c.Handle.Track==track))return "IK";
            var bone=actor.bones.Find(b=>b.Track==track);
            if(bone==null || actor.humanBones==null)return null;
            var pair=actor.humanBones.FirstOrDefault(p=>p.Value==bone.Joint);
            if(pair.Value==null)return null;
            string name=pair.Key.ToString();
            if(name.Contains("Proximal")||name.Contains("Intermediate")||name.Contains("Distal"))return "指";
            if(name.Contains("Arm")||name.Contains("Hand")||name.Contains("Shoulder"))return "腕";
            if(name.Contains("Leg")||name.Contains("Foot")||name.Contains("Toes"))return "足";
            return name.Contains("Head")||name.Contains("Eye")||name.Contains("Jaw")?"顔":name=="Hips"?null:"体（上）";
        }
        void BuildRibbonRows()
        {
            ribbonRows.Clear();
            void Track(MotionTrack t)
            {
                ribbonRows.Add(new RibbonRow{track=t,label=DisplayName(t)});
                foreach(var l in t.Layers)ribbonRows.Add(new RibbonRow{track=t,layer=l,label="  "+l.name});
            }
            foreach(var family in VisibleTracks.GroupBy(t=>modelMode?TrackFamily(t):null))
            {
                if(family.Key==null){foreach(var t in family)Track(t);continue;}
                bool open=WorkspacePreferences.GetInt("Ribbon.Group."+family.Key)==1;
                ribbonRows.Add(new RibbonRow{group=family.Key,label=(open?"− ":"+ ")+family.Key,members=family.ToArray()});
                if(open)foreach(var t in family)Track(t);
            }
        }
        int TimelineRows => ribbonProfile?ribbonRows.Count:VisibleTracks.Count;
        void ChooseGroup(RibbonRow line,bool label,int at,bool toggle)
        {
            if(label && toggle){string key="Ribbon.Group."+line.group;WorkspacePreferences.SetInt(key,WorkspacePreferences.GetInt(key)==0?1:0);}
            var lead=line.members.FirstOrDefault();
            if(lead==null)return;
            SelectTrack(lead);selectedRows.UnionWith(line.members);
            boneSelection.AddRange(actor.bones.Where(b=>selectedRows.Contains(b.Track)));
            if(!label){rangeSelected=true;foreach(var t in line.members)if(t.EditKey(at)!=null)selectedKeys.Add((t,at));}
            RefreshTimelineLabels();RefreshKeyButtons();SyncCurve();RedrawOverlay();
        }
    }
}
