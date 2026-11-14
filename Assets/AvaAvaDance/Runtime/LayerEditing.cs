using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        VisualElement layerBar;
        DropdownField layerChoice,clipChoice;
        Button layerSettings,clipSettings;
        void BuildLayerSelector(VisualElement parent)
        {
            layerBar=Box(parent,"layer-target","row");
            layerBar.Add(new Label("編集対象"));
            layerChoice=Choice(layerBar,new[]{"ベース"});
            layerChoice.RegisterValueChangedCallback(_=>{
                var track=selectedTrack;
                if(track==null)return;
                track.ActiveLayer=layerChoice.index>0 && layerChoice.index<=track.Layers.Count ? track.Layers[layerChoice.index-1].id : null;
                track.ActiveClip=track.Target?.clips.FirstOrDefault()?.id;
                ChangeLayerContext();
            });
            clipChoice=Choice(layerBar,new[]{"作品"});
            clipChoice.RegisterValueChangedCallback(_=>{
                var layer=selectedTrack?.Target;
                if(layer!=null && clipChoice.index>=0 && clipChoice.index<layer.clips.Count) {
                    selectedTrack.ActiveClip=layer.clips[clipChoice.index].id; ChangeLayerContext();
                }
            });
            Action(layerBar,"＋レイヤー",true,()=>EditLayer(true)).focusable=false;
            layerSettings=Action(layerBar,"レイヤー設定",true,()=>EditLayer(false));
            clipSettings=Action(layerBar,"クリップ",true,EditClip);
        }
        void ChangeLayerContext()
        {
            selectedKeys.Clear();rangeSelected=false; SyncLayerSelector();SyncCurve();RefreshKeyButtons();RefreshTimelineLabels();
        }
        void SyncLayerSelector()
        {
            if(layerBar==null)return;
            var track=selectedTrack;
            bool visible=track?.SupportsLayers==true && (ribbonProfile || track.Layers.Count>0);
            layerBar.EnableInClassList("hidden",!visible);
            if(!visible)return;
            var names=new List<string>{"ベース"};
            names.AddRange(track.Layers.Select((l,i)=>(i+1)+". "+l.name));
            layerChoice.choices=names; layerChoice.SetValueWithoutNotify(names[track.Target==null?0:track.Layers.IndexOf(track.Target)+1]);
            layerChoice.SetEnabled(names.Count>1);
            var target=track.Target; layerSettings.SetEnabled(target!=null);clipSettings.SetEnabled(target!=null);
            var clips=target?.clips;
            clipChoice.EnableInClassList("hidden",clips==null || clips.Count==0);
            if(clips==null || clips.Count==0)return;
            var labels=clips.Select((c,i)=>(i+1)+". "+c.RangeLabel).ToList();
            clipChoice.choices=labels;
            clipChoice.SetValueWithoutNotify(labels[Math.Max(0,clips.FindIndex(c=>c.id==track.ActiveClip))]);
            clipChoice.SetEnabled(clips.Count>1);
        }
        void ApplyLayerEdit(MotionTrack track,List<MotionLayer> next,string active,string clip)
        {
            var prior=track.Layers.ToArray();var oldLayer=track.ActiveLayer;var oldClip=track.ActiveClip;
            void Apply(MotionTrack target,bool forward)
            {
                target.Revision++;target.Layers.Clear(); if(forward)target.Layers.AddRange(next);else target.Layers.AddRange(prior);
                target.ActiveLayer=forward?active:oldLayer;target.ActiveClip=forward?clip:oldClip;
                StopPlayback(); InvalidateDynamics(); target.Seek(frame); RefreshRigPreview(); SyncControls();ChangeLayerContext();RedrawOverlay();
            }
            Apply(track,true); PushOperation(track,Apply);
        }

        void EditLayer(bool create)
        {
            var track=selectedTrack;
            if(track?.SupportsLayers!=true || !create && track.Target==null)return;
            var index=create?track.Layers.Count:track.Layers.IndexOf(track.Target);
            var layer=create?new MotionLayer{name=(index+1).ToString("000")}:
                JsonUtility.FromJson<MotionLayer>(JsonUtility.ToJson(track.Target));
            ShowDialog(create?"レイヤー追加":"レイヤー設定",out var body);
            var name=new TextField("名前"){value=layer.name};var mute=new Toggle("ミュート"){value=layer.muted};
            var order=new IntegerField("順序"){value=index+1};body.Add(name); body.Add(mute);body.Add(order);
            var buttons=Box(body,"actions","row");
            Action(buttons,"OK",true,()=>{
                if(string.IsNullOrWhiteSpace(name.value))return;
                layer.name=name.value;layer.muted=mute.value;
                var list=track.Layers.ToList();if(!create)list.RemoveAt(index);
                list.Insert(Mathf.Clamp(order.value-1,0,list.Count),layer);
                ApplyLayerEdit(track,list,layer.id,create?null:track.ActiveClip);CloseDialog();
            });
            if(!create)Action(buttons,"削除",true,()=>{var list=track.Layers.ToList();list.RemoveAt(index);ApplyLayerEdit(track,list,null,null);CloseDialog();});
            Action(buttons,"キャンセル",true,CloseDialog);
        }

        void EditClip()
        {
            var track=selectedTrack; if(track?.Target==null)return;
            var layer=JsonUtility.FromJson<MotionLayer>(JsonUtility.ToJson(track.Target));
            var prior=layer.clips.Find(c=>c.id==track.ActiveClip);
            var clip=prior??new ClipPlacement{start=frame,cycles=1,sourceEnd=Math.Max(1,layer.keys.LastOrDefault().frame)};
            if(prior==null)clip.length=clip.sourceEnd;
            ShowDialog("モーションクリップ",out var body);
            IntegerField Integer(string label,int value){var field=new IntegerField(label){value=value};body.Add(field);return field;}
            FloatField Number(string label,float value){var field=new FloatField(label){value=value};body.Add(field);return field;}
            var parents=prior==null?layer.clips:layer.clips.Take(layer.clips.IndexOf(prior)).ToList();
            layer.sources??=new List<LayerSource>();
            var sources=new[]{(LayerSource)layer}.Concat(layer.sources).ToList();
            var sourceField=new DropdownField("モーションソース",sources.Select((s,i)=>(i+1)+". "+s.name).ToList(),
                sources.IndexOf(layer.Source(clip.source)));
            sourceField.SetEnabled(sources.Count>1);body.Add(sourceField);
            var parentField=new DropdownField("親クリップ",new[]{"作品"}.Concat(parents.Select((c,i)=>(i+1)+". "+c.RangeLabel)).ToList(),parents.FindIndex(c=>c.id==clip.parent)+1);body.Add(parentField);
            var start=Integer("配置フレーム",clip.start);var length=Integer("配置長",Math.Max(1,clip.EndFrame-clip.start));
            var first=Integer("ソース開始",clip.sourceStart);var last=Integer("ソース終了",clip.sourceEnd);
            var speed=Number("時間スケール",1f/clip.rate);var weight=Number("重み",clip.weight);
            var fadeIn=Number("ブレンドイン",clip.fadeIn);var fadeOut=Number("ブレンドアウト",clip.fadeOut);
            var range=new DropdownField("再生範囲",new List<string>{"配置長を指定","配置長の間で反復","ソースを1回再生","回数を指定して反復","無限に反復"},
                clip.infinite?4:clip.cycles>0?(clip.repeat?3:2):(clip.repeat?1:0));body.Add(range);
            var cycles=Integer("反復回数",Math.Max(2,clip.cycles));
            void RangeChanged(){length.SetEnabled(range.index<2);cycles.SetEnabled(range.index==3);fadeOut.SetEnabled(range.index!=4);}
            range.RegisterValueChangedCallback(_=>RangeChanged());RangeChanged();
            var mute=new Toggle("ミュート"){value=clip.muted};body.Add(mute);
            var warp=new Toggle("タイムワープ"){value=clip.warp.custom};body.Add(warp);
            var x1=Number("X1",clip.warp.First.x); var y1=Number("Y1",clip.warp.First.y);
            var x2=Number("X2",clip.warp.Second.x); var y2=Number("Y2",clip.warp.Second.y);
            var error=new Label(); body.Add(error);
            void Save(string active)
            {
                var list=track.Layers.ToList();list[list.IndexOf(track.Target)]=layer;
                ApplyLayerEdit(track,list,layer.id,active);CloseDialog();
            }
            var buttons=Box(body,"actions","row");
            void Store(bool copy,bool separate=false)
            {
                if(copy && layer.clips.Count>=256 || separate && layer.sources.Count>=256){error.text="配置またはソースの上限に達しました。";return;}
                var result=new ClipPlacement{id=copy?Guid.NewGuid().ToString("N"):clip.id,source=sources[sourceField.index].id,start=start.value,length=length.value,
                    parent=parentField.index>0?parents[parentField.index-1].id:null,sourceStart=first.value,sourceEnd=last.value,rate=1f/speed.value,weight=weight.value,repeat=range.index==1 || range.index>=3,muted=mute.value,
                    infinite=range.index==4,cycles=range.index==3?cycles.value:range.index>=2?1:0,
                    fadeIn=fadeIn.value,fadeOut=fadeOut.value,warp=new EaseCurve{custom=warp.value,a=new Vector2(x1.value,y1.value),b=new Vector2(x2.value,y2.value)}};
                if(range.index>=2)result.length=1;
                if(!result.Valid || (range.index==3 && cycles.value<1)){error.text="配置、範囲、倍率、回数、重み、制御点を確認してください。";return;}
                if(separate)layer.DuplicateSource(result);
                if(prior!=null && !copy)layer.clips[layer.clips.IndexOf(prior)]=result;else layer.clips.Add(result);
                Save(result.id);
            }
            Action(buttons,"OK",true,()=>Store(false));
            if(prior!=null) {
                Action(buttons,"追加配置",true,()=>Store(true));
                Action(buttons,"ソースを複製",true,()=>Store(false,true));
                Action(buttons,"削除",true,()=>{var ids=new HashSet<string>{prior.id};foreach(var c in layer.clips)if(c.parent!=null && ids.Contains(c.parent))ids.Add(c.id);
                    layer.clips.RemoveAll(c=>ids.Contains(c.id));Save(layer.clips.FirstOrDefault()?.id);});
            }
            Action(buttons,"キャンセル",true,CloseDialog);
        }
    }
}
