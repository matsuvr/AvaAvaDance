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
            public bool layerGroup;
            public bool HasKey(int at)
            {
                if(members!=null)return layerGroup ? members.Any(t=>HasLayerKey(t.GroupedLayer,at)) : members.Any(t=>t.KeyAt(at)!=null);
                if(layer==null)return track.KeyAt(at)!=null;
                return HasLayerKey(layer,at);
            }
            static bool HasLayerKey(MotionLayer layer,int at)
            {
                if(layer.clips.Count==0)return layer.At(at).HasValue;
                return layer.clips.Any(c=>layer.Map(c,at,out var local,out _) && Math.Abs(local-Mathf.Round(local))<.01f && layer.Source(c.source).At(Mathf.RoundToInt(local)).HasValue);
            }
        }
        readonly List<RibbonRow> ribbonRows=new List<RibbonRow>();
        bool ribbonHasLayerGroups;
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
            void Track(MotionTrack t,MotionLayer grouped=null)
            {
                ribbonRows.Add(new RibbonRow{track=t,layer=grouped,label=(grouped==null?"":"  ")+DisplayName(t)});
                if(grouped!=null && selectedTrack==t && t.Target==null)
                    ribbonRows.Add(new RibbonRow{track=t,label="    ベース"});
                foreach(var l in t.Layers)
                    if(l!=grouped)ribbonRows.Add(new RibbonRow{track=t,layer=l,label=(grouped==null?"  ":"    ")+l.name});
            }
            var tracks=VisibleTracks.Select(t=>(track:t,layer:t.GroupedLayer)).ToArray();
            var groups=tracks.Where(t=>t.layer!=null).GroupBy(t=>t.layer.GroupId).OrderBy(g=>g.Key,StringComparer.Ordinal).ToArray();
            ribbonHasLayerGroups=groups.Length>0;
            foreach(var group in groups)
            {
                string name=group.First().layer.group;
                bool open=WorkspacePreferences.GetInt(RibbonGroupPreference(group.Key))==1;
                ribbonRows.Add(new RibbonRow{group=group.Key,label=(open?"− ":"+ ")+name,
                    layerGroup=true,members=group.Select(t=>t.track).ToArray()});
                if(open)foreach(var entry in group)Track(entry.track,entry.layer);
            }
            foreach(var family in tracks.Where(t=>t.layer==null).Select(t=>t.track).GroupBy(t=>modelMode?TrackFamily(t):null))
            {
                if(family.Key==null){foreach(var t in family)Track(t);continue;}
                bool open=WorkspacePreferences.GetInt(RibbonGroupPreference(family.Key))==1;
                ribbonRows.Add(new RibbonRow{group=family.Key,label=(open?"− ":"+ ")+family.Key,members=family.ToArray()});
                if(open)foreach(var t in family)Track(t);
            }
        }
        int TimelineRows => ribbonProfile?ribbonRows.Count:VisibleTracks.Count;
        static string RibbonGroupPreference(string group) => "Ribbon.Group."+group;
        void ChooseGroup(RibbonRow line,bool label,int at,bool toggle)
        {
            if(label && (toggle || line.layerGroup))
            {
                string key=RibbonGroupPreference(line.group);
                WorkspacePreferences.SetInt(key,WorkspacePreferences.GetInt(key)==0?1:0);
                if(line.layerGroup){RefreshTimelineLabels();return;}
            }
            var lead=line.members.FirstOrDefault();
            if(lead==null)return;
            if(line.layerGroup)
                foreach(var track in line.members)SetRibbonLayer(track,track.GroupedLayer,false,at);
            SelectTrack(lead);selectedRows.UnionWith(line.members);
            boneSelection.AddRange(actor.bones.Where(b=>selectedRows.Contains(b.Track)));
            if(!label)
            {
                rangeSelected=true;
                foreach(var t in line.members)
                {
                    int local=at;
                    if(line.layerGroup && !t.LocalTime(at,out local))continue;
                    if(t.EditKey(local)!=null)selectedKeys.Add((t,local));
                }
            }
            RefreshTimelineLabels();RefreshKeyButtons();SyncCurve();RedrawOverlay();
        }
    }
}
