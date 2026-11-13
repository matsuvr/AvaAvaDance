using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const string ActivateClassic="workspace.activateMmd",ActivateRibbon="workspace.activateMmm";
        readonly Dictionary<string,string>[] keyOverrides={new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase),new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)};
        [StructLayout(LayoutKind.Sequential)]
        struct SymbolKey
        {
            public int vk,scan,glyph,layout,mods,up;
            public bool Present => vk!=0;
            public KeyCode Label => glyph==0xa5?KeyCode.Backslash:glyph>=32 && glyph<=126?(KeyCode)glyph:KeyCode.None;
            public string Id => "OEM-"+vk.ToString("X2");
            public KeyCode Transform => vk switch {
                0xbe=>KeyCode.Period,0xbf=>KeyCode.Slash,0xe2=>KeyCode.Backslash,
                0xbb=>KeyCode.Semicolon,0xba=>KeyCode.Colon,0xdd=>KeyCode.RightBracket,_=>KeyCode.None};
            public static bool IsOem(int vk)=>(vk>=0xba && vk<=0xc0) || (vk>=0xdb && vk<=0xdf) || vk==0xe2;
        }
        [DllImport("user32")]static extern uint MapVirtualKeyExW(uint key,uint kind,IntPtr layout);
        [DllImport("user32")]static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("AvaClassicUi")]static extern int ava_editor_keyboard(IntPtr window);
        [DllImport("AvaClassicUi")]static extern int ava_editor_key(out SymbolKey key);
        bool physicalMainKeys;
        SymbolKey symbol;
        readonly HashSet<int> pressedSymbols=new HashSet<int>();
        KeyCode heldLabel;
        int heldVirtual;
        TextField captureShortcut;
        static bool Punctuation(KeyCode key) => ((int)key>=33 && (int)key<=47) || ((int)key>=58 && (int)key<=64) ||
            ((int)key>=91 && (int)key<=96) || ((int)key>=123 && (int)key<=126);
        static EventModifiers KeyMods(int mods) => ((mods&1)!=0?EventModifiers.Shift:0) |
            ((mods&2)!=0?EventModifiers.Control:0) | ((mods&4)!=0?EventModifiers.Alt:0);
        void PumpSymbolKeys()
        {
            if(!physicalMainKeys)return;
            while(ava_editor_key(out var input)!=0){
                if(input.up!=0){pressedSymbols.Remove(input.vk);if(heldVirtual==input.vk)FinishHeldTransform();}
                if(!Application.isFocused)continue;
                var root=MenuRoot;
                var target=root.focusController?.focusedElement as VisualElement??root;
                bool capture=captureShortcut?.panel!=null && (target==captureShortcut || captureShortcut.Contains(target));
                if(!capture && (EditingField(target,input.Label) || modal!=null || importDialog!=null))continue;
                symbol=input;
                try{
                    var systemEvent=new Event{keyCode=input.Label,modifiers=KeyMods(input.mods)};
                    if(input.up!=0){
                        systemEvent.type=EventType.KeyUp;
                        using(var e=KeyUpEvent.GetPooled(systemEvent)){e.target=target;target.SendEvent(e);}
                    }else{
                        systemEvent.type=EventType.KeyDown;
                        using(var e=KeyDownEvent.GetPooled(systemEvent)){e.target=target;target.SendEvent(e);}
                    }
                }finally{symbol=default;}
            }
        }
        string Chord(KeyDownEvent e,bool physical=true)=>(e.ctrlKey?"Ctrl+":"")+(e.altKey?"Alt+":"")+(e.shiftKey?"Shift+":"")+
            (physical && symbol.Present?symbol.Id:e.keyCode.ToString());
        bool DuplicateMainSymbol(VisualElement target,KeyCode key)=>physicalMainKeys && !symbol.Present && Punctuation(key) &&
            target?.panel==MenuRoot.panel && !EditingField(target,key);
        void LoadKeyOverrides()
        {
            for(int i=0;i<2;i++)
            {
                keyOverrides[i].Clear();
                foreach(string entry in WorkspacePreferences.GetString("Workspace.Keys."+i).Split('\n'))
                {
                    int split=entry.IndexOf('=');if(split>0)keyOverrides[i][entry.Substring(0,split)]=entry.Substring(split+1);
                }
            }
        }
        static readonly HashSet<string> coreChords=new HashSet<string>((
            "F10 LeftAlt RightAlt Alt+F Alt+D Alt+V Alt+B Alt+M Alt+P Alt+H Alt+E Alt+Return Alt+KeypadEnter " +
            "Ctrl+N Ctrl+O Ctrl+S Ctrl+Z Ctrl+X Ctrl+Y Ctrl+LeftArrow Ctrl+RightArrow Ctrl+C Ctrl+V Ctrl+W Ctrl+A Ctrl+Period Ctrl+Comma Ctrl+M Ctrl+Shift+S Ctrl+Shift+Z " +
            "Shift+V Shift+Tab Shift+Backslash Shift+Underscore Shift+A Shift+LeftArrow Shift+RightArrow Shift+F1 Shift+F2 Shift+F3 Shift+Colon " +
            "RightBracket Tab Backslash Underscore UpArrow DownArrow G Escape LeftArrow RightArrow Delete P Return KeypadEnter I K U J H F R D V B A S L Z X C E W " +
            "Keypad2 Keypad8 Keypad4 Keypad6 Keypad5 Keypad0 Keypad1 F1 F2 F3 Home End Period Slash Semicolon Colon").Split(' '),StringComparer.OrdinalIgnoreCase);

        sealed class RibbonKey
        {
            internal readonly string id,caption;
            internal readonly KeyCode key;
            internal readonly EventModifiers mods;
            internal readonly bool repeat;
            internal readonly int vk;
            internal RibbonKey(string id,string caption,KeyCode key,EventModifiers mods=EventModifiers.None,bool repeat=false,int vk=0)
            {this.id=id;this.caption=caption;this.key=key;this.mods=mods;this.repeat=repeat;this.vk=vk;}
            internal string Chord=>((mods&EventModifiers.Control)!=0?"Ctrl+":"")+((mods&EventModifiers.Shift)!=0?"Shift+":"")+
                (vk!=0?"OEM-"+vk.ToString("X2"):key.ToString());
        }
        static readonly RibbonKey[] ribbonKeys={
            new RibbonKey("selection.bonesAndKeys.selectAll","ボーンとキーを全選択",KeyCode.A,EventModifiers.Control),
            new RibbonKey("selection.bones.selectAll","ボーンを全選択",KeyCode.A,EventModifiers.Shift),
            new RibbonKey("timeline.bookmark.next","次のブックマーク",KeyCode.Period,EventModifiers.Control,repeat:true),
            new RibbonKey("timeline.bookmark.previous","前のブックマーク",KeyCode.Comma,EventModifiers.Control,repeat:true),
            new RibbonKey("timeline.bookmark.toggleAtCurrent","ブックマークを追加・削除",KeyCode.M,EventModifiers.Control),
            new RibbonKey("keyframes.copy","キーをコピー",KeyCode.C,EventModifiers.Control),
            new RibbonKey("keyframes.cut","キーを切り取り",KeyCode.X,EventModifiers.Control),
            new RibbonKey("keyframes.paste","キーを貼り付け",KeyCode.V,EventModifiers.Control),
            new RibbonKey("keyframes.pasteMirrored","キーを反転貼り付け",KeyCode.W,EventModifiers.Control),
            new RibbonKey("keyframes.delete","選択キーを削除",KeyCode.Delete),
            new RibbonKey("view.boneMarkers.toggle","ボーンマーカーの表示",KeyCode.C),
            new RibbonKey("rig.centerBias.open","センター位置バイアス",KeyCode.D),
            new RibbonKey("keyframes.initialize","ポーズを初期化",KeyCode.E),
            new RibbonKey("transform.space.toggle","軸をGlobal／Localに切り替え",KeyCode.G),
            new RibbonKey("timeline.column.insert","フレーム列を挿入",KeyCode.I),
            new RibbonKey("timeline.column.delete","フレーム列を削除",KeyCode.K),
            new RibbonKey("transport.playStop.toggle","再生・停止",KeyCode.P),
            new RibbonKey("selection.unregisteredBones.select","未登録ボーンを選択",KeyCode.S),
            new RibbonKey("view.modelTranslucency.toggle","モデルの半透明表示",KeyCode.V),
            new RibbonKey("pose.mirror","ポーズを左右反転",KeyCode.W),
            new RibbonKey("view.poseHandles.toggle","ポーズハンドルの表示",KeyCode.X),
            new RibbonKey("camera.key.enabled.toggle","カメラキーの有効化",KeyCode.Z),
            new RibbonKey("keyframes.register","キーを登録",KeyCode.Return),
            new RibbonKey("editingTarget.cycleNext","次の編集対象",KeyCode.Tab),
            new RibbonKey("editingTarget.cyclePrevious","前の編集対象",KeyCode.Tab,EventModifiers.Shift),
            new RibbonKey("timeline.seekStart","先頭へ移動",KeyCode.Home),
            new RibbonKey("selection.globalProperties.select","全体プロパティを選択",KeyCode.End),
            new RibbonKey("timeline.row.previous","前の行",KeyCode.UpArrow,repeat:true),
            new RibbonKey("timeline.row.next","次の行",KeyCode.DownArrow,repeat:true),
            new RibbonKey("timeline.frame.previous","前のフレーム",KeyCode.LeftArrow,repeat:true),
            new RibbonKey("timeline.frame.next","次のフレーム",KeyCode.RightArrow,repeat:true),
            new RibbonKey("timeline.key.previous","前のキー",KeyCode.LeftArrow,EventModifiers.Control,repeat:true),
            new RibbonKey("timeline.key.next","次のキー",KeyCode.RightArrow,EventModifiers.Control,repeat:true),
            new RibbonKey("timeline.frame.previousWithPose","ポーズを保って前のフレーム",KeyCode.LeftArrow,EventModifiers.Shift,repeat:true),
            new RibbonKey("timeline.frame.nextWithPose","ポーズを保って次のフレーム",KeyCode.RightArrow,EventModifiers.Shift,repeat:true),
            new RibbonKey("transform.rotate.x","X軸回転",KeyCode.Period,vk:0xbe),
            new RibbonKey("transform.rotate.y","Y軸回転",KeyCode.Slash,vk:0xbf),
            new RibbonKey("transform.rotate.z","Z軸回転",KeyCode.Backslash,vk:0xe2),
            new RibbonKey("transform.translate.x","X軸移動",KeyCode.Semicolon,vk:0xbb),
            new RibbonKey("transform.translate.y","Y軸移動",KeyCode.Colon,vk:0xba),
            new RibbonKey("transform.translate.z","Z軸移動",KeyCode.RightBracket,vk:0xdd),
            new RibbonKey("project.save","作品を保存",KeyCode.S,EventModifiers.Control),
            new RibbonKey("history.undo","元に戻す",KeyCode.Z,EventModifiers.Control),
            new RibbonKey("history.redo","やり直し",KeyCode.Y,EventModifiers.Control),
            new RibbonKey("viewport.view.right","右視点",KeyCode.F1),
            new RibbonKey("viewport.view.left","左視点",KeyCode.F1,EventModifiers.Shift),
            new RibbonKey("viewport.view.top","上視点",KeyCode.F2),
            new RibbonKey("viewport.view.bottom","下視点",KeyCode.F2,EventModifiers.Shift),
            new RibbonKey("viewport.view.front","正面視点",KeyCode.F3),
            new RibbonKey("viewport.view.back","背面視点",KeyCode.F3,EventModifiers.Shift)
        };
        static readonly Dictionary<string,RibbonKey> ribbonById=ribbonKeys.ToDictionary(k=>k.id);
        string Replacement(string id,string area)
        {
            if(id==null)return null;
            string local=area+"|";
            foreach(var binding in keyOverrides[1])if(binding.Value==id && binding.Key.StartsWith(local,StringComparison.Ordinal))return binding.Key.Substring(local.Length);
            return keyOverrides[1].FirstOrDefault(p=>p.Value==id && !p.Key.Contains("|")).Key;
        }
        bool RouteNamedKey(KeyDownEvent e,bool extension=false,bool repeated=false)
        {
            if(EditingField(e.target as VisualElement,e.keyCode) || e.commandKey)return false;
            var chord=Chord(e);
            string named=null;
            if(extension){
                if(coreChords.Contains(Chord(e,false)) || ribbonProfile && symbol.Present && symbol.Transform!=KeyCode.None && !e.shiftKey && !e.ctrlKey && !e.altKey)return false;
                foreach(var plugin in extensions)if(plugin.shortcuts.TryGetValue(chord,out named) || plugin.shortcuts.TryGetValue(Chord(e,false),out named))break;
            }else{
                var map=keyOverrides[ribbonProfile?1:0];string scope=CurrentKeyArea(e.target as VisualElement);
                if(!map.TryGetValue(scope+"|"+chord,out named) && !map.TryGetValue(scope+"|"+Chord(e,false),out named) &&
                    !map.TryGetValue(chord,out named))map.TryGetValue(Chord(e,false),out named);
            }
            if(named==null)return false;
            if(ribbonProfile && ribbonById.TryGetValue(named,out var preset)){
                if(!repeated || preset.repeat){
                    bool started=RibbonShortcut(preset.key,(preset.mods&EventModifiers.Control)!=0,(preset.mods&EventModifiers.Shift)!=0,false);
                    if(started && transformKey!=KeyCode.None && preset.vk!=0){heldLabel=e.keyCode;heldVirtual=symbol.vk;}
                }
                return true;
            }
            if(repeated)return true;
            if(named==ActivateClassic || named==ActivateRibbon)RequestProfile(named==ActivateRibbon);
            else if(FindCommand(named,out var command))Execute(command);
            return true;
        }
        static readonly string[] keyAreas={"全体","タイムライン","ビューポート","操作パネル","音声"};
        string mainKeyArea="全体";
        static string KeyArea(VisualElement target)
        {
            for(var p=target;p!=null;p=p.parent){
                if(p.name=="floating-wave" || p.name=="waveform")return "音声";
                if(p.name=="timeline" || p.name=="floating-timeline" || p.name=="sidebar")return "タイムライン";
                if(p.name=="viewport")return "ビューポート";
                if(p.name=="bottom" || p.ClassListContains("floating-editor"))return "操作パネル";
            }
            return null;
        }
        string KeyCaption(string chord)
        {
            int at=chord.IndexOf("OEM-",StringComparison.Ordinal);
            if(at<0 || !int.TryParse(chord.Substring(at+4),System.Globalization.NumberStyles.HexNumber,null,out var vk))return chord;
            IntPtr layout=GetKeyboardLayout(0);
            uint scan=MapVirtualKeyExW((uint)vk,0,layout);
            char glyph=(char)(MapVirtualKeyExW((uint)vk,2,layout)&65535);
            return chord.Substring(0,at)+glyph+"　("+((layout.ToInt64()&65535)==0x411?"JIS":(layout.ToInt64()&65535)==0x409?"US":"入力配列")+" / "+scan.ToString("X2")+")";
        }
        string CurrentKeyArea(VisualElement target)=>KeyArea(target)??mainKeyArea;
        void EditShortcuts()
        {
            int index=ribbonProfile?1:0;
            ShowDialog("ショートカット設定",out var body);
            var names=new List<string>{ActivateClassic,ActivateRibbon};
            names.AddRange(commands.Keys.Where(c=>(int)c<10000).Select(c=>"core."+c));names.AddRange(extensionCommands.Keys);
            var labels=new Dictionary<string,string>{{ActivateClassic,classicTab.text},{ActivateRibbon,movingTab.text}};
            void NameItems(MmdMenu.Entry group,string parent){
                foreach(var entry in group.Children??Array.Empty<MmdMenu.Entry>()){
                    if(entry.Japanese==null)continue;
                    var label=parent+" → "+entry.Label(englishMenus);
                    if(entry.Children?.Length>0)NameItems(entry,label);else{
                        var id=extensionCommands.FirstOrDefault(p=>p.Value==entry.Id).Key??"core."+entry.Id;
                        labels[id]=label;
                    }
                }
            }
            foreach(var menu in combinedMenus)NameItems(menu,menu.Label(englishMenus));
            if(ribbonProfile)foreach(var binding in ribbonKeys){names.Add(binding.id);labels[binding.id]=binding.caption;}
            string Caption(string id)=>labels.TryGetValue(id,out var label)?label:id;
            var scope=new DropdownField("操作範囲",keyAreas.ToList(),0);body.Add(scope);
            var command=new DropdownField("操作",names,0,Caption,Caption);body.Add(command);
            var keys=new TextField("キー"){isReadOnly=true};body.Add(keys);captureShortcut=keys;
            string selectedChord="";
            var detail=new Label();body.Add(detail);
            var replace=new Toggle("この範囲の割当を置き換える");replace.style.display=DisplayStyle.None;body.Add(replace);
            var error=new Label();body.Add(error);
            string Prefix()=>scope.index==0?"":scope.value+"|";
            bool InScope(string key)=>scope.index==0?!key.Contains("|"):key.StartsWith(Prefix(),StringComparison.Ordinal);
            void Sync(){var saved=keyOverrides[index].FirstOrDefault(p=>p.Value==command.value && InScope(p.Key)).Key;
                selectedChord=saved==null?"":saved.Substring(Prefix().Length);keys.SetValueWithoutNotify(KeyCaption(selectedChord));error.text="";replace.value=false;replace.style.display=DisplayStyle.None;
                detail.text=ribbonById.TryGetValue(command.value,out var binding)?"既定のキー　"+KeyCaption(binding.Chord):"";}
            scope.RegisterValueChangedCallback(_=>Sync());
            command.RegisterValueChangedCallback(_=>Sync());Sync();
            keys.RegisterCallback<KeyDownEvent>(e=>{
                if(physicalMainKeys && !symbol.Present && Punctuation(e.keyCode)){e.StopImmediatePropagation();e.PreventDefault();return;}
                if(e.keyCode==KeyCode.None && !symbol.Present)return;
                if(e.keyCode!=KeyCode.LeftControl && e.keyCode!=KeyCode.RightControl && e.keyCode!=KeyCode.LeftShift && e.keyCode!=KeyCode.RightShift &&
                    e.keyCode!=KeyCode.LeftAlt && e.keyCode!=KeyCode.RightAlt){selectedChord=Chord(e);keys.value=KeyCaption(selectedChord);replace.value=false;replace.style.display=DisplayStyle.None;error.text="";}
                e.StopImmediatePropagation();e.PreventDefault();
            },TrickleDown.TrickleDown);
            Action(body,"割当を解除",true,()=>{selectedChord="";keys.value="";error.text="";});
            Action(body,"適用",true,()=>{
                string chord=selectedChord;
                if(chord.Length>0){
                    keyOverrides[index].TryGetValue(Prefix()+chord,out var previous);
                    if(previous==null && ribbonProfile){
                        var preset=ribbonKeys.FirstOrDefault(k=>k.Chord==chord);
                        if(preset!=null && Replacement(preset.id,scope.value)==null)previous=preset.id;
                    }
                    if(previous!=null && previous!=command.value && !replace.value){
                        error.text=scope.value+"で「"+Caption(previous)+"」に割当済みです。";replace.style.display=DisplayStyle.Flex;return;
                    }
                }
                foreach(var old in keyOverrides[index].Where(p=>p.Value==command.value && InScope(p.Key)).ToArray())keyOverrides[index].Remove(old.Key);
                if(chord.Length>0)keyOverrides[index][Prefix()+chord]=command.value;
                WorkspacePreferences.SetString("Workspace.Keys."+index,string.Join("\n",keyOverrides[index].Select(p=>p.Key+"="+p.Value)));CloseDialog();
            });
            Action(body,"キャンセル",true,CloseDialog);
        }
    }
}
