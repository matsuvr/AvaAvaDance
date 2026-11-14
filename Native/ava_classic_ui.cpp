#define NOMINMAX
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <commctrl.h>
#include <commdlg.h>
#include <ole2.h>
#include <shellapi.h>
#include <algorithm>
#include <string>
#include <vector>

void registerPanel();
bool translatePanelKey(HWND window,WPARAM key);
using Handler = void (__cdecl *)(HWND, UINT, int, LPARAM);
struct WindowData { Handler handler; HFONT font=nullptr; };
static int pixels(HWND window,int logical){return MulDiv(logical,GetDpiForWindow(window),96);}
static HFONT toolFont(HWND window){
    LOGFONT font{};GetObject(GetStockObject(DEFAULT_GUI_FONT),sizeof(font),&font);
    font.lfHeight=-MulDiv(9,GetDpiForWindow(window),72);return CreateFontIndirect(&font);
}
static BOOL CALLBACK setChildFont(HWND item,LPARAM font){SendMessage(item,WM_SETFONT,font,TRUE);return TRUE;}
static constexpr UINT Dispatch = WM_APP + 1;

static LRESULT CALLBACK windowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l)
{
    if (message == WM_NCCREATE) {
        auto create = reinterpret_cast<CREATESTRUCT*>(l);
        SetWindowLongPtr(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    auto data = reinterpret_cast<WindowData*>(GetWindowLongPtr(hwnd, GWLP_USERDATA));
    if (!data) return DefWindowProc(hwnd, message, w, l);
    switch (message) {
    case WM_HSCROLL:if(l){HWND slider=reinterpret_cast<HWND>(l);data->handler(hwnd,WM_COMMAND,GetDlgCtrlID(slider),0x7000);return 0;}break;
    case WM_VSCROLL: {
        if(l)break;
        SCROLLINFO info{sizeof(info),SIF_ALL};GetScrollInfo(hwnd,SB_VERT,&info);
        int pos=info.nPos;
        switch(LOWORD(w)){case SB_LINEUP:pos-=24;break;case SB_LINEDOWN:pos+=24;break;case SB_PAGEUP:pos-=info.nPage;break;case SB_PAGEDOWN:pos+=info.nPage;break;case SB_THUMBTRACK:case SB_THUMBPOSITION:pos=info.nTrackPos;break;default:return 0;}
        data->handler(hwnd,WM_VSCROLL,pos,0);return 0;
    }
    case WM_MOUSEWHEEL: {
        SCROLLINFO info{sizeof(info),SIF_ALL};GetScrollInfo(hwnd,SB_VERT,&info);
        if(info.nMax>0){data->handler(hwnd,WM_VSCROLL,info.nPos-GET_WHEEL_DELTA_WPARAM(w)/WHEEL_DELTA*48,0);return 0;}break;
    }
    case WM_CREATE:data->font=toolFont(hwnd);data->handler(hwnd,message,0,l);return 0;
    case WM_SIZE:{
        int dpi=GetDpiForWindow(hwnd);data->handler(hwnd,message,static_cast<int>(w),MAKELPARAM(MulDiv(LOWORD(l),96,dpi),MulDiv(HIWORD(l),96,dpi)));return 0;
    }
    case WM_DPICHANGED:{
        auto r=reinterpret_cast<RECT*>(l);auto old=data->font;data->font=toolFont(hwnd);
        EnumChildWindows(hwnd,setChildFont,reinterpret_cast<LPARAM>(data->font));
        SetWindowPos(hwnd,nullptr,r->left,r->top,r->right-r->left,r->bottom-r->top,SWP_NOZORDER|SWP_NOACTIVATE);
        if(old)DeleteObject(old);return 0;
    }
    case WM_SETFOCUS:{auto child=GetDlgItem(hwnd,30);wchar_t name[32]{};if(child && GetClassNameW(child,name,32) && wcscmp(name,L"AvaEditorPanel")==0)SetFocus(child);return 0;}
    case WM_CLOSE: case Dispatch: case WM_DROPFILES:
        data->handler(hwnd, message, static_cast<int>(w), message == WM_DROPFILES ? static_cast<LPARAM>(w) : l);
        return 0;
    case WM_COMMAND:
        data->handler(hwnd, message, LOWORD(w), HIWORD(w)); return 0;
    case WM_NOTIFY: {
        auto header = reinterpret_cast<NMHDR*>(l);
        data->handler(hwnd, message, static_cast<int>(header->idFrom), header->code); return 0;
    }
    case WM_DESTROY:
        if(data->font)DeleteObject(data->font);PostQuitMessage(0); return 0;
    default: break;
    }
    return DefWindowProc(hwnd,message,w,l);
}

#define API extern "C" __declspec(dllexport)
API int ava_ui_open(const wchar_t* title, int width, int height, HWND owner, Handler handler, BOOL visible)
{
    HRESULT initialized = OleInitialize(nullptr);
    if (FAILED(initialized)) return -1;
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_TAB_CLASSES | ICC_LISTVIEW_CLASSES | ICC_PROGRESS_CLASS | ICC_BAR_CLASSES};
    InitCommonControlsEx(&controls);
    WNDCLASS cls{};
    cls.lpfnWndProc = windowProc; cls.hInstance = GetModuleHandle(nullptr);
    cls.hCursor = LoadCursor(nullptr, IDC_ARROW); cls.hbrBackground = GetSysColorBrush(COLOR_BTNFACE);
    cls.lpszClassName = L"AvaDanceToolWindow";
    RegisterClass(&cls);registerPanel();
    auto priorDpi=SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    int dpi=owner?GetDpiForWindow(owner):GetDpiForSystem();
    WindowData data{handler};
    HWND hwnd = CreateWindowEx(WS_EX_ACCEPTFILES | WS_EX_CONTROLPARENT, cls.lpszClassName, title, WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, MulDiv(width,dpi,96), MulDiv(height,dpi,96), owner, nullptr, cls.hInstance, &data);
    int result = 0;
    if (hwnd) {
        if(visible)ShowWindow(hwnd, SW_SHOWNORMAL);
        MSG msg;
        while ((result = GetMessage(&msg, nullptr, 0, 0)) > 0) {
            wchar_t targetClass[32]{};GetClassNameW(msg.hwnd,targetClass,32);
            if(wcscmp(targetClass,L"AvaEditorPanel")==0){
                if(translatePanelKey(msg.hwnd,msg.wParam))TranslateMessage(&msg);
                DispatchMessage(&msg);continue;
            }
            if (msg.message == WM_KEYDOWN && msg.wParam == VK_ESCAPE) { PostMessage(hwnd, WM_CLOSE, 0, 0); continue; }
            if (IsDialogMessage(hwnd, &msg)) continue;
            TranslateMessage(&msg); DispatchMessage(&msg);
        }
    } else result = -1;
    if(priorDpi)SetThreadDpiAwarenessContext(priorDpi);OleUninitialize(); return result;
}

API int ava_ui_run(const wchar_t* title, int width, int height, HWND owner, Handler handler){
    return ava_ui_open(title,width,height,owner,handler,TRUE);
}

API HWND ava_ui_control(HWND window, const wchar_t* cls, const wchar_t* text, int id, DWORD style)
{
    HWND item = CreateWindowEx(0, cls, text, WS_VISIBLE | WS_CHILD | style, 0, 0, 1, 1, window,
        reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), GetModuleHandle(nullptr), nullptr);
    auto data=reinterpret_cast<WindowData*>(GetWindowLongPtr(window,GWLP_USERDATA));
    SendMessage(item, WM_SETFONT, reinterpret_cast<WPARAM>(data->font), FALSE);
    return item;
}
API bool ava_ui_bounds(HWND window,int* box,BOOL restore)
{
    if(!window || !box || IsIconic(window))return false;
    UINT dpi=GetDpiForWindow(window);
    if(!restore){
        RECT r{};if(!GetWindowRect(window,&r))return false;
        box[0]=r.left;box[1]=r.top;box[2]=MulDiv(r.right-r.left,96,dpi);box[3]=MulDiv(r.bottom-r.top,96,dpi);
        return true;
    }
    if(box[2]<220 || box[2]>8192 || box[3]<120 || box[3]>8192)return false;
    POINT pos{box[0],box[1]};HMONITOR monitor=MonitorFromPoint(pos,MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{sizeof(info)};if(!GetMonitorInfo(monitor,&info))return false;
    RECT work=info.rcWork;int w=std::min<int>(MulDiv(box[2],dpi,96),work.right-work.left);
    int h=std::min<int>(MulDiv(box[3],dpi,96),work.bottom-work.top);
    int x=std::clamp<int>(box[0],work.left,work.right-w),y=std::clamp<int>(box[1],work.top,work.bottom-h);
    return SetWindowPos(window,nullptr,x,y,w,h,SWP_NOZORDER|SWP_NOACTIVATE)!=FALSE;
}
API void ava_ui_place(HWND item,int x,int y,int width,int height)
{
    MoveWindow(item,pixels(item,x),pixels(item,y),pixels(item,width),pixels(item,height),TRUE);
}
API void ava_ui_tab(HWND tab, int index, wchar_t* label)
{
    TCITEM item{}; item.mask = TCIF_TEXT; item.pszText = label;
    TabCtrl_InsertItem(tab, index, &item);
}
API void ava_ui_list_setup(HWND list, wchar_t* first, wchar_t* second)
{
    ListView_SetExtendedListViewStyle(list, LVS_EX_CHECKBOXES | LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);
    LVCOLUMN column{};column.mask=LVCF_TEXT|LVCF_WIDTH;column.pszText=first;column.cx=pixels(list,160);
    ListView_InsertColumn(list,0,&column);column.pszText=second;column.cx=340;ListView_InsertColumn(list,1,&column);
}
API void ava_ui_list_row(HWND list,int index,wchar_t* first,wchar_t* second,BOOL checked)
{
    LVITEM row{};row.mask=LVIF_TEXT;row.iItem=index;row.pszText=first;
    if(index>=ListView_GetItemCount(list))ListView_InsertItem(list,&row);
    else ListView_SetItemText(list,index,0,first);
    ListView_SetItemText(list,index,1,second);
    ListView_SetCheckState(list,index,checked);
}
API void ava_ui_select(HWND list,int index)
{
    ListView_SetItemState(list,index,LVIS_SELECTED,LVIS_SELECTED);
}
struct ChoiceData
{
    const wchar_t* title; const wchar_t* labels; int count; int current; bool multiple; unsigned char* selected=nullptr;
};
static INT_PTR CALLBACK choiceProc(HWND hwnd,UINT msg,WPARAM w,LPARAM l)
{
    auto data=reinterpret_cast<ChoiceData*>(GetWindowLongPtr(hwnd,DWLP_USER));
    if(msg==WM_INITDIALOG) {
        data=reinterpret_cast<ChoiceData*>(l);SetWindowLongPtr(hwnd,DWLP_USER,l);SetWindowText(hwnd,data->title);
        RECT r{};GetClientRect(hwnd,&r);int width=r.right,height=r.bottom;
        auto list=CreateWindowEx(WS_EX_CLIENTEDGE,L"LISTBOX",L"",WS_CHILD|WS_VISIBLE|WS_VSCROLL|WS_TABSTOP|LBS_NOTIFY|LBS_NOINTEGRALHEIGHT|
            (data->multiple?LBS_MULTIPLESEL:0),8,8,width-16,height-50,hwnd,reinterpret_cast<HMENU>(10),nullptr,nullptr);
        auto label=data->labels;
        for(int i=0;i<data->count;i++) {
            SendMessage(list,LB_ADDSTRING,0,reinterpret_cast<LPARAM>(label));
            if(data->multiple && (data->selected?data->selected[i]!=0:(data->current&(1<<i))!=0))SendMessage(list,LB_SETSEL,TRUE,i);
            label+=wcslen(label)+1;
        }
        if(!data->multiple)SendMessage(list,LB_SETCURSEL,data->current,0);
        for(int i=0;i<2;i++)CreateWindow(L"BUTTON",i?L"キャンセル":L"OK",WS_VISIBLE|WS_CHILD|WS_TABSTOP|(i?0:BS_DEFPUSHBUTTON),
            width-175+i*85,height-33,78,25,hwnd,reinterpret_cast<HMENU>(static_cast<INT_PTR>(i?IDCANCEL:IDOK)),nullptr,nullptr);
        for(HWND child=GetWindow(hwnd,GW_CHILD);child;child=GetWindow(child,GW_HWNDNEXT))SendMessage(child,WM_SETFONT,reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)),TRUE);
        SetFocus(list);return FALSE;
    }
    if(msg==WM_CLOSE || (msg==WM_COMMAND && LOWORD(w)==IDCANCEL)){EndDialog(hwnd,-1);return TRUE;}
    if(msg==WM_COMMAND && (LOWORD(w)==IDOK || (!data->multiple && LOWORD(w)==10 && HIWORD(w)==LBN_DBLCLK))) {
        HWND list=GetDlgItem(hwnd,10);int result=0;
        if(data->selected){for(int i=0;i<data->count;i++)data->selected[i]=SendMessage(list,LB_GETSEL,i,0)>0;result=1;}
        else if(data->multiple){for(int i=0;i<data->count;i++)if(SendMessage(list,LB_GETSEL,i,0)>0)result|=1<<i;}
        else result=static_cast<int>(SendMessage(list,LB_GETCURSEL,0,0));
        EndDialog(hwnd,result);return TRUE;
    }
    return FALSE;
}
API int ava_ui_choice(HWND owner,const wchar_t* title,const wchar_t* labels,int count,int current,BOOL multiple)
{
    if(count<1 || (multiple && count>30))return -1;
    struct EmptyDialog { DLGTEMPLATE header; WORD menu,windowClass,title; };
    EmptyDialog dialog{};dialog.header.style=WS_POPUP|WS_CAPTION|WS_SYSMENU|DS_MODALFRAME|DS_CENTER;
    dialog.header.cx=275;dialog.header.cy=175;
    ChoiceData data{title,labels,count,current,multiple!=FALSE};
    return static_cast<int>(DialogBoxIndirectParam(GetModuleHandle(nullptr),&dialog.header,owner,choiceProc,reinterpret_cast<LPARAM>(&data)));
}
API int ava_ui_select_many(HWND owner,const wchar_t* title,const wchar_t* labels,int count,unsigned char* selected)
{
    if(count<=0 || !selected)return -1;
    struct EmptyDialog {DLGTEMPLATE header;WORD menu,windowClass,title;} dialog{};
    dialog.header.style=WS_POPUP|WS_CAPTION|WS_SYSMENU|DS_MODALFRAME|DS_CENTER;
    dialog.header.cx=400;dialog.header.cy=250;
    ChoiceData data{title,labels,count,0,true,selected};
    return static_cast<int>(DialogBoxIndirectParam(GetModuleHandle(nullptr),&dialog.header,owner,choiceProc,reinterpret_cast<LPARAM>(&data)));
}
struct OffsetDialog {const wchar_t* labels;int count;int* values;int scroll=0;};
static void offsetLayout(HWND hwnd,OffsetDialog* data)
{
    RECT r{};GetClientRect(hwnd,&r);int w=r.right,h=r.bottom,span=pixels(hwnd,235);
    SCROLLINFO scroll{sizeof(scroll),SIF_RANGE|SIF_PAGE|SIF_POS,0,span*data->count+pixels(hwnd,16)-1,static_cast<UINT>(w),data->scroll};
    SetScrollInfo(hwnd,SB_HORZ,&scroll,TRUE);
    for(int i=0;i<data->count;i++){
        int x=pixels(hwnd,12)+i*span-data->scroll;int id=100+i*3;
        MoveWindow(GetDlgItem(hwnd,id),x,pixels(hwnd,20),span-pixels(hwnd,12),pixels(hwnd,40),TRUE);
        MoveWindow(GetDlgItem(hwnd,id+1),x,pixels(hwnd,65),span-pixels(hwnd,12),pixels(hwnd,36),TRUE);
        MoveWindow(GetDlgItem(hwnd,id+2),x,pixels(hwnd,110),span-pixels(hwnd,12),pixels(hwnd,24),TRUE);
    }
    MoveWindow(GetDlgItem(hwnd,IDOK),w-pixels(hwnd,175),h-pixels(hwnd,37),pixels(hwnd,77),pixels(hwnd,26),TRUE);
    MoveWindow(GetDlgItem(hwnd,IDCANCEL),w-pixels(hwnd,90),h-pixels(hwnd,37),pixels(hwnd,77),pixels(hwnd,26),TRUE);
}
static void offsetText(HWND hwnd,int index)
{
    auto track=GetDlgItem(hwnd,101+index*3);int pos=static_cast<int>(SendMessage(track,TBM_GETPOS,0,0));
    wchar_t text[64];swprintf_s(text,L"%.2f",(pos-40)/20.0);SetDlgItemText(hwnd,102+index*3,text);
}
static INT_PTR CALLBACK offsetsProc(HWND hwnd,UINT msg,WPARAM w,LPARAM l)
{
    auto data=reinterpret_cast<OffsetDialog*>(GetWindowLongPtr(hwnd,DWLP_USER));
    if(msg==WM_INITDIALOG){
        data=reinterpret_cast<OffsetDialog*>(l);SetWindowLongPtr(hwnd,DWLP_USER,l);SetWindowText(hwnd,L"足ＩＫオフセット指定");
        const wchar_t* text=data->labels;
        auto control=[hwnd](const wchar_t* cls,const wchar_t* title,int id,DWORD style){
            return CreateWindow(cls,title,WS_CHILD|WS_VISIBLE|style,0,0,1,1,hwnd,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),nullptr,nullptr);
        };
        for(int i=0;i<data->count;i++){
            int id=100+i*3;control(L"STATIC",text,id,0);text+=wcslen(text)+1;
            auto slider=control(TRACKBAR_CLASS,L"",id+1,WS_TABSTOP|TBS_AUTOTICKS);control(L"STATIC",L"",id+2,0);
            SendMessage(slider,TBM_SETRANGE,TRUE,MAKELPARAM(0,80));SendMessage(slider,TBM_SETPOS,TRUE,data->values[i]+40);offsetText(hwnd,i);
        }
        control(L"BUTTON",L"OK",IDOK,WS_TABSTOP|BS_DEFPUSHBUTTON);control(L"BUTTON",L"キャンセル",IDCANCEL,WS_TABSTOP);
        EnumChildWindows(hwnd,setChildFont,reinterpret_cast<LPARAM>(GetStockObject(DEFAULT_GUI_FONT)));offsetLayout(hwnd,data);return TRUE;
    }
    if(!data)return FALSE;
    if(msg==WM_HSCROLL){
        if(l){int index=(GetDlgCtrlID(reinterpret_cast<HWND>(l))-101)/3;if(index>=0 && index<data->count)offsetText(hwnd,index);return TRUE;}
        SCROLLINFO s{sizeof(s),SIF_ALL};GetScrollInfo(hwnd,SB_HORZ,&s);
        int pos=s.nPos;
        switch(LOWORD(w)){case SB_LINELEFT:pos-=24;break;case SB_LINERIGHT:pos+=24;break;case SB_PAGELEFT:pos-=s.nPage;break;
        case SB_PAGERIGHT:pos+=s.nPage;break;case SB_THUMBPOSITION:case SB_THUMBTRACK:pos=s.nTrackPos;break;default:return TRUE;}
        data->scroll=std::max(0,std::min(pos,s.nMax-static_cast<int>(s.nPage)+1));offsetLayout(hwnd,data);return TRUE;
    }
    if(msg==WM_CLOSE || (msg==WM_COMMAND && LOWORD(w)==IDCANCEL)){EndDialog(hwnd,0);return TRUE;}
    if(msg==WM_COMMAND && LOWORD(w)==IDOK){
        for(int i=0;i<data->count;i++)data->values[i]=static_cast<int>(SendMessage(GetDlgItem(hwnd,101+i*3),TBM_GETPOS,0,0))-40;
        EndDialog(hwnd,1);return TRUE;
    }
    return FALSE;
}
API int ava_ui_offsets(HWND owner,const wchar_t* labels,int count,int* values)
{
    if(!labels || count<1 || count>256 || !values)return 0;
    struct EmptyDialog{DLGTEMPLATE header;WORD menu,windowClass,title;} dialog{};
    dialog.header.style=WS_POPUP|WS_CAPTION|WS_SYSMENU|DS_MODALFRAME|DS_CENTER|WS_HSCROLL;dialog.header.cx=400;dialog.header.cy=250;
    OffsetDialog data{labels,count,values};
    return static_cast<int>(DialogBoxIndirectParam(GetModuleHandle(nullptr),&dialog.header,owner,offsetsProc,reinterpret_cast<LPARAM>(&data)));
}
static BOOL CALLBACK cancelOwned(HWND child,LPARAM owner)
{
    if(child!=reinterpret_cast<HWND>(owner) && GetAncestor(child,GA_ROOTOWNER)==GetAncestor(reinterpret_cast<HWND>(owner),GA_ROOTOWNER)){
        PostMessage(child,WM_COMMAND,IDCANCEL,0);PostMessage(child,WM_CLOSE,0,0);
    }
    return TRUE;
}
API void ava_ui_shutdown(HWND window)
{
    EnumThreadWindows(GetWindowThreadProcessId(window,nullptr),cancelOwned,reinterpret_cast<LPARAM>(window));
    PostMessage(window,WM_CLOSE,0,0);
}
API void ava_ui_scroll(HWND window,int total,int page,int position)
{
    SCROLLINFO info{sizeof(info),SIF_RANGE|SIF_PAGE|SIF_POS,0,total>page?total-1:0,static_cast<UINT>(page),position};
    SetScrollInfo(window,SB_VERT,&info,TRUE);
}
API void ava_ui_copy(HWND window,const wchar_t* text)
{
    if(!OpenClipboard(window))return;
    size_t size=(wcslen(text)+1)*sizeof(wchar_t);HGLOBAL data=GlobalAlloc(GMEM_MOVEABLE,size);
    if(data){auto p=GlobalLock(data);if(p){memcpy(p,text,size);GlobalUnlock(data);EmptyClipboard();if(SetClipboardData(CF_UNICODETEXT,data))data=nullptr;}if(data)GlobalFree(data);}
    CloseClipboard();
}
API int ava_ui_drop_target(HWND window,HDROP files)
{
    POINT pt={};DragQueryPoint(files,&pt);return GetDlgCtrlID(ChildWindowFromPointEx(window,pt,CWP_SKIPINVISIBLE));
}
API BOOL ava_ui_pick(HWND owner, const wchar_t* title, const wchar_t* extensions,const wchar_t* initial, BOOL save, wchar_t* path, DWORD capacity)
{
    std::wstring filters(extensions); filters.push_back(0); filters += extensions;
    filters.push_back(0); filters.push_back(0);
    OPENFILENAME dialog{}; dialog.lStructSize = sizeof(dialog); dialog.hwndOwner = owner;
    dialog.lpstrInitialDir=initial; dialog.lpstrTitle = title; dialog.lpstrFilter = filters.c_str(); dialog.lpstrFile = path; dialog.nMaxFile = capacity;
    dialog.Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST);
    return save ? GetSaveFileName(&dialog) : GetOpenFileName(&dialog);
}
