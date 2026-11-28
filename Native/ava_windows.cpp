#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <algorithm>
#include <deque>
#include <mutex>
#include <string>

// Unity's cooked punctuation can discard the distinction between two keys with the same glyph.
namespace {
int modifiers(){return ((GetKeyState(VK_SHIFT)<0)?1:0)|((GetKeyState(VK_CONTROL)<0)?2:0)|((GetKeyState(VK_MENU)<0)?4:0);}
struct OemKey {int vk,scan,glyph,layout,mods,up;};
std::mutex keyLock;
std::deque<OemKey> keyEvents;
std::deque<std::wstring> droppedFiles;
HWND keyWindow=nullptr;
HHOOK keyHook=nullptr;
LRESULT CALLBACK editorMessages(int code,WPARAM removed,LPARAM data){
    if(code==HC_ACTION && removed==PM_REMOVE){
        auto msg=reinterpret_cast<MSG*>(data);
        if(msg->hwnd==keyWindow && msg->message==WM_DROPFILES){
            const auto drop=reinterpret_cast<HDROP>(msg->wParam);
            const UINT count=DragQueryFileW(drop,0xffffffff,nullptr,0);
            std::lock_guard<std::mutex> lock(keyLock);
            for(UINT i=0;i<count && droppedFiles.size()<255;i++){
                UINT length=DragQueryFileW(drop,i,nullptr,0);
                std::wstring path(length+1,L'\0');
                if(DragQueryFileW(drop,i,path.data(),length+1)==length){
                    path.resize(length);droppedFiles.push_back(std::move(path));
                }
            }
            DragFinish(drop);msg->message=WM_NULL;msg->wParam=0;
        }
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
    if(keyWindow && IsWindow(keyWindow))DragAcceptFiles(keyWindow,FALSE);
    {std::lock_guard<std::mutex> lock(keyLock);keyEvents.clear();droppedFiles.clear();}
    keyWindow=window;if(!window)return 0;
    DWORD pid=0;auto thread=GetWindowThreadProcessId(window,&pid);
    if(pid!=GetCurrentProcessId() || !thread)return 0;
    HMODULE module=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&editorMessages),&module);
    keyHook=SetWindowsHookExW(WH_GETMESSAGE,editorMessages,module,thread);
    if(keyHook)DragAcceptFiles(window,TRUE);
    return keyHook!=nullptr;
}
extern "C" __declspec(dllexport) int ava_editor_key(OemKey* out){
    std::lock_guard<std::mutex> lock(keyLock);if(keyEvents.empty())return 0;
    *out=keyEvents.front();keyEvents.pop_front();return 1;
}
extern "C" __declspec(dllexport) int ava_editor_drop(wchar_t* out,int capacity){
    std::lock_guard<std::mutex> lock(keyLock);
    if(droppedFiles.empty())return 0;
    const auto& path=droppedFiles.front();
    int size=static_cast<int>(path.size()+1);
    if(!out)return size;
    if(capacity<size)return 0;
    std::wmemcpy(out,path.c_str(),size);droppedFiles.pop_front();return size;
}

extern "C" __declspec(dllexport) bool ava_ui_bounds(HWND window,int* box,BOOL restore)
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
