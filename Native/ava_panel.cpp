#define NOMINMAX
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <windowsx.h>
#include <imm.h>
#include <string>
#include <deque>
#include <mutex>

namespace {
using Input=void (__cdecl *)(int,int,int,int,int,int,const wchar_t*);
struct Panel {Input input=nullptr;bool composing=false, tracking=false;};
enum { Resize=1, Down, Up, Move, Wheel, KeyDown, KeyUp, Text, Focus, Blur, Composition, Commit, LostCapture, Leave };
int modifiers(){return ((GetKeyState(VK_SHIFT)<0)?1:0)|((GetKeyState(VK_CONTROL)<0)?2:0)|((GetKeyState(VK_MENU)<0)?4:0);}
void send(Panel* p,int kind,int x=0,int y=0,int a=0,int b=0,const wchar_t* text=nullptr){
    if(p && p->input)p->input(kind,x,y,a,b,modifiers(),text);
}
void measure(HWND h,Panel* p){
    RECT r{};GetClientRect(h,&r);send(p,Resize,r.right,r.bottom,GetDpiForWindow(h));
}
void ime(HWND h,Panel* p,DWORD id,int kind){
    auto ctx=ImmGetContext(h);if(!ctx)return;
    LONG bytes=ImmGetCompositionStringW(ctx,id,nullptr,0);
    if(bytes>=0){std::wstring s(static_cast<size_t>(bytes)/sizeof(wchar_t),0);
        if(bytes)ImmGetCompositionStringW(ctx,id,s.data(),bytes);
        send(p,kind,0,0,0,0,s.c_str());}
    ImmReleaseContext(h,ctx);
}
LRESULT CALLBACK panelProc(HWND h,UINT m,WPARAM w,LPARAM l){
    auto p=reinterpret_cast<Panel*>(GetWindowLongPtr(h,GWLP_USERDATA));
    int x=GET_X_LPARAM(l),y=GET_Y_LPARAM(l);
    switch(m){
    case WM_CREATE:SetWindowLongPtr(h,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(new Panel()));return 0;
    case WM_NCDESTROY:SetWindowLongPtr(h,GWLP_USERDATA,0);delete p;break;
    case WM_SIZE:case WM_DPICHANGED_AFTERPARENT:measure(h,p);return 0;
    case WM_SETFOCUS:send(p,Focus);return 0;
    case WM_KILLFOCUS:send(p,Blur);return 0;
    case WM_GETDLGCODE:return DLGC_WANTALLKEYS|DLGC_WANTCHARS|DLGC_WANTTAB|DLGC_WANTARROWS;
    case WM_MOUSEMOVE:{
        if(p && !p->tracking){TRACKMOUSEEVENT t{sizeof(t),TME_LEAVE,h,0};p->tracking=TrackMouseEvent(&t)!=FALSE;}
        send(p,Move,x,y);return 0;}
    case WM_MOUSELEAVE:if(p)p->tracking=false;send(p,Leave);return 0;
    case WM_XBUTTONDOWN:case WM_XBUTTONDBLCLK:
        SetFocus(h);SetCapture(h);send(p,Down,x,y,GET_XBUTTON_WPARAM(w)==XBUTTON1?3:4,m==WM_XBUTTONDBLCLK?2:1);return TRUE;
    case WM_XBUTTONUP:
        send(p,Up,x,y,GET_XBUTTON_WPARAM(w)==XBUTTON1?3:4,1);if(GetCapture()==h)ReleaseCapture();return TRUE;
    case WM_LBUTTONDOWN:case WM_RBUTTONDOWN:case WM_MBUTTONDOWN:
    case WM_LBUTTONDBLCLK:case WM_RBUTTONDBLCLK:case WM_MBUTTONDBLCLK:
        SetFocus(h);SetCapture(h);send(p,Down,x,y,(m==WM_RBUTTONDOWN||m==WM_RBUTTONDBLCLK)?1:(m==WM_MBUTTONDOWN||m==WM_MBUTTONDBLCLK)?2:0,
            m==WM_LBUTTONDBLCLK||m==WM_RBUTTONDBLCLK||m==WM_MBUTTONDBLCLK?2:1);return 0;
    case WM_LBUTTONUP:case WM_RBUTTONUP:case WM_MBUTTONUP:
        send(p,Up,x,y,m==WM_RBUTTONUP?1:m==WM_MBUTTONUP?2:0,1);
        if(!(w&(MK_LBUTTON|MK_RBUTTON|MK_MBUTTON)) && GetCapture()==h)ReleaseCapture();return 0;
    case WM_CAPTURECHANGED:send(p,LostCapture);return 0;
    case WM_MOUSEWHEEL:case WM_MOUSEHWHEEL:{
        POINT pt{x,y};ScreenToClient(h,&pt);send(p,Wheel,pt.x,pt.y,GET_WHEEL_DELTA_WPARAM(w),m==WM_MOUSEHWHEEL);return 0;}
    case WM_SYSKEYDOWN:case WM_SYSKEYUP:if(w==VK_F4 && GetKeyState(VK_MENU)<0){if(m==WM_SYSKEYDOWN)PostMessage(GetParent(h),WM_CLOSE,0,0);return 0;}[[fallthrough]];
    case WM_KEYDOWN:case WM_KEYUP:
        send(p,m==WM_KEYUP||m==WM_SYSKEYUP?KeyUp:KeyDown,MapVirtualKeyExW(static_cast<UINT>(w),MAPVK_VK_TO_CHAR,GetKeyboardLayout(0))&0xffff,LOWORD(reinterpret_cast<ULONG_PTR>(GetKeyboardLayout(0))),static_cast<int>(w),static_cast<int>(l));return 0;
    case WM_CHAR:if(p && !p->composing && w>=32 && w!=127){wchar_t s[]={static_cast<wchar_t>(w),0};send(p,Text,0,0,0,0,s);}return 0;
    case WM_IME_STARTCOMPOSITION:if(p)p->composing=true;send(p,Composition,0,0,1);break;
    case WM_IME_COMPOSITION:
        if(l&GCS_RESULTSTR)ime(h,p,GCS_RESULTSTR,Commit);
        // The system IME renders marked text, clause highlights and candidates at the field's caret.
        l&=~(GCS_RESULTSTR|GCS_RESULTCLAUSE|GCS_RESULTREADSTR|GCS_RESULTREADCLAUSE);
        if(l)return DefWindowProc(h,m,w,l);
        return 0;
    case WM_IME_ENDCOMPOSITION:if(p)p->composing=false;send(p,Composition);break;
    case WM_IME_CHAR:{wchar_t s[]={static_cast<wchar_t>(w),0};send(p,Commit,0,0,0,0,s);return 0;}
    case WM_ERASEBKGND:return 1;
    case WM_PAINT:{PAINTSTRUCT paint{};BeginPaint(h,&paint);EndPaint(h,&paint);return 0;}
    }
    return DefWindowProc(h,m,w,l);
}
}
// Unity's cooked punctuation can discard the distinction between two keys with the same glyph.
namespace {
struct OemKey {int vk,scan,glyph,layout,mods,up;};
std::mutex keyLock;
std::deque<OemKey> keyEvents;
HWND keyWindow=nullptr;
HHOOK keyHook=nullptr;
LRESULT CALLBACK editorMessages(int code,WPARAM removed,LPARAM data){
    if(code==HC_ACTION && removed==PM_REMOVE){
        auto msg=reinterpret_cast<MSG*>(data);
        if(msg->hwnd==keyWindow && (msg->message==WM_KEYDOWN || msg->message==WM_KEYUP || msg->message==WM_SYSKEYDOWN || msg->message==WM_SYSKEYUP)){
            int vk=static_cast<int>(msg->wParam);
            if((vk>=VK_OEM_1 && vk<=VK_OEM_3) || (vk>=VK_OEM_4 && vk<=VK_OEM_8) || vk==VK_OEM_102){
                auto layout=GetKeyboardLayout(0);
                OemKey event{vk,static_cast<int>((msg->lParam>>16)&0x1ff),static_cast<int>(MapVirtualKeyExW(vk,MAPVK_VK_TO_CHAR,layout)&0xffff),
                    LOWORD(reinterpret_cast<ULONG_PTR>(layout)),modifiers(),msg->message==WM_KEYUP || msg->message==WM_SYSKEYUP};
                std::lock_guard<std::mutex> lock(keyLock);
                if(keyEvents.size()==128)keyEvents.pop_front();
                keyEvents.push_back(event);
            }
        }
    }
    return CallNextHookEx(nullptr,code,removed,data);
}
}
extern "C" __declspec(dllexport) int ava_editor_keyboard(HWND window){
    if(keyHook){UnhookWindowsHookEx(keyHook);keyHook=nullptr;}
    {std::lock_guard<std::mutex> lock(keyLock);keyEvents.clear();}
    keyWindow=window;if(!window)return 0;
    DWORD pid=0;auto thread=GetWindowThreadProcessId(window,&pid);
    if(pid!=GetCurrentProcessId() || !thread)return 0;
    HMODULE module=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&editorMessages),&module);
    keyHook=SetWindowsHookExW(WH_GETMESSAGE,editorMessages,module,thread);
    return keyHook!=nullptr;
}
extern "C" __declspec(dllexport) int ava_editor_key(OemKey* out){
    std::lock_guard<std::mutex> lock(keyLock);if(keyEvents.empty())return 0;
    *out=keyEvents.front();keyEvents.pop_front();return 1;
}
bool translatePanelKey(HWND h,WPARAM key){
    auto p=reinterpret_cast<Panel*>(GetWindowLongPtr(h,GWLP_USERDATA));
    return !p || !p->composing || key==VK_PROCESSKEY;
}
void registerPanel(){
    WNDCLASS cls{};cls.style=CS_DBLCLKS;cls.lpfnWndProc=panelProc;cls.hInstance=GetModuleHandle(nullptr);
    cls.hCursor=LoadCursor(nullptr,IDC_ARROW);cls.lpszClassName=L"AvaEditorPanel";RegisterClass(&cls);
}
extern "C" __declspec(dllexport) void ava_panel_input(HWND h,Input input){
    auto p=reinterpret_cast<Panel*>(GetWindowLongPtr(h,GWLP_USERDATA));if(p){p->input=input;ImmAssociateContextEx(h,nullptr,0);measure(h,p);}
}
extern "C" __declspec(dllexport) void ava_panel_editing(HWND h,int editing){
    ImmAssociateContextEx(h,nullptr,editing?IACE_DEFAULT:0);
}
extern "C" __declspec(dllexport) void ava_panel_caret(HWND h,int x,int y,int height){
    auto ctx=ImmGetContext(h);if(!ctx)return;
    COMPOSITIONFORM form{};form.dwStyle=CFS_POINT;form.ptCurrentPos={x,y};ImmSetCompositionWindow(ctx,&form);
    CANDIDATEFORM cf{};cf.dwStyle=CFS_EXCLUDE;cf.ptCurrentPos={x,y};cf.rcArea={x,y,x+1,y+height};
    ImmSetCandidateWindow(ctx,&cf);ImmReleaseContext(h,ctx);
}
