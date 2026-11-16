using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const string OutputPresetPreference = "Output.Presets";
        [Serializable] sealed class SavedOutputPresets { public OutputPreset[] items; }

        OutputPreset[] ReadOutputPresets()
        {
            string json=WorkspacePreferences.GetString(OutputPresetPreference);
            if(!string.IsNullOrEmpty(json)) {
                try {
                    var saved=JsonUtility.FromJson<SavedOutputPresets>(json);
                    if(saved?.items!=null)return saved.items.Where(p=>p!=null && p.Valid).ToArray();
                } catch(ArgumentException) { }
            }
            return OutputPreset.Defaults();
        }

        static MovieSettings CopyOutputSettings(MovieSettings source) => new MovieSettings {
            width=source.width, height=source.height, fps=source.fps,
            imageWidth=source.imageWidth, imageHeight=source.imageHeight,
            first=source.first, last=source.last, audio=source.audio
        };

        // The form owns its working settings. Cancelling it never changes the
        // project, and opening the preset editor preserves all in-progress fields.
        void AddOutputPresetFields(VisualElement body, MovieSettings working, Action reopen, bool image=false)
        {
            var presets=ReadOutputPresets();
            var choices=new List<string>{"カスタム"};
            choices.AddRange(presets.Select(p=>p.name+"　"+p.width+" × "+p.height+(image?"":"　"+p.fps+" fps")));
            var picker=new DropdownField("プリセット",choices,0){name="output-preset"};
            body.Add(picker);
            Action(body,"プリセット編集…",true,()=>EditOutputPresets(working,reopen));
            var width=new IntegerField("幅"){name="output-width",value=working.width,isDelayed=true};
            var height=new IntegerField("高さ"){name="output-height",value=working.height,isDelayed=true};
            var fps=new IntegerField("フレームレート"){name="output-fps",value=working.fps,isDelayed=true};
            body.Add(width);body.Add(height);if(!image)body.Add(fps);
            void SyncPicker() {
                int index=Array.FindIndex(presets,p=>p.width==working.width && p.height==working.height && (image || p.fps==working.fps));
                picker.SetValueWithoutNotify(choices[index+1]);
            }
            width.RegisterValueChangedCallback(e=>{working.width=e.newValue;SyncPicker();});
            height.RegisterValueChangedCallback(e=>{working.height=e.newValue;SyncPicker();});
            fps.RegisterValueChangedCallback(e=>{working.fps=e.newValue;SyncPicker();});
            picker.RegisterValueChangedCallback(_=>{
                if(picker.index<=0)return;
                var preset=presets[picker.index-1];
                working.width=preset.width;working.height=preset.height;working.fps=preset.fps;
                width.SetValueWithoutNotify(working.width);height.SetValueWithoutNotify(working.height);fps.SetValueWithoutNotify(working.fps);
            });
            SyncPicker();
        }

        void EditOutputPresets(MovieSettings current, Action returnToOutput)
        {
            ShowDialog("出力プリセット",out var body);
            modal.userData=(Action)(()=>{CloseDialog();returnToOutput();});
            var rows=Box(body,"output-presets","output-presets");
            var entries=new List<(TextField name,IntegerField width,IntegerField height,IntegerField fps)>();
            void Add(OutputPreset value) {
                var item=Box(rows,"preset");
                var row=Box(item,"preset-name","row");
                var name=new TextField{value=value.name,tooltip="名前"};name.style.flexGrow=1;
                var dimensions=Box(item,"preset-dimensions","row");
                IntegerField Dimension(string label,int number) {
                    var field=new IntegerField(label){value=number};field.style.flexGrow=1;field.style.width=100;
                    field.labelElement.style.minWidth=24;field.labelElement.style.width=24;
                    dimensions.Add(field);return field;
                }
                var width=Dimension("幅",value.width);var height=Dimension("高さ",value.height);var fps=Dimension("fps",value.fps);
                var entry=(name,width,height,fps);entries.Add(entry);
                row.Add(name);
                var remove=Action(row,"削除",true,()=>{entries.Remove(entry);item.RemoveFromHierarchy();});remove.style.flexGrow=0;
            }
            foreach(var preset in ReadOutputPresets())Add(preset);
            var tools=Box(body,"preset-tools","row");
            Action(tools,"追加",true,()=>Add(new OutputPreset{name="新規",width=current.width,height=current.height,fps=current.fps}));
            Action(tools,"初期値",true,()=>{entries.Clear();rows.Clear();foreach(var preset in OutputPreset.Defaults())Add(preset);});
            var error=new Label{name="output-error"};error.style.whiteSpace=WhiteSpace.Normal;body.Add(error);
            var buttons=Box(body,"preset-actions","row");
            Action(buttons,"OK",true,()=>{
                var items=entries.Select(e=>new OutputPreset{name=e.name.value.Trim(),width=e.width.value,height=e.height.value,fps=e.fps.value}).ToArray();
                int bad=Array.FindIndex(items,p=>!p.Valid);
                if(bad>=0){error.text=(bad+1)+"行目：名前、幅・高さ（1以上）、fps（1～120）を確認してください。";return;}
                WorkspacePreferences.SetString(OutputPresetPreference,JsonUtility.ToJson(new SavedOutputPresets{items=items}));
                CloseDialog();returnToOutput();
            });
            Action(buttons,"キャンセル",true,()=>{CloseDialog();returnToOutput();});
        }
    }
}
