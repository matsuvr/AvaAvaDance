#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <unordered_map>
using Microsoft::WRL::ComPtr;
namespace {
struct Presenter {
    HWND hwnd{};
    std::atomic<bool> active{true},queued{false};
    std::atomic<int> frames{0};std::atomic<long> result{S_OK};
    std::mutex images;
    ComPtr<ID3D11Texture2D> image;
    ComPtr<IDXGISwapChain> chain;
    ComPtr<ID3D11Device> owner;
    UINT width=0,height=0;
};
std::mutex registry;
std::unordered_map<int,std::shared_ptr<Presenter>> panels;
std::atomic<int> nextId{1},deviceGeneration{1};
std::shared_ptr<Presenter> findPanel(int id){
    std::lock_guard<std::mutex> guard(registry);auto it=panels.find(id);
    return it==panels.end()?nullptr:it->second;
}
HRESULT present(Presenter& p,ID3D11Texture2D* texture){
    ComPtr<ID3D11Device> device;texture->GetDevice(&device);
    D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
    if(!p.active || !IsWindow(p.hwnd))return S_FALSE;
    if(device.Get()!=p.owner.Get()){p.chain.Reset();p.owner=device;}
    if(!p.chain){
        ComPtr<IDXGIDevice> dx;HRESULT hr=device.As(&dx);if(FAILED(hr))return hr;
        ComPtr<IDXGIAdapter> adapter;hr=dx->GetAdapter(&adapter);if(FAILED(hr))return hr;
        ComPtr<IDXGIFactory> factory;hr=adapter->GetParent(IID_PPV_ARGS(&factory));if(FAILED(hr))return hr;
        DXGI_SWAP_CHAIN_DESC config{};config.BufferDesc.Width=desc.Width;config.BufferDesc.Height=desc.Height;
        const bool bgra=desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM || desc.Format==DXGI_FORMAT_B8G8R8A8_TYPELESS || desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        config.BufferDesc.Format=bgra?DXGI_FORMAT_B8G8R8A8_UNORM:DXGI_FORMAT_R8G8B8A8_UNORM;config.SampleDesc.Count=1;
        config.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;config.BufferCount=2;config.OutputWindow=p.hwnd;
        config.Windowed=TRUE;config.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
        hr=factory->CreateSwapChain(device.Get(),&config,&p.chain);if(FAILED(hr))return hr;
        p.width=desc.Width;p.height=desc.Height;
    }else if(p.width!=desc.Width || p.height!=desc.Height){
        HRESULT hr=p.chain->ResizeBuffers(0,desc.Width,desc.Height,DXGI_FORMAT_UNKNOWN,0);if(FAILED(hr))return hr;
        p.width=desc.Width;p.height=desc.Height;
    }
    ComPtr<ID3D11Texture2D> back;HRESULT hr=p.chain->GetBuffer(0,IID_PPV_ARGS(&back));if(FAILED(hr))return hr;
    ComPtr<ID3D11DeviceContext> context;device->GetImmediateContext(&context);
    context->CopySubresourceRegion(back.Get(),0,0,0,0,texture,0,nullptr);
    if(!p.active)return S_FALSE;
    return p.chain->Present(0,DXGI_PRESENT_DO_NOT_WAIT);
}
void __stdcall drawPanel(int id){
    auto p=findPanel(id);if(!p || !p->active)return;
    ComPtr<ID3D11Texture2D> texture;
    {std::lock_guard<std::mutex> lock(p->images);texture.Swap(p->image);}
    if(texture){
        HRESULT hr=present(*p,texture.Get());p->result=hr;
        if(hr==S_OK)p->frames++;
        if(hr==DXGI_ERROR_DEVICE_RESET || hr==DXGI_ERROR_DEVICE_REMOVED){p->chain.Reset();p->owner.Reset();}
    }
    p->queued=false;
}
}
extern "C" __declspec(dllexport) int ava_present_open(HWND hwnd){
    if(!IsWindow(hwnd))return 0;
    auto p=std::make_shared<Presenter>();p->hwnd=hwnd;
    int id=nextId.fetch_add(1);std::lock_guard<std::mutex> lock(registry);panels.emplace(id,p);return id;
}
extern "C" __declspec(dllexport) int ava_present_queue(int id,ID3D11Texture2D* texture){
    auto p=findPanel(id);if(!p || !texture || p->queued.exchange(true))return 0;
    std::lock_guard<std::mutex> lock(p->images);p->image=texture;return 1;
}
extern "C" __declspec(dllexport) int ava_present_frames(int id,long* result){
    auto p=findPanel(id);if(!p)return 0;*result=p->result;return p->frames;
}
extern "C" __declspec(dllexport) void ava_present_close(int id){
    std::shared_ptr<Presenter> p;{
        std::lock_guard<std::mutex> lock(registry);auto it=panels.find(id);if(it==panels.end())return;
        p=it->second;p->active=false;panels.erase(it);
    }
}
extern "C" __declspec(dllexport) void* ava_present_event(){return reinterpret_cast<void*>(&drawPanel);}
extern "C" __declspec(dllexport) int ava_present_generation(){return deviceGeneration.load();}
void resetPanelDevices(){
    deviceGeneration++;
    std::lock_guard<std::mutex> lock(registry);
    for(auto& item:panels){auto& p=*item.second;std::lock_guard<std::mutex> guard(p.images);
        p.chain.Reset();p.owner.Reset();p.image.Reset();p.queued=false;}
}
