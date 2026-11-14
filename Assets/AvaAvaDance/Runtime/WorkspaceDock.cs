using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp : IWindowHost, IWindowSettings
    {
        string IWindowSettings.WindowSettingsDirectory=>WorkspacePreferences.WindowDirectory;
        IntPtr IWindowHost.MainWindow=>mainWindowHandle;
        void IWindowHost.Post(Action operation)=>pluginQueue.Enqueue(operation);
        void IWindowHost.Report(string message){status.text=message;}
        IDisposable IWindowHost.KeepActive(){AcquireActivity();return new Registration(()=>pluginQueue.Enqueue(ReleaseActivity));}

        [DllImport("user32")]static extern IntPtr GetForegroundWindow();
        [DllImport("user32")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        bool lastToolFocus;
        readonly uint processId=(uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        OperationWindow operations;

        bool ToolFocused()
        {
            if(activityCount<=0)return false;
            GetWindowThreadProcessId(GetForegroundWindow(),out var pid);return pid==processId;
        }
        void UpdateDock()
        {
            bool focus=ToolFocused();
            if(focus!=lastToolFocus){lastToolFocus=focus;ApplyFrameLimit();}
            if(operations?.IsOpen==true)operations.Synchronize();
            floatingTime?.Synchronize();floatingWave?.Synchronize();
        }
        void ToggleDock()
        {
            WorkspacePreferences.SetInt("Ribbon.OperationWindow",operations?.IsOpen==true?0:1);PresentOperationWindow();
        }
        void PresentOperationWindow()
        {
            if(!ribbonProfile || WorkspacePreferences.GetInt("Ribbon.OperationWindow")==0){var old=operations;operations=null;old?.Dispose();return;}
            if(operations?.IsOpen==true)return;
            operations=new OperationWindow(this);
            operations.Open("操作パネル",500,295,true);
        }

        sealed class OperationWindow : FloatingPanel
        {
            Label title;
            TextField position,memo;
            readonly TextField[] values=new TextField[6];
            Button apply;
            string previousPose,previousMemo,previousId;
            int previousFrame=-1;
            protected override bool Current=>app.ribbonProfile && app.operations==this;
            public OperationWindow(DanceApp app):base(app){}
            protected override void Populate()
            {
                title=new Label();title.AddToClassList("floating-heading");root.Add(title);
                var nav=Box(root,"frame-tools","row");position=new TextField("フレーム"){name="float-frame"};nav.Add(position);
                Button Tool(VisualElement group,string name,System.Action run)=>Action(group,name,true,()=>{if(CanEdit)run();});
                Tool(nav,"移動",()=>{if(int.TryParse(position.value,out int frame))app.SetFrame(frame);});
                Tool(nav,"前のキー",()=>app.JumpKey(false));Tool(nav,"次のキー",()=>app.JumpKey(true));
                string[] axes={"X","Y","Z","Rx","Ry","Rz"};VisualElement row=null;
                for(int i=0;i<6;i++){
                    if(i%3==0)row=Box(root,"pose-"+i,"row");
                    values[i]=new TextField(axes[i]){name="float-axis-"+i};row.Add(values[i]);
                }
                var edit=Box(root,"edit-tools","row");apply=Tool(edit,"適用",Apply);
                Tool(edit,"登録",app.RegisterSelection);Tool(edit,"再生 / 停止",app.TogglePlayback);
                var history=Box(root,"history-tools","row");Tool(history,"元に戻す",()=>app.ApplyHistory(false));
                Tool(history,"やり直し",()=>app.ApplyHistory(true));Tool(history,"対象切替",()=>app.CycleEditingTarget(false));
                var bookmarks=Box(root,"bookmark-tools","row");memo=new TextField("メモ"){name="float-memo"};bookmarks.Add(memo);
                Tool(bookmarks,"ブックマーク登録",()=>app.ChangeBookmark(app.frame,app.frame,memo.value));
            }
            void Apply(){
                if(previousId!=app.selectedTrack?.Id || previousFrame!=app.frame)return;
                var pose=new float[6];
                for(int i=0;i<6;i++)if(!float.TryParse(values[i].value,out pose[i]) || !float.IsFinite(pose[i]))return;
                app.BeginDraftGesture();try{for(int i=0;i<6;i++)app.EditTransform(i,pose[i]);}finally{app.FinishDraftGesture();}
            }
            protected override void Refresh(){
                string id=app.selectedTrack?.Id;
                bool changed=id!=previousId || previousFrame!=app.frame;previousId=id;previousFrame=app.frame;
                if(changed){title.text=app.selectedTrack?.Name??"";position.SetValueWithoutNotify(app.frame.ToString());}
                string text=app.bookmarkMemos.TryGetValue(app.frame,out var note)?note:"";
                if(changed || text!=previousMemo){memo.SetValueWithoutNotify(text);previousMemo=text;}
                string[] pose=app.transformFields.Take(6).Select(f=>f.value.ToString("G6",CultureInfo.CurrentCulture)).ToArray();
                string snapshot=string.Join("|",pose);if(!changed && snapshot==previousPose)return;previousPose=snapshot;
                bool editable=app.selectedTrack is MotionTrack<BonePose> || app.selectedTrack==app.cameraTrack;
                for(int i=0;i<6;i++){values[i].SetValueWithoutNotify(pose[i]);values[i].SetEnabled(editable);}
                apply.SetEnabled(editable);
            }
            protected override void UserClosed(){app.operations=null;WorkspacePreferences.SetInt("Ribbon.OperationWindow",0);}
        }
    }
}
