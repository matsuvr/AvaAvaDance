using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        abstract class FloatingPanel : ClassicWindow
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
            delegate void InputHandler(int kind,int x,int y,int a,int b,int modifiers,[MarshalAs(UnmanagedType.LPWStr)] string text);
            [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]static extern void ava_panel_input(IntPtr child,InputHandler handler);
            [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]static extern void ava_panel_caret(IntPtr child,int x,int y,int height);
            [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]static extern void ava_panel_editing(IntPtr child,int editing);

            enum Kind { Resize=1,Down,Up,Move,Wheel,KeyDown,KeyUp,Text,Focus,Blur,Composition,Commit,LostCapture,Leave }
            readonly ConcurrentQueue<(Kind kind,int x,int y,int a,int b,int mods,string text,long document)> input=new ConcurrentQueue<(Kind,int,int,int,int,int,string,long)>();
            readonly InputHandler inputHandler;
            protected readonly DanceApp app;
            readonly HashSet<KeyCode> held=new HashSet<KeyCode>();
            readonly Dictionary<int,KeyCode> heldSymbols=new Dictionary<int,KeyCode>();
            protected UIDocument document;
            protected VisualElement root;
            GameObject view;
            PanelSettings settings;
            RenderTexture output;
            WindowSurface windowSurface;
            IntPtr child;
            int deviceGeneration;
            bool dispatching,dead,mouseDown;
            Vector2 lastPoint;
            long shownDocument;
            bool composing,imeEnabled;
            internal bool HasActiveInput=>mouseDown || composing;
            internal void WithInputWindow(Action<IntPtr> action)=>OnWindow(()=>action(child));
            internal int PresentedFrames=>windowSurface?.Frames??0;
            internal VisualElement Root=>root;
            internal void DispatchMcp(Action send)
            {
                bool previous = dispatching;
                dispatching = true;
                try { send(); }
                finally { dispatching = previous; }
            }
            internal RenderTexture Surface=>output;
            protected bool CanEdit=>Current && app.modal==null && app.importDialog==null && !app.importing && !app.exporting;
            protected abstract bool Current {get;}
            protected abstract void Populate();
            protected abstract void Refresh();
            protected abstract void UserClosed();
            protected FloatingPanel(DanceApp app):base(app){
                this.app=app;shownDocument=app.documentGeneration;
                inputHandler=(kind,x,y,a,b,mods,text)=>input.Enqueue(((Kind)kind,x,y,a,b,mods,text,System.Threading.Interlocked.Read(ref shownDocument)));
            }
            protected sealed override void Build(){
                Add(30,"AvaEditorPanel","",0x10000);
                System.Threading.Interlocked.Exchange(ref child,Control(30));ava_panel_input(child,inputHandler);
            }
            protected sealed override void Layout(int w,int h)=>Place(30,0,0,w,h);
            protected sealed override void Command(int id,int notification){}
            protected sealed override void Closing(){app.pluginQueue.Enqueue(()=>{
                ReleaseView();if(Current)UserClosed();
            });}
            void Initialize(){
                settings=UnityEngine.Object.Instantiate(app.panel);
                settings.name="Floating panel";settings.scaleMode=PanelScaleMode.ConstantPixelSize;
                settings.clearColor=true;settings.colorClearValue=new Color(.93f,.94f,.96f,1);
                settings.SetScreenToPanelSpaceFunction(_=>new Vector2(-100000,-100000));
                view=new GameObject("編集パネル");view.SetActive(false);
                document=view.AddComponent<UIDocument>();document.panelSettings=settings;
                view.SetActive(true);root=document.rootVisualElement;root.focusable=true;
                root.styleSheets.Add(Resources.Load<StyleSheet>("Classic"));
                root.AddToClassList("floating-editor");Populate();
                void LocalOnly(EventBase e){if(!dispatching){e.StopImmediatePropagation();e.PreventDefault();}}
                root.RegisterCallback<PointerDownEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<PointerMoveEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<PointerUpEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<WheelEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<KeyDownEvent>(e=>{LocalOnly(e);if(dispatching && CanEdit && !composing)app.MainMenuKey(e);},TrickleDown.TrickleDown);
                windowSurface=new WindowSurface(app,()=>child,()=>output);
            }
            void Resize(int w,int h,int dpi){
                if(w<1 || h<1 || w>SystemInfo.maxTextureSize || h>SystemInfo.maxTextureSize)return;
                if(root==null)Initialize();
                settings.scale=Mathf.Max(1,dpi)/96f;
                int generation=WindowSurface.DeviceGeneration;
                if(output!=null && output.width==w && output.height==h && output.IsCreated() && deviceGeneration==generation)return;
                var next=new RenderTexture(w,h,24,RenderTextureFormat.ARGB32){name="浮動パネル",antiAliasing=1};
                if(!next.Create()){UnityEngine.Object.Destroy(next);return;}
                if(output!=null)UnityEngine.Object.Destroy(output);
                output=next;settings.targetTexture=output;deviceGeneration=generation;
            }
            public void Synchronize(){
                if(dead || !Current)return;
                var previous=System.Threading.Interlocked.Exchange(ref shownDocument,app.documentGeneration);
                if(previous!=app.documentGeneration)EndInput();
                while(input.TryDequeue(out var m)){
                    if(m.kind==Kind.Resize){Resize(m.x,m.y,m.a);continue;}
                    if(root?.panel==null || m.document!=app.documentGeneration)continue;
                    dispatching=true;
                    try{Dispatch(m.kind,m.x,m.y,m.a,m.b,m.mods,m.text);}
                    finally{app.symbol=default;dispatching=false;}
                }
                if(output!=null && (!output.IsCreated() || deviceGeneration!=WindowSurface.DeviceGeneration))Resize(output.width,output.height,Mathf.RoundToInt(settings.scale*96));
                if(root!=null){Refresh();SetCaret();}
            }
            static EventModifiers Mods(int flags)=>(flags%2!=0?EventModifiers.Shift:0)|((flags&2)!=0?EventModifiers.Control:0)|((flags&4)!=0?EventModifiers.Alt:0);
            static void Send(VisualElement target,EventBase e){using(e){e.target=target;target.SendEvent(e);}}
            VisualElement Focused=>root?.focusController?.focusedElement as VisualElement;
            TextField Field=>Focused as TextField??Focused?.GetFirstAncestorOfType<TextField>();
            VisualElement KeyboardTarget=>Field?.textSelection as VisualElement??Focused??root;
            void EndInput(){
                foreach(var key in held){app.pressedKeys.Remove(key);app.suppressedKeys.Remove(key);if(app.transformKey!=KeyCode.None && app.heldLabel==key)app.FinishHeldTransform();}
                foreach(var vk in heldSymbols.Keys)app.pressedSymbols.Remove(vk);
                heldSymbols.Clear();held.Clear();CancelPointer();composing=false;Focused?.Blur();
            }
            void CancelPointer(){
                if(root?.panel?.GetCapturingElement(PointerId.mousePointerId) is VisualElement captured)captured.ReleasePointer(PointerId.mousePointerId);
                mouseDown=false;
            }
            void Dispatch(Kind kind,int x,int y,int a,int b,int flags,string text){
                var mods=Mods(flags);
                if(kind==Kind.Focus){if(Focused==null)root.Focus();return;}
                if(kind==Kind.Blur){EndInput();return;}
                if(kind==Kind.LostCapture){if(mouseDown)CancelPointer();return;}
                if(kind==Kind.Composition){composing=a!=0;return;}
                if(kind==Kind.Text || kind==Kind.Commit){
                    if(Field!=null && !string.IsNullOrEmpty(text)){
                        foreach(char letter in text)Send(KeyboardTarget,KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,character=letter,keyCode=KeyCode.None}));
                    }
                    return;
                }
                if(kind==Kind.KeyDown || kind==Kind.KeyUp){
                    if(SymbolKey.IsOem(a))app.symbol=new SymbolKey{vk=a,scan=(b>>16)&511,glyph=x,layout=y,mods=flags};
                    var key=PanelKey(a,b,x);if(key==KeyCode.None && !app.symbol.Present)return;
                    var target=KeyboardTarget;
                    if(kind==Kind.KeyUp){
                        if(heldSymbols.TryGetValue(a,out var original)){key=original;heldSymbols.Remove(a);}
                        app.pressedSymbols.Remove(a);
                        held.Remove(key);app.pressedKeys.Remove(key);app.suppressedKeys.Remove(key);
                        if(app.transformKey!=KeyCode.None && (app.heldVirtual!=0?app.heldVirtual==app.symbol.vk:app.heldLabel==key))app.FinishHeldTransform();
                        Send(target,KeyUpEvent.GetPooled(new Event{type=EventType.KeyUp,keyCode=key,modifiers=mods}));return;
                    }
                    if(app.symbol.Present){
                        if(!heldSymbols.TryGetValue(a,out var original))heldSymbols.Add(a,key);
                        else key=original;
                    }
                    held.Add(key);
                    if(composing)return;
                    if(Field!=null && EditNavigation(key,mods))return;
                    if(Field!=null && mods==EventModifiers.Control){
                        var field=Field;var sel=field.textSelection;int lo=Math.Min(sel.cursorIndex,sel.selectIndex),hi=Math.Max(sel.cursorIndex,sel.selectIndex);
                        switch(key){
                            case KeyCode.A:sel.SelectRange(0,field.value.Length);return;
                            case KeyCode.C:case KeyCode.X:
                                if(hi>lo){GUIUtility.systemCopyBuffer=field.value.Substring(lo,hi-lo);if(key==KeyCode.X)ReplaceSelection("");}return;
                            case KeyCode.V:ReplaceSelection(GUIUtility.systemCopyBuffer);return;
                        }
                    }
                    if(key==KeyCode.Tab && Field!=null){
                        var fields=root.Query<TextField>().ToList().Where(v=>v.enabledInHierarchy).ToList();int i=fields.IndexOf(Field);
                        if(i>=0)fields[(i+((flags&1)==0?1:fields.Count-1))%fields.Count].Focus();return;
                    }
                    Send(target,KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,keyCode=key,modifiers=mods}));
                    if(app.transformKey!=KeyCode.None && app.heldLabel==key)app.holdPoint=lastPoint;return;
                }
                var point=new Vector2(x,y)/settings.scale;
                if(kind==Kind.Move && app.transformKey!=KeyCode.None && held.Contains(app.heldLabel)){
                    app.ApplyHeldDelta(app.transformKey,point-app.holdPoint);app.holdPoint=point;lastPoint=point;return;
                }
                var hit=(root.panel.GetCapturingElement(PointerId.mousePointerId) as VisualElement)??root.panel.Pick(point)??root;
                var e=new Event{mousePosition=point,modifiers=mods,button=a,clickCount=b};
                switch(kind){
                    case Kind.Down:mouseDown=true;e.type=EventType.MouseDown;Send(hit,PointerDownEvent.GetPooled(e));break;
                    case Kind.Up:mouseDown=false;e.type=EventType.MouseUp;Send(hit,PointerUpEvent.GetPooled(e));break;
                    case Kind.Move:e.type=mouseDown?EventType.MouseDrag:EventType.MouseMove;e.delta=point-lastPoint;Send(hit,PointerMoveEvent.GetPooled(e));break;
                    case Kind.Wheel:e.type=EventType.ScrollWheel;e.delta=b==0?new Vector2(0,-a/120f):new Vector2(a/120f,0);Send(hit,WheelEvent.GetPooled(e));break;
                    case Kind.Leave:e.type=EventType.MouseLeaveWindow;Send(root,MouseLeaveWindowEvent.GetPooled(e));break;
                }
                lastPoint=point;
            }
            bool EditNavigation(KeyCode key,EventModifiers mods){
                if(key!=KeyCode.LeftArrow && key!=KeyCode.RightArrow && key!=KeyCode.Home && key!=KeyCode.End &&
                    key!=KeyCode.Backspace && key!=KeyCode.Delete)return false;
                var f=Field;var text=f.value;var selection=f.textSelection;
                int caret=Mathf.Clamp(selection.cursorIndex,0,text.Length),anchor=Mathf.Clamp(selection.selectIndex,0,text.Length);
                int lower=Math.Min(caret,anchor),upper=Math.Max(caret,anchor);
                bool back=key==KeyCode.LeftArrow || key==KeyCode.Backspace,extend=(mods&EventModifiers.Shift)!=0;
                var positions=System.Globalization.StringInfo.ParseCombiningCharacters(text);
                int adjacent=back?positions.LastOrDefault(p=>p<caret):positions.Where(p=>p>caret).DefaultIfEmpty(text.Length).First();
                if((mods&EventModifiers.Control)!=0){
                    if(back){while(adjacent>0 && char.IsWhiteSpace(text[adjacent]))adjacent--;
                        while(adjacent>0 && !char.IsWhiteSpace(text[adjacent-1]))adjacent--;
                    }else{while(adjacent<text.Length && !char.IsWhiteSpace(text[adjacent]))adjacent++;
                        while(adjacent<text.Length && char.IsWhiteSpace(text[adjacent]))adjacent++;}
                }
                if(key==KeyCode.Backspace || key==KeyCode.Delete){
                    if(f.isReadOnly)return true;
                    if(lower==upper){lower=Math.Min(caret,adjacent);upper=Math.Max(caret,adjacent);}
                    if(upper>lower)f.value=text.Remove(lower,upper-lower);
                    selection.SelectRange(lower,lower);return true;
                }
                int next=key==KeyCode.Home?0:key==KeyCode.End?text.Length:!extend && lower!=upper?(back?lower:upper):adjacent;
                selection.SelectRange(next,extend?anchor:next);return true;
            }
            void ReplaceSelection(string value){
                var f=Field;if(f==null || f.isReadOnly)return;
                var selection=f.textSelection;int lo=Mathf.Clamp(Math.Min(selection.cursorIndex,selection.selectIndex),0,f.value.Length);
                int hi=Mathf.Clamp(Math.Max(selection.cursorIndex,selection.selectIndex),lo,f.value.Length);
                f.value=f.value.Remove(lo,hi-lo).Insert(lo,value);selection.SelectRange(lo+value.Length,lo+value.Length);
            }
            Vector2Int lastCaret=new Vector2Int(-1,-1);
            void SetCaret(){
                var f=Field;bool editing=f!=null && !f.isReadOnly;
                if(imeEnabled!=editing){imeEnabled=editing;lastCaret=new Vector2Int(-1,-1);OnWindow(()=>ava_panel_editing(child,editing?1:0));}
                if(!editing)return;
                var text=f.textSelection as VisualElement;
                var p=text==null?f.worldBound.min:text.LocalToWorld(f.textSelection.cursorPosition);
                if(!float.IsFinite(p.x+p.y))return;
                int lineHeight=Mathf.Max(1,Mathf.CeilToInt(f.resolvedStyle.fontSize*settings.scale));
                var caret=Vector2Int.RoundToInt(p*settings.scale)-new Vector2Int(0,lineHeight);if(lastCaret==caret)return;lastCaret=caret;
                OnWindow(()=>ava_panel_caret(child,caret.x,caret.y,lineHeight));
            }
            static KeyCode PanelKey(int vk,int details,int unmodified){
                if(vk>=186 && vk<=226 && unmodified>=33 && unmodified<=126)return (KeyCode)unmodified;
                if(vk>=65 && vk<=90)return (KeyCode)(vk+32);
                if(vk>=48 && vk<=57)return (KeyCode)vk;
                if(vk>=112 && vk<=123)return KeyCode.F1+(vk-112);
                if(vk>=96 && vk<=105)return KeyCode.Keypad0+(vk-96);
                return vk switch {8=>KeyCode.Backspace,9=>KeyCode.Tab,13=>(details&(1<<24))!=0?KeyCode.KeypadEnter:KeyCode.Return,
                    16=>KeyCode.LeftShift,17=>KeyCode.LeftControl,18=>KeyCode.LeftAlt,27=>KeyCode.Escape,32=>KeyCode.Space,33=>KeyCode.PageUp,34=>KeyCode.PageDown,
                    35=>KeyCode.End,36=>KeyCode.Home,37=>KeyCode.LeftArrow,38=>KeyCode.UpArrow,39=>KeyCode.RightArrow,40=>KeyCode.DownArrow,
                    45=>KeyCode.Insert,46=>KeyCode.Delete,186=>KeyCode.Semicolon,187=>KeyCode.Equals,188=>KeyCode.Comma,189=>KeyCode.Minus,190=>KeyCode.Period,
                    191=>KeyCode.Slash,192=>KeyCode.BackQuote,219=>KeyCode.LeftBracket,220=>KeyCode.Backslash,221=>KeyCode.RightBracket,222=>KeyCode.Quote,_=>KeyCode.None};
            }
            void ReleaseView(){
                if(dead)return;dead=true;if(app.contextLayer!=null && root?.Contains(app.contextLayer)==true)app.CloseTimelineMenu();EndInput();windowSurface?.Dispose();
                if(view!=null)UnityEngine.Object.Destroy(view);
                if(settings!=null)UnityEngine.Object.Destroy(settings);
                if(output!=null)UnityEngine.Object.Destroy(output);
            }
            public override void Dispose(){ReleaseView();base.Dispose();}
        }
    }
}
