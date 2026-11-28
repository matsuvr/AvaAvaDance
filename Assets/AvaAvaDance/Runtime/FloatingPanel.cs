using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        abstract class FloatingPanel : System.IDisposable
        {
            protected readonly DanceApp app;
            protected UIDocument document;
            protected VisualElement root;
            GameObject view;
            PanelSettings settings;
            RenderTexture output;
            bool dispatching,dead;
            long shownDocument;
            internal VisualElement Root=>root;
            internal bool IsOpen => !dead && root != null;
            internal string DesktopTitle { get; private set; }
            internal void OpenEditorWindow(string title, int width, int height)
            {
                DesktopTitle = title;
                Resize(width, height);
                root?.Focus();
            }
            internal void ResizeDesktop(int width, int height) => Resize(width, height);
            internal void CloseDesktop()
            {
                Dispose();
                if (Current) UserClosed();
            }
            internal void DispatchMcp(System.Action send)
            {
                bool previous = dispatching;
                dispatching = true;
                try { send(); }
                finally { dispatching = previous; }
            }
            internal RenderTexture Surface=>output;
            protected bool CanEdit=>Current && app.modal==null && app.importDialog==null && !app.importing && !app.exporting;
            protected abstract bool Current {get;}
            protected abstract void Populate(VisualElement content);
            protected abstract void Refresh();
            protected abstract void UserClosed();
            protected FloatingPanel(DanceApp app){this.app=app;shownDocument=app.documentGeneration;}
            void Initialize(){
                settings=UnityEngine.Object.Instantiate(app.panel);
                settings.name="Floating panel";settings.scaleMode=PanelScaleMode.ConstantPixelSize;
                settings.clearColor=true;settings.colorClearValue=new Color32(244,243,250,255);
                settings.SetScreenToPanelSpaceFunction(_=>new Vector2(-100000,-100000));
                view=new GameObject("編集パネル");view.SetActive(false);
                document=view.AddComponent<UIDocument>();document.panelSettings=settings;
                view.SetActive(true);root=document.rootVisualElement;root.focusable=true;
                root.styleSheets.Add(Resources.Load<StyleSheet>("Classic"));
                root.AddToClassList("floating-editor");
                var scroll=new ScrollView(ScrollViewMode.Vertical);
                scroll.style.flexGrow=1;
                root.Add(scroll);
                var content=scroll.contentContainer;
                scroll.RegisterCallback<GeometryChangedEvent>(e=>content.style.minHeight=e.newRect.height);
                Populate(content);
                void LocalOnly(EventBase e){if(!dispatching){e.StopImmediatePropagation();e.PreventDefault();}}
                root.RegisterCallback<PointerDownEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<PointerMoveEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<PointerUpEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<WheelEvent>(LocalOnly,TrickleDown.TrickleDown);
                root.RegisterCallback<KeyDownEvent>(e=>{LocalOnly(e);if(dispatching && CanEdit)app.MainMenuKey(e);},TrickleDown.TrickleDown);
            }
            void Resize(int w,int h){
                if(w<1 || h<1 || w>SystemInfo.maxTextureSize || h>SystemInfo.maxTextureSize)return;
                if(root==null)Initialize();
                settings.scale=1;
                if(output!=null && output.width==w && output.height==h && output.IsCreated())return;
                var next=new RenderTexture(w,h,24,RenderTextureFormat.ARGB32){name="浮動パネル",antiAliasing=1};
                if(!next.Create()){UnityEngine.Object.Destroy(next);return;}
                if(output!=null)UnityEngine.Object.Destroy(output);
                output=next;settings.targetTexture=output;
            }
            public void Synchronize(){
                if(dead || !Current)return;
                if(shownDocument!=app.documentGeneration){
                    shownDocument=app.documentGeneration;
                    ReleaseInput();
                }
                if(output!=null && !output.IsCreated())Resize(output.width,output.height);
                if(root!=null)Refresh();
            }
            void ReleaseInput(){
                if(root?.panel?.GetCapturingElement(PointerId.mousePointerId) is VisualElement captured)
                    captured.ReleasePointer(PointerId.mousePointerId);
                (root?.focusController?.focusedElement as VisualElement)?.Blur();
            }
            public virtual void Dispose(){
                if(dead)return;
                dead=true;
                if(app.contextLayer!=null && root?.Contains(app.contextLayer)==true)app.CloseTimelineMenu();
                ReleaseInput();
                if (settings!=null)settings.targetTexture=null;
                if(view!=null)UnityEngine.Object.Destroy(view);
                if(settings!=null)UnityEngine.Object.Destroy(settings);
                if(output!=null)UnityEngine.Object.Destroy(output);
            }
        }
    }
}
