using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ava.Extensions
{
    public abstract class ClassicWindow : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Handler(IntPtr hwnd, uint msg, int id, IntPtr data);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_ui_open(string title,int width,int height,IntPtr owner,Handler handler,bool visible);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr ava_ui_control(IntPtr parent,string cls,string text,int id,uint style);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern void ava_ui_tab(IntPtr tab,int index,string label);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern bool ava_ui_pick(IntPtr owner,string title,string pattern,string initial,bool save,StringBuilder path,int capacity);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_ui_choice(IntPtr owner,string title,string labels,int count,int current,bool multiple);
        [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_ui_drop_target(IntPtr window,IntPtr files);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern void ava_ui_copy(IntPtr window,string text);
        protected void CopyText(string value)=>ava_ui_copy(window,value);
        [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)] static extern void ava_ui_scroll(IntPtr window,int total,int page,int position);
        protected void ScrollRange(int total,int page,int position)=>ava_ui_scroll(window,total,page,position);
        protected virtual void Scroll(int position){}
        protected int DropTarget(IntPtr files)=>ava_ui_drop_target(window,files);
        protected int Choose(string title,string[] labels,int selected=0,bool multiple=false)
            =>ava_ui_choice(window,title,string.Join("\0",labels)+"\0",labels.Length,selected,multiple);
        [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]static extern void ava_ui_shutdown(IntPtr window);
        [DllImport("user32")] static extern bool PostMessageW(IntPtr target,uint message,IntPtr w,IntPtr l);
        [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)] static extern void ava_ui_place(IntPtr item,int x,int y,int w,int h);
        [DllImport("user32")] static extern bool EnableWindow(IntPtr item,bool enable);
        [DllImport("user32")] static extern bool ShowWindow(IntPtr item,int show);
        [DllImport("user32",CharSet=CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr item,string text);
        [DllImport("user32",CharSet=CharSet.Unicode)] static extern int GetWindowTextLengthW(IntPtr item);
        [DllImport("user32",CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr item,StringBuilder text,int capacity);
        [DllImport("user32")] protected static extern IntPtr SendMessageW(IntPtr item,uint msg,IntPtr w,IntPtr l);

        [DllImport("user32")]static extern IntPtr CreateMenu();
        [DllImport("user32")]static extern IntPtr CreatePopupMenu();
        [DllImport("user32")]static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32",CharSet=CharSet.Unicode)]static extern bool AppendMenuW(IntPtr menu,uint flags,IntPtr id,string label);
        [DllImport("user32")]static extern bool SetMenu(IntPtr window,IntPtr menu);
        [DllImport("user32")]static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr owner,IntPtr rect);
        [DllImport("user32")]static extern bool GetCursorPos(out Point position);
        [StructLayout(LayoutKind.Sequential)]struct Point {public int x,y;}
        IntPtr menuBar;
        readonly IWindowHost context;
        readonly object gate=new object();
        readonly ConcurrentQueue<Action> queue=new ConcurrentQueue<Action>();
        readonly Dictionary<int,IntPtr> controls=new Dictionary<int,IntPtr>();
        [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)]
        static extern bool ava_ui_bounds(IntPtr hwnd,[In,Out]int[] box,bool restore);
        string placementFile;
        Thread thread;
        System.Threading.Tasks.TaskCompletionSource<bool> closed;
        IntPtr window;
        bool disposed;
        protected ClassicWindow(IWindowHost context) { this.context=context; }
        protected abstract void Build();
        protected abstract void Layout(int width,int height);
        protected abstract void Command(int id,int notification);
        protected virtual void Notify(int id,int notification) {}
        protected virtual void Closing() {}
        protected virtual void Drop(IntPtr files) { DragFinish(files); }
        [DllImport("shell32")] protected static extern void DragFinish(IntPtr files);

        public void Open(string title,int width,int height,bool owned=false)=>OpenWindow(title,width,height,owned,true);
        protected void OpenWindow(string title,int width,int height,bool owned,bool visible)
        {
            lock(gate)
            {
                if(disposed) throw new ObjectDisposedException(GetType().Name);
                if(thread!=null) { if(window!=IntPtr.Zero) SetForegroundWindow(window); return; }
                var lease=context.KeepActive();
                var completion=closed=new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                var owner=owned?context.MainWindow:IntPtr.Zero;
                string folder=(context as IPluginContext)?.SettingsDirectory??(context as IWindowSettings)?.WindowSettingsDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AvaAvaDance","Windows");
                var filename=new string(title.Where(c=>!Path.GetInvalidFileNameChars().Contains(c)).Take(100).ToArray());
                placementFile=Path.Combine(folder,"window-"+filename+".txt");
                thread=new Thread(()=>{
                    try {
                        if(ava_ui_open(title,width,height,owner,Dispatch,visible)<0) throw new InvalidOperationException("ウィンドウを作成できません。");
                    } catch(Exception ex) { context.Post(()=>context.Report(ex.Message)); }
                    finally { lock(gate) {window=IntPtr.Zero; thread=null; controls.Clear(); while(queue.TryDequeue(out _)){} } lease.Dispose();completion.TrySetResult(true); }
                }) { IsBackground=true,Name=title };
                thread.SetApartmentState(ApartmentState.STA); thread.Start();
            }
        }

        void Dispatch(IntPtr hwnd,uint message,int id,IntPtr data)
        {
            try {
                switch(message)
                {
                    case 1:
                        lock(gate) {window=hwnd; if(disposed) {PostMessageW(hwnd,16,IntPtr.Zero,IntPtr.Zero);return;} }
                        menuBar=IntPtr.Zero;Build();Placement(hwnd,true);break;
                    case 5: int size=data.ToInt32(); Layout(size & 65535,(size>>16)&65535); break;
                    case 16: try { Placement(hwnd,false);Closing(); } finally { DestroyWindow(hwnd); } break;
                    case 277: Scroll(id);break;
                    case 273: Command(id,data.ToInt32()); break;
                    case 78: Notify(id,unchecked((int)data.ToInt64())); break;
                    case 563: Drop(data); break;
                    case 32769: while(queue.TryDequeue(out var action)) action(); break;
                }
            } catch(Exception ex) { context.Post(()=>context.Report(ex.Message)); }
        }

        void Placement(IntPtr hwnd,bool restore)
        {
            try{
                if(restore){
                    if(!File.Exists(placementFile))return;
                    var parts=File.ReadAllLines(placementFile);var box=new int[4];
                    if(parts.Length==4 && Enumerable.Range(0,4).All(i=>int.TryParse(parts[i],out box[i])))ava_ui_bounds(hwnd,box,true);
                }else{
                    var box=new int[4];if(!ava_ui_bounds(hwnd,box,false))return;
                    Directory.CreateDirectory(Path.GetDirectoryName(placementFile));
                    VmdDocument.AtomicFile(placementFile,tmp=>File.WriteAllLines(tmp,Array.ConvertAll(box,n=>n.ToString())));
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        public bool IsOpen { get { lock(gate) return thread!=null; } }
        protected void OnWindow(Action action)
        {
            lock(gate) {
                if(window==IntPtr.Zero || disposed) return;
                queue.Enqueue(action); PostMessageW(window,32769,IntPtr.Zero,IntPtr.Zero);
            }
        }
        static IntPtr MakeMenu((int id,string label)[] items)
        {
            var menu=CreatePopupMenu();
            foreach(var item in items)AppendMenuW(menu,item.id==0?0x800u:0,new IntPtr(item.id),item.label);
            return menu;
        }
        protected void Menu(string caption,params (int id,string label)[] items)
        {
            if(menuBar==IntPtr.Zero)menuBar=CreateMenu();
            AppendMenuW(menuBar,0x10,MakeMenu(items),caption);SetMenu(window,menuBar);
        }
        [DllImport("user32")]static extern uint CheckMenuItem(IntPtr menu,uint id,uint flags);
        protected void MenuChecked(int id,bool value){if(menuBar!=IntPtr.Zero)CheckMenuItem(menuBar,(uint)id,value?8u:0u);}
        protected void Popup(params (int id,string label)[] items)
        {
            var menu=MakeMenu(items);
            try{GetCursorPos(out var at);int id=(int)TrackPopupMenu(menu,0x102,at.x,at.y,0,window,IntPtr.Zero);if(id!=0)Command(id,0);}
            finally{DestroyMenu(menu);}
        }
        protected IntPtr Add(int id,string cls,string label,uint style=0)
        {
            var control=ava_ui_control(window,cls,label,id,style);
            if(control==IntPtr.Zero) throw new InvalidOperationException("コントロールを作成できません: "+cls);
            controls[id]=control; return control;
        }
        protected void Tabs(int id,params string[] labels)
        {
            Add(id,"SysTabControl32","",0x10000);SetTabs(id,labels,0);
        }
        protected void SetTabs(int id,string[] labels,int selected)
        {
            var tab=controls[id];SendMessageW(tab,0x1309,IntPtr.Zero,IntPtr.Zero);
            for(int i=0;i<labels.Length;i++)ava_ui_tab(tab,i,labels[i]);
            SendMessageW(tab,0x130c,new IntPtr(selected),IntPtr.Zero);
        }
        protected IntPtr WindowHandle=>window;
        protected IntPtr Control(int id) => controls[id];
        protected void Place(int id,int x,int y,int w,int h) {if(controls.TryGetValue(id,out var c))ava_ui_place(c,x,y,Math.Max(1,w),Math.Max(1,h));}
        protected void Show(int id,bool visible) => ShowWindow(controls[id],visible?5:0);
        protected void Enable(int id,bool enabled) => EnableWindow(controls[id],enabled);
        protected void Text(int id,string value) => SetWindowTextW(controls[id],value);
        protected string Text(int id) {var c=controls[id];var s=new StringBuilder(GetWindowTextLengthW(c)+1);GetWindowTextW(c,s,s.Capacity);return s.ToString();}
        protected string Pick(string title,string pattern,bool save=false,string initial=null)
        {
            var value=new StringBuilder(32768);
            return ava_ui_pick(window,title,pattern,initial,save,value,value.Capacity)?value.ToString():null;
        }
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_ui_select_many(IntPtr window,string title,string items,int count,[In,Out]byte[] selected);
        protected bool ChooseItems(string title,string[] labels,byte[] selection)
        {
            if(labels.Length!=selection.Length)throw new ArgumentException(nameof(selection));
            return ava_ui_select_many(window,title,string.Join("\0",labels)+"\0",labels.Length,selection)>0;
        }
        protected void Close() => PostMessageW(window,16,IntPtr.Zero,IntPtr.Zero);
        public System.Threading.Tasks.Task CloseAsync()
        {
            lock(gate){
                disposed=true;
                if(window!=IntPtr.Zero)ava_ui_shutdown(window);
                return closed?.Task??System.Threading.Tasks.Task.CompletedTask;
            }
        }
        public virtual void Dispose()
        {
            var done=CloseAsync();
            if(Thread.CurrentThread!=thread)done.GetAwaiter().GetResult();
        }
    }
}
