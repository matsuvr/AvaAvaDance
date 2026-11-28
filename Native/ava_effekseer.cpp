#define NOMINMAX
#include <d3d11_1.h>
#include <Effekseer.h>
#include <EffekseerRendererDX11.h>
#include "effekseer_playback.h"
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <filesystem>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <Effekseer/Effekseer.EffectNode.h>
#include <EffekseerRendererDX11/GraphicsDevice.h>

using Microsoft::WRL::ComPtr;
namespace E = Effekseer;
using Bytes = std::vector<uint8_t>;
using PathCallback = void (__cdecl*)(const char16_t*);
#define API extern "C" __declspec(dllexport)

// Resource discovery uses no graphics device and no ambient filesystem.
API int ava_efk_inspect(const void* data, int length, PathCallback visit) {
    if (!data || length < 8 || !visit) return 0;
    try {
        auto effect = E::Effect::Create(E::Setting::Create(), data, length);
        if (!effect) return 0;
        auto paths = [&](auto count, auto path) {
            for (int i = 0; i < count; ++i) {
                auto value = (effect.Get()->*path)(i);
                if (value && *value) visit(value);
            }
        };
        paths(effect->GetColorImageCount(), &E::Effect::GetColorImagePath);
        paths(effect->GetNormalImageCount(), &E::Effect::GetNormalImagePath);
        paths(effect->GetDistortionImageCount(), &E::Effect::GetDistortionImagePath);
        paths(effect->GetModelCount(), &E::Effect::GetModelPath);
        paths(effect->GetMaterialCount(), &E::Effect::GetMaterialPath);
        paths(effect->GetCurveCount(), &E::Effect::GetCurvePath);
        paths(effect->GetWaveCount(), &E::Effect::GetWavePath);
        return 1;
    } catch (...) { return 0; }
}

namespace {
// CPU particle renderers accept a depth function directly. GPU particle passes
// create their own pipeline states and must use the camera's reversed Z as well.
class UnityGraphicsDevice : public EffekseerRendererDX11::Backend::GraphicsDevice {
public:
    using GraphicsDevice::GraphicsDevice;
    E::Backend::PipelineStateRef CreatePipelineState(const E::Backend::PipelineStateParameter& param) override {
        using Depth = E::Backend::DepthFuncType;
        auto state = param;
        switch (state.DepthFunc) {
            case Depth::Less: state.DepthFunc=Depth::Greater; break;
            case Depth::Greater: state.DepthFunc=Depth::Less; break;
            case Depth::LessEqual: state.DepthFunc=Depth::GreaterEqual; break;
            case Depth::GreaterEqual: state.DepthFunc=Depth::LessEqual; break;
            default: break;
        }
        return GraphicsDevice::CreatePipelineState(state);
    }
};
struct Reader final : E::FileReader {
    const Bytes& data;
    size_t offset = 0;
    explicit Reader(const Bytes& source) : data(source) {}
    size_t Read(void* out, size_t size) override {
        size = std::min(size, data.size() - offset);
        if (size) memcpy(out, data.data() + offset, size);
        offset += size; return size;
    }
    void Seek(int p) override { offset = std::min(data.size(), static_cast<size_t>(std::max(0,p))); }
    int GetPosition() const override { return static_cast<int>(offset); }
    size_t GetLength() const override { return data.size(); }
};
std::wstring key(const char16_t* path) {
    auto value = std::filesystem::path(path).lexically_normal().generic_wstring();
    CharLowerBuffW(value.data(), static_cast<DWORD>(value.size()));
    return value;
}
struct Resources final : E::FileInterface {
    std::unordered_map<std::wstring,Bytes> entries;
    E::FileReaderRef OpenRead(const char16_t* path) override {
        auto it = entries.find(key(path));
        if (it == entries.end()) return nullptr;
        return E::MakeRefPtr<Reader>(it->second);
    }
    E::FileWriterRef OpenWrite(const char16_t*) override { return nullptr; }
};

// Snapshot the bound camera color when a distortion node actually requests it.
struct Background final : EffekseerRenderer::DistortingCallback {
    ID3D11Device* device;
    ID3D11DeviceContext* context;
    ID3D11RenderTargetView* target = nullptr;
    ComPtr<ID3D11Texture2D> copy;
    ComPtr<ID3D11ShaderResourceView> view;
    D3D11_TEXTURE2D_DESC previous{};
    Background(ID3D11Device* d, ID3D11DeviceContext* c) : device(d), context(c) {}
    bool OnDistorting(EffekseerRenderer::Renderer* renderer) override {
        if (!target) return false;
        ComPtr<ID3D11Resource> source;
        target->GetResource(&source);
        ComPtr<ID3D11Texture2D> texture;
        if (FAILED(source.As(&texture))) return false;
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        D3D11_RENDER_TARGET_VIEW_DESC rtv;
        target->GetDesc(&rtv);
        if (!copy || previous.Width != desc.Width || previous.Height != desc.Height || previous.Format != rtv.Format) {
            D3D11_TEXTURE2D_DESC dest{};
            dest.Width=desc.Width; dest.Height=desc.Height; dest.Format=rtv.Format; dest.ArraySize=1;
            dest.MipLevels=1; dest.SampleDesc.Count=1; dest.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            if (FAILED(device->CreateTexture2D(&dest,nullptr,copy.ReleaseAndGetAddressOf())) ||
                FAILED(device->CreateShaderResourceView(copy.Get(),nullptr,view.ReleaseAndGetAddressOf()))) return false;
            previous=dest;
        }
        if (desc.SampleDesc.Count > 1)
            context->ResolveSubresource(copy.Get(),0,texture.Get(),0,rtv.Format);
        else context->CopySubresourceRegion(copy.Get(),0,0,0,0,texture.Get(),0,nullptr);
        static_cast<EffekseerRendererDX11::Renderer*>(renderer)->SetBackground(view.Get());
        return true;
    }
};
struct Frame {
    ComPtr<ID3D11Resource> target;
    E::Matrix44 view, projection, center, base;
    std::array<float,12> controls;
};
struct CameraState {
    ID3D11DeviceContext1* context;
    ComPtr<ID3DDeviceContextState> original;
    CameraState(ID3D11DeviceContext1* ctx, ID3DDeviceContextState* isolated) : context(ctx) {
        context->SwapDeviceContextState(isolated,&original);
    }
    ~CameraState() {
        context->OMSetRenderTargets(0,nullptr,nullptr);
        context->SwapDeviceContextState(original.Get(),nullptr);
    }
};
E::Matrix43 affine(const E::Matrix44& matrix) {
    E::Matrix43 result;
    for (int i=0;i<4;++i) for (int j=0;j<3;++j) result.Value[i][j]=matrix.Values[i][j];
    return result;
}
struct Instance {
    Bytes source;
    E::RefPtr<Resources> files = E::MakeRefPtr<Resources>();
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11DeviceContext1> context1;
    ComPtr<ID3DDeviceContextState> isolated;
    EffekseerRendererDX11::RendererRef renderer;
    EffectPlayback playback;
    std::atomic<bool> failed{false};
    Background* background = nullptr; // owned by renderer
    bool selectDevice(ID3D11Device* next) {
        playback = {}; renderer.Reset(); isolated.Reset(); context1.Reset(); context.Reset(); device = next;
        device->GetImmediateContext(&context);
        ComPtr<ID3D11Device1> device1;
        if (FAILED(device.As(&device1)) || FAILED(context.As(&context1))) return false;
        auto level=device->GetFeatureLevel();
        return SUCCEEDED(device1->CreateDeviceContextState(0,&level,1,D3D11_SDK_VERSION,__uuidof(ID3D11Device),nullptr,isolated.ReleaseAndGetAddressOf()));
    }
    static bool usesGpuParticles(E::EffectNode* node) {
        if (static_cast<E::EffectNodeImplemented*>(node)->GpuParticlesResource) return true;
        for (int i=0;i<node->GetChildrenCount();++i)
            if (usesGpuParticles(node->GetChild(i))) return true;
        return false;
    }
    bool initialize() {
        auto graphics = E::MakeRefPtr<UnityGraphicsDevice>(device.Get(),context.Get());
        renderer = EffekseerRendererDX11::Renderer::Create(graphics,8192,D3D11_COMPARISON_GREATER_EQUAL);
        if (!renderer) return false;
        renderer->SetRestorationOfStatesFlag(false);
        background = new Background(device.Get(),context.Get());
        renderer->SetDistortingCallback(background);
        auto manager = E::Manager::Create(8192);
        manager->SetRandFunc([] { return 1; });
        manager->SetCoordinateSystem(E::CoordinateSystem::LH);
        manager->SetSpriteRenderer(renderer->CreateSpriteRenderer());
        manager->SetRibbonRenderer(renderer->CreateRibbonRenderer());
        manager->SetRingRenderer(renderer->CreateRingRenderer());
        manager->SetTrackRenderer(renderer->CreateTrackRenderer());
        manager->SetModelRenderer(renderer->CreateModelRenderer());
        manager->SetTextureLoader(renderer->CreateTextureLoader(files));
        manager->SetModelLoader(renderer->CreateModelLoader(files));
        manager->SetMaterialLoader(renderer->CreateMaterialLoader(files));
        manager->SetCurveLoader(E::MakeRefPtr<E::CurveLoader>(files));
        manager->SetGpuParticleFactory(renderer->CreateGpuParticleFactory());
        playback.manager=manager;
        playback.effect=E::Effect::Create(manager,source.data(),static_cast<int>(source.size()));
        if (!playback.effect) return false;
        playback.lifetime=playback.effect->CalculateTerm().TermMax;
        if (usesGpuParticles(playback.effect->GetRoot())) {
            auto particles=renderer->CreateGpuParticleSystem();
            if (!particles) return false;
            manager->SetGpuParticleSystem(particles);
        }
        return true;
    }
    void render(const Frame& frame) {
        ComPtr<ID3D11Device> current;
        frame.target->GetDevice(&current);
        ComPtr<ID3D11DeviceContext> immediate;
        current->GetImmediateContext(&immediate);
        ComPtr<ID3D11RenderTargetView> color;
        ComPtr<ID3D11DepthStencilView> depth;
        immediate->OMGetRenderTargets(1,&color,&depth);
        if (!color) return;
        D3D11_VIEWPORT viewport{};
        UINT count=1;
        immediate->RSGetViewports(&count,&viewport);
        if (current.Get()!=device.Get() && !selectDevice(current.Get())) { failed=true; return; }
        CameraState state(context1.Get(),isolated.Get());
        if (!playback.effect && !initialize()) { failed=true; return; }
        context->RSSetViewports(1,&viewport);
        context->OMSetRenderTargets(1,color.GetAddressOf(),depth.Get());
        background->target=color.Get();
        renderer->SetCameraMatrix(frame.view);
        renderer->SetProjectionMatrix(frame.projection);
        renderer->SetTime(frame.controls[0]/30.f);
        E::Matrix44 relative, inverse;
        E::Matrix44::Inverse(inverse,frame.base);
        E::Matrix44::Mul(relative,frame.center,inverse);
        playback.update(frame.controls.data(),affine(relative),affine(frame.base));
        E::Manager::DrawParameter draw;
        E::Matrix44::Mul(draw.ViewProjectionMatrix, frame.view, frame.projection);
        draw.CameraPosition=renderer->GetCameraPosition();
        draw.CameraFrontDirection=renderer->GetCameraFrontDirection();
        if (renderer->BeginRendering()) {
            playback.manager->Draw(draw);
            renderer->EndRendering();
        }
        background->target=nullptr;
    }
};

std::mutex guard;
std::unordered_map<int,std::shared_ptr<Instance>> models;
struct Pending { int model; Frame frame; };
std::unordered_map<int,Pending> pending;
unsigned serial=0;
int nextId() {
    do { serial=(serial+1)&0x7fffffffu; } while (!serial || models.count(serial) || pending.count(serial));
    return static_cast<int>(serial);
}
void __stdcall drawEvent(int id) {
    std::shared_ptr<Instance> model;
    Frame frame;
    {
        std::lock_guard<std::mutex> lock(guard);
        auto it=pending.find(id);
        if (it==pending.end()) return;
        model=models.at(it->second.model);
        frame=std::move(it->second.frame); pending.erase(it);
    }
    if (!model->failed) {
        try { model->render(frame); }
        catch (...) { model->failed=true; }
    }
}
}
API int ava_efk_create(const uint8_t* data, int length) {
    if (!data || length<8) return 0;
    auto model=std::make_shared<Instance>();
    model->source.assign(data,data+length);
    std::lock_guard<std::mutex> lock(guard);
    auto id=nextId(); models.emplace(id,std::move(model)); return id;
}
API void ava_efk_resource(int id,const char16_t* path,const uint8_t* data,int length) {
    std::lock_guard<std::mutex> lock(guard);
    auto it=models.find(id);
    if (it!=models.end() && length>0) it->second->files->entries[key(path)].assign(data,data+length);
}
API int ava_efk_queue(int id, ID3D11Resource* target, const float* view, const float* projection,
                      const float* center, const float* base, const float* controls) {
    std::lock_guard<std::mutex> lock(guard);
    auto model=models.find(id);
    if (!target || model==models.end()) return 0;
    if (model->second->failed) return -1;
    auto token=nextId();
    auto& packet=pending[token];
    packet.model=id; packet.frame.target=target;
    memcpy(packet.frame.view.Values,view,64); memcpy(packet.frame.projection.Values,projection,64);
    memcpy(packet.frame.center.Values,center,64); memcpy(packet.frame.base.Values,base,64);
    std::copy_n(controls,packet.frame.controls.size(),packet.frame.controls.begin());
    return token;
}
API void* ava_efk_event() { return reinterpret_cast<void*>(&drawEvent); }
API void ava_efk_release(int id) {
    std::lock_guard<std::mutex> lock(guard);
    for (auto it=pending.begin(); it!=pending.end();) {
        if (it->second.model==id) it=pending.erase(it); else ++it;
    }
    models.erase(id);
}
