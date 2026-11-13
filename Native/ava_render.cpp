#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <unordered_set>
#ifdef AVA_UNITY_DEVICE_EVENTS
#include "IUnityGraphics.h"
#endif
#include <vector>
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)

static std::mutex errorMutex;
static std::string lastError;
static bool fail(const char* stage,HRESULT hr)
{
    char message[160];snprintf(message,sizeof(message),"%s (0x%08lX)",stage,static_cast<unsigned long>(hr));
    std::lock_guard<std::mutex> lock(errorMutex);lastError=message;return false;
}
API bool ava_fx_error(char* output,int size)
{
    std::lock_guard<std::mutex> lock(errorMutex);
    if(lastError.empty())return false;
    snprintf(output,size,"%s",lastError.c_str());lastError.clear();return true;
}
static ComPtr<ID3D11ShaderResourceView> resourceView(ID3D11Device* device,ID3D11Resource* texture)
{
    ComPtr<ID3D11ShaderResourceView> srv;
    if(SUCCEEDED(device->CreateShaderResourceView(texture,nullptr,&srv)))return srv;
    ComPtr<ID3D11Texture2D> t;
    if(FAILED(texture->QueryInterface(IID_PPV_ARGS(&t)))){
        ComPtr<ID3D11Texture3D> volume;if(FAILED(texture->QueryInterface(IID_PPV_ARGS(&volume))))return srv;
        D3D11_TEXTURE3D_DESC desc{};volume->GetDesc(&desc);D3D11_SHADER_RESOURCE_VIEW_DESC view{};
        view.Format=desc.Format==DXGI_FORMAT_R8G8B8A8_TYPELESS?DXGI_FORMAT_R8G8B8A8_UNORM:desc.Format;
        view.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE3D;view.Texture3D.MipLevels=desc.MipLevels;
        HRESULT hr=device->CreateShaderResourceView(texture,&view,&srv);if(FAILED(hr))fail("CreateShaderResourceView3D",hr);return srv;
    }
    D3D11_TEXTURE2D_DESC desc{};t->GetDesc(&desc);D3D11_SHADER_RESOURCE_VIEW_DESC view{};
    switch(desc.Format) {
        case DXGI_FORMAT_R8G8B8A8_TYPELESS:view.Format=DXGI_FORMAT_R8G8B8A8_UNORM;break;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS:view.Format=DXGI_FORMAT_B8G8R8A8_UNORM;break;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS:view.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;break;
        case DXGI_FORMAT_R32_TYPELESS:view.Format=DXGI_FORMAT_R32_FLOAT;break;
        case DXGI_FORMAT_R16_TYPELESS:view.Format=DXGI_FORMAT_R16_FLOAT;break;
        case DXGI_FORMAT_R16G16_TYPELESS:view.Format=DXGI_FORMAT_R16G16_FLOAT;break;
        case DXGI_FORMAT_R32G32_TYPELESS:view.Format=DXGI_FORMAT_R32G32_FLOAT;break;
        case DXGI_FORMAT_R32G32B32A32_TYPELESS:view.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;break;
        default:view.Format=desc.Format;
    }
    if(desc.MiscFlags&D3D11_RESOURCE_MISC_TEXTURECUBE){view.ViewDimension=D3D11_SRV_DIMENSION_TEXTURECUBE;view.TextureCube.MipLevels=desc.MipLevels;}
    else if(desc.SampleDesc.Count>1)view.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2DMS;
    else{view.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;view.Texture2D.MipLevels=desc.MipLevels;}
    HRESULT hr=device->CreateShaderResourceView(texture,&view,&srv);
    if(FAILED(hr))fail("CreateShaderResourceView",hr);return srv;
}

struct Variable { UINT offset,size; std::string semantic; std::vector<BYTE> initial; bool columnMajor=false; };
struct Constants { UINT slot,size; std::vector<Variable> variables; };
struct ResourceSlot { UINT slot,index; };
struct Stage { ComPtr<ID3DBlob> code; std::vector<Constants> buffers;std::vector<ResourceSlot> textures,samplers; };
struct Program {
    std::atomic<unsigned> refs{1};
    Stage vertex,pixel;
    int blend,depthWrite,cull;
    int raster[18]={5,6,1,2,6,1,1,7,15,3,0,255,255,0,8,1,1,1};
    std::vector<std::array<int,6>> samplers;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3DBlob> link;
    ComPtr<ID3D11GeometryShader> gs;
    ComPtr<ID3D11InputLayout> layout;
};
#include "ava_varyings.h"
static std::mutex programMutex;
static std::unordered_set<Program*> programs;
API void ava_fx_release(Program* program){
    if(program && --program->refs==0){
        std::lock_guard<std::mutex> lock(programMutex);programs.erase(program);delete program;
    }
}

static bool compile(Stage& stage,const char* source,int size,const char* entry,const char* profile,
    const std::unordered_map<std::string,std::string>& names,char* error,int capacity)
{
    ComPtr<ID3DBlob> messages;
    const D3D_SHADER_MACRO stageDefines[]={{"AVA_VERTEX_STAGE","1"},{nullptr,nullptr}};
    HRESULT hr=D3DCompile(source,size,"effect",profile[0]=='v'?stageDefines:nullptr,nullptr,entry,profile,
        D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY|D3DCOMPILE_PACK_MATRIX_ROW_MAJOR,0,&stage.code,&messages);
    if(FAILED(hr)) {
        snprintf(error,capacity,"%s",messages ? static_cast<const char*>(messages->GetBufferPointer()) : "Shader compilation failed");
        return false;
    }
    ComPtr<ID3D11ShaderReflection> info;
    if(FAILED(D3DReflect(stage.code->GetBufferPointer(),stage.code->GetBufferSize(),IID_PPV_ARGS(&info)))) return false;
    D3D11_SHADER_DESC shader{};info->GetDesc(&shader);
    for(UINT i=0;i<shader.BoundResources;i++){
        D3D11_SHADER_INPUT_BIND_DESC bind{};if(FAILED(info->GetResourceBindingDesc(i,&bind)))continue;
        if(bind.Type!=D3D_SIT_TEXTURE && bind.Type!=D3D_SIT_SAMPLER)continue;
        auto& slots=bind.Type==D3D_SIT_TEXTURE?stage.textures:stage.samplers;
        std::string prefix=bind.Type==D3D_SIT_TEXTURE?"AVATEXTURE":"AVASAMPLER";
        auto found=names.find(bind.Name);UINT index=bind.BindPoint;
        if(found!=names.end() && found->second.rfind(prefix,0)==0)
            index=static_cast<UINT>(std::stoul(found->second.substr(prefix.size())));
        for(UINT n=0;n<bind.BindCount;n++)slots.push_back({bind.BindPoint+n,index+n});
    }
    for(UINT i=0;i<shader.ConstantBuffers;i++){
        auto buffer=info->GetConstantBufferByIndex(i); D3D11_SHADER_BUFFER_DESC desc{};
        D3D11_SHADER_INPUT_BIND_DESC bind{};
        if(FAILED(buffer->GetDesc(&desc)) || FAILED(info->GetResourceBindingDescByName(desc.Name,&bind)))continue;
        Constants constants{bind.BindPoint,desc.Size};
        for(UINT j=0;j<desc.Variables;j++){
            D3D11_SHADER_VARIABLE_DESC v{};buffer->GetVariableByIndex(j)->GetDesc(&v);
            auto found=names.find(v.Name);
            Variable item{v.StartOffset,v.Size,found!=names.end()?found->second:v.Name};
            D3D11_SHADER_TYPE_DESC type{};
            if(SUCCEEDED(buffer->GetVariableByIndex(j)->GetType()->GetDesc(&type)))
                item.columnMajor=type.Class==D3D_SVC_MATRIX_COLUMNS && type.Rows==4 && type.Columns==4;
            if(v.DefaultValue) {auto p=static_cast<const BYTE*>(v.DefaultValue); item.initial.assign(p,p+v.Size);}
            constants.variables.push_back(std::move(item));
        }
        stage.buffers.push_back(std::move(constants));
    }
    return true;
}

API void* ava_fx_preprocess(const char* source,int size,int* outputSize,char* error,int capacity)
{
    ComPtr<ID3DBlob> output,messages;
    D3D_SHADER_MACRO macros[]={{"MME_MIPMAP","1"},{nullptr,nullptr}};
    if(FAILED(D3DPreprocess(source,size,"effect",macros,nullptr,&output,&messages))){
        snprintf(error,capacity,"%s",messages?static_cast<const char*>(messages->GetBufferPointer()):"FX preprocessor failed");
        return nullptr;
    }
    if(output->GetBufferSize()>32*1024*1024){snprintf(error,capacity,"FX source is too large");return nullptr;}
    *outputSize=static_cast<int>(output->GetBufferSize());
    void* bytes=CoTaskMemAlloc(*outputSize);
    if(bytes)memcpy(bytes,output->GetBufferPointer(),*outputSize);return bytes;
}
API Program* ava_fx_compile(const char* source,int size,const char* vs,const char* ps,const char* bindings,
    int blend,int zwrite,int cull,char* error,int capacity)
{
    try {
        auto result=std::make_unique<Program>();result->blend=blend;result->depthWrite=zwrite;result->cull=cull;
        std::unordered_map<std::string,std::string> semantics;std::istringstream lines(bindings);std::string name,semantic;
        while(lines>>name>>semantic) semantics[name]=semantic;
        if(!compile(result->vertex,source,size,vs,"vs_5_0",semantics,error,capacity) ||
           !compile(result->pixel,source,size,ps,"ps_5_0",semantics,error,capacity) || !linkVaryings(*result,error,capacity))return nullptr;
        std::lock_guard<std::mutex> lock(programMutex);programs.insert(result.get());return result.release();
    }catch(...){snprintf(error,capacity,"Could not allocate effect");return nullptr;}
}

API void ava_fx_states(Program* p,const int* raster,const int* samplers,int count)
{
    if(!p || !raster || count<0 || count>1024)return;
    std::copy_n(raster,18,p->raster);p->samplers.resize(count);
    for(int i=0;i<count;i++)std::copy_n(samplers+i*6,6,p->samplers[i].begin());
}

struct Packet {
    Program* program=nullptr;bool fullscreen=false,reverseDepth=false;D3D11_CULL_MODE materialCull=D3D11_CULL_NONE;
    ComPtr<ID3D11Resource> target;std::vector<ComPtr<ID3D11Resource>> textures;
    std::vector<float> vertices,values;std::vector<int> indices;
    ~Packet(){ava_fx_release(program);}
};
static std::mutex packetMutex;
static std::atomic<uintptr_t> nextPacket{1};
static std::unordered_map<uintptr_t,std::unique_ptr<Packet>> packets;
static std::unique_ptr<Packet> takePacket(void* token){
    std::lock_guard<std::mutex> lock(packetMutex);auto item=packets.find(reinterpret_cast<uintptr_t>(token));
    if(item==packets.end())return {};
    auto result=std::move(item->second);packets.erase(item);return result;
}
#ifdef AVA_UNITY_DEVICE_EVENTS
void resetPanelDevices();
static IUnityGraphics* unityGraphics=nullptr;
static void UNITY_INTERFACE_API deviceEvent(UnityGfxDeviceEventType type){
    if(type!=kUnityGfxDeviceEventShutdown && type!=kUnityGfxDeviceEventBeforeReset)return;
    resetPanelDevices();
    {std::lock_guard<std::mutex> guard(packetMutex);packets.clear();}
    std::lock_guard<std::mutex> guard(programMutex);
    for(auto p:programs){p->layout.Reset();p->gs.Reset();p->ps.Reset();p->vs.Reset();p->device.Reset();}
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces){
    unityGraphics=interfaces->Get<IUnityGraphics>();if(unityGraphics)unityGraphics->RegisterDeviceEventCallback(deviceEvent);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload(){
    if(unityGraphics)unityGraphics->UnregisterDeviceEventCallback(deviceEvent);
    deviceEvent(kUnityGfxDeviceEventShutdown);unityGraphics=nullptr;
}
#endif
API void* ava_fx_packet(Program* program,ID3D11Resource* target,ID3D11Resource* const* textures,int textureCount,
    const float* vertices,int count,const int* indices,int indexCount,const float* values,int valueCount,int fullscreen)
{
    if(!program || !target || valueCount<472 || valueCount>472+16*1024 || textureCount<0 || textureCount>1024 || count<=0 || indexCount<=0) return nullptr;
    try {
        auto p=std::make_unique<Packet>();program->refs++;p->program=program;p->target=target;p->fullscreen=(fullscreen&1)!=0;p->reverseDepth=(fullscreen&8)!=0;
        int inherited=(fullscreen>>1)&3;p->materialCull=inherited==0?D3D11_CULL_NONE:static_cast<D3D11_CULL_MODE>(inherited);
        for(int i=0;i<textureCount;i++)p->textures.emplace_back(textures[i]);
        p->vertices.assign(vertices,vertices+size_t(count)*33);p->indices.assign(indices,indices+indexCount);
        p->values.assign(values,values+valueCount);
        auto token=nextPacket++;std::lock_guard<std::mutex> lock(packetMutex);packets.emplace(token,std::move(p));return reinterpret_cast<void*>(token);
    }catch(...){return nullptr;}
}
API void ava_fx_cancel(void* token){takePacket(token);}

API int ava_fx_texture_indices(Program* program,int* destination,int capacity)
{
    if(!program)return 0;
    std::vector<int> indices;
    for(const auto* stage:{&program->vertex,&program->pixel})
        for(const auto& binding:stage->textures)indices.push_back(static_cast<int>(binding.index));
    std::sort(indices.begin(),indices.end());
    indices.erase(std::unique(indices.begin(),indices.end()),indices.end());
    if(destination && capacity>=static_cast<int>(indices.size()))std::copy(indices.begin(),indices.end(),destination);
    return static_cast<int>(indices.size());
}

API void* ava_fx_disassemble(Program* p,int* length)
{
    if(!p)return nullptr;
    std::string text;
    for(auto stage:{&p->vertex,&p->pixel}){
        ComPtr<ID3DBlob> code;
        if(SUCCEEDED(D3DDisassemble(stage->code->GetBufferPointer(),stage->code->GetBufferSize(),0,nullptr,&code)))
            text.append(static_cast<const char*>(code->GetBufferPointer())).append("\r\n");
    }
    *length=static_cast<int>(text.size());void* result=CoTaskMemAlloc(*length);
    if(result)memcpy(result,text.data(),*length);return result;
}

static bool prepare(Program& program,ID3D11Device* dev)
{
    if(program.device.Get()==dev && program.layout)return true;
    program.device=dev;program.layout.Reset();program.vs.Reset();program.ps.Reset();program.gs.Reset();
    auto vs=program.vertex.code.Get(); auto ps=program.pixel.code.Get();
    if(FAILED(dev->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&program.vs)) ||
       FAILED(dev->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&program.ps)))return false;
    if(program.link && FAILED(dev->CreateGeometryShader(program.link->GetBufferPointer(),program.link->GetBufferSize(),nullptr,&program.gs)))return false;
    D3D11_INPUT_ELEMENT_DESC layout[]={
        {"POSITION",0,DXGI_FORMAT_R32G32B32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"NORMAL",0,DXGI_FORMAT_R32G32B32_FLOAT,0,12,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"TANGENT",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,24,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,40,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"TEXCOORD",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,56,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"TEXCOORD",1,DXGI_FORMAT_R32G32B32A32_FLOAT,0,72,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"TEXCOORD",2,DXGI_FORMAT_R32G32B32A32_FLOAT,0,88,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"TEXCOORD",3,DXGI_FORMAT_R32G32B32A32_FLOAT,0,104,D3D11_INPUT_PER_VERTEX_DATA,0},
        {"BINORMAL",0,DXGI_FORMAT_R32G32B32_FLOAT,0,120,D3D11_INPUT_PER_VERTEX_DATA,0}};
    HRESULT hr=dev->CreateInputLayout(layout,ARRAYSIZE(layout),vs->GetBufferPointer(),vs->GetBufferSize(),&program.layout);
    return SUCCEEDED(hr) || fail("CreateInputLayout",hr);
}
API bool ava_fx_validate(Program* program,char* error,int capacity){
    if(!program)return false;
    ComPtr<ID3D11Device> device;D3D_FEATURE_LEVEL level{};
    auto hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,nullptr);
    if(FAILED(hr)){snprintf(error,capacity,"CreateDevice (0x%08lX)",static_cast<unsigned long>(hr));return false;}
    Program test;test.vertex.code=program->vertex.code;test.pixel.code=program->pixel.code;test.link=program->link;
    if(prepare(test,device.Get()))return true;
    if(!ava_fx_error(error,capacity))snprintf(error,capacity,"Shader device validation failed");
    return false;
}
static ComPtr<ID3D11Buffer> buffer(ID3D11Device* device,UINT bind,UINT bytes,const void* data)
{
    D3D11_BUFFER_DESC desc{};desc.ByteWidth=bytes;desc.BindFlags=bind;desc.Usage=D3D11_USAGE_IMMUTABLE;
    D3D11_SUBRESOURCE_DATA init{};init.pSysMem=data;ComPtr<ID3D11Buffer> result;
    device->CreateBuffer(&desc,&init,&result);return result;
}
static void constants(ID3D11Device* device,ID3D11DeviceContext* ctx,const Stage& stage,const std::vector<float>& payload,bool vertex)
{
    static const std::unordered_map<std::string,std::pair<UINT,UINT>> locations={
        {"WORLD",{0,16}},{"VIEW",{16,16}},{"PROJECTION",{32,16}},{"WORLDVIEWPROJECTION",{48,16}},
        {"WORLDVIEW",{64,16}},{"VIEWPROJECTION",{80,16}},{"DIFFUSE",{96,4}},{"TIME",{100,1}},
        {"ELAPSEDTIME",{101,1}},{"VIEWPORTPIXELSIZE",{102,2}},{"CAMERAPOSITION",{104,4}},{"LIGHTDIRECTION",{108,4}},
        {"SYNCTIME",{112,1}},{"SYNCELAPSEDTIME",{113,1}},{"AMBIENT",{404,4}},{"EMISSIVE",{408,4}},
        {"SPECULAR",{412,4}},{"EDGECOLOR",{416,4}},{"GROUNDSHADOWCOLOR",{420,4}},{"LIGHTDIFFUSE",{424,4}},
        {"LIGHTSPECULAR",{424,4}},{"LIGHTAMBIENT",{404,4}},{"MULTIPLYINGTEXTURE",{428,4}},{"MULTIPLYINGSPHERETEXTURE",{428,4}},
        {"SPECULARPOWER",{432,1}},{"CAMERADIRECTION",{436,4}},{"LIGHTPOSITION",{440,4}},{"parthf",{444,1}},
        {"transp",{445,1}},{"use_texture",{446,1}},{"VertexCount",{447,1}},{"SubsetCount",{448,1}},
        {"MOUSEPOSITION",{449,2}},{"LEFTMOUSEDOWN",{452,4}},{"MIDDLEMOUSEDOWN",{456,4}},{"RIGHTMOUSEDOWN",{460,4}},
        {"spadd",{464,1}},{"use_spheremap",{465,1}},{"use_toon",{466,1}},{"opadd",{467,1}},{"TOONCOLOR",{468,4}}};
    static const auto matrices=[] {
        std::unordered_map<std::string,UINT> matrixLocations;UINT offset=116;
        for(const char* m:{"WORLD","VIEW","PROJECTION","WORLDVIEWPROJECTION","WORLDVIEW","VIEWPROJECTION"})
            for(const char* suffix:{"INVERSE","TRANSPOSE","INVERSETRANSPOSE"}) {matrixLocations[std::string(m)+suffix]=offset;offset+=16;}
        return matrixLocations;
    }();
    const float* values=payload.data();
    for(const auto& b:stage.buffers) {
        std::vector<BYTE> data(b.size,0);
        for(const auto& v:b.variables) {
            if(v.offset+v.size>b.size)continue;
            if(!v.initial.empty())memcpy(data.data()+v.offset,v.initial.data(),v.size);
            auto at=locations.find(v.semantic);
            if(at!=locations.end())memcpy(data.data()+v.offset,values+at->second.first,std::min(v.size,at->second.second*4));
            else if(v.semantic.rfind("AVATEXELS",0)==0){
                size_t offset=472+16*std::stoul(v.semantic.substr(9));
                if(offset<payload.size())memcpy(data.data()+v.offset,values+offset,std::min<size_t>(v.size,(payload.size()-offset)*4));
            }
            else if(v.semantic.rfind("AVACONTROL",0)==0){
                size_t offset=472+16*std::stoul(v.semantic.substr(10));
                if(offset+16<=payload.size())memcpy(data.data()+v.offset,values+offset,std::min(v.size,64u));
            }
            else if(auto mat=matrices.find(v.semantic);mat!=matrices.end())memcpy(data.data()+v.offset,values+mat->second,std::min(v.size,64u));
            else continue;
            if(v.columnMajor && v.size>=64){
                float matrix[16];memcpy(matrix,data.data()+v.offset,64);
                for(int row=0;row<4;row++)for(int col=row+1;col<4;col++)std::swap(matrix[row*4+col],matrix[col*4+row]);
                memcpy(data.data()+v.offset,matrix,64);
            }
        }
        auto cb=buffer(device,D3D11_BIND_CONSTANT_BUFFER,b.size,data.data());
        if(vertex)ctx->VSSetConstantBuffers(b.slot,1,cb.GetAddressOf());else ctx->PSSetConstantBuffers(b.slot,1,cb.GetAddressOf());
    }
}

static void __stdcall draw(int,void* raw)
{
    auto packet=takePacket(raw);if(!packet)return;
    try {
        auto& p=*packet;auto& prog=*p.program; ComPtr<ID3D11Device> dev;p.target->GetDevice(&dev);
        if(!dev || !prepare(prog,dev.Get()))return;
        // Record a deferred command list. ExecuteCommandList restores every immediate-context binding.
        ComPtr<ID3D11DeviceContext> immediate,ctx;dev->GetImmediateContext(&immediate);
        if(FAILED(dev->CreateDeferredContext(0,&ctx)))return;
        std::array<ID3D11RenderTargetView*,4> colors{};ComPtr<ID3D11DepthStencilView> depth;
        immediate->OMGetRenderTargets(4,colors.data(),&depth);
        if(!colors[0] && !depth){for(auto* rtv:colors)if(rtv)rtv->Release();return;}
        ctx->OMSetRenderTargets(4,colors.data(),depth.Get());
        for(auto* rtv:colors)if(rtv)rtv->Release();
        UINT count=1; D3D11_VIEWPORT viewport{};immediate->RSGetViewports(&count,&viewport);
        if(count==0 || viewport.Width<=0 || viewport.Height<=0)return;ctx->RSSetViewports(1,&viewport);
        if(p.fullscreen && p.vertices.size()==4*33){
            // Cover the outer multisample positions while extrapolating the same UV plane.
            for(size_t i=0;i<p.vertices.size();i+=33){
                p.vertices[i]*=1.f+2.f/viewport.Width;
                p.vertices[i+1]*=1.f+2.f/viewport.Height;
                p.vertices[i+14]=(1.f+p.vertices[i])*.5f;
                p.vertices[i+15]=(1.f-p.vertices[i+1])*.5f;
            }
        }
        float rasterValues[]={1.f/viewport.Width,1.f/viewport.Height,p.reverseDepth?1.f:0.f,0.f};
        auto rasterBuffer=buffer(dev.Get(),D3D11_BIND_CONSTANT_BUFFER,sizeof(rasterValues),rasterValues);
        if(!rasterBuffer)return;ctx->GSSetConstantBuffers(0,1,rasterBuffer.GetAddressOf());
        auto vb=buffer(dev.Get(),D3D11_BIND_VERTEX_BUFFER,static_cast<UINT>(p.vertices.size()*4),p.vertices.data());
        auto ib=buffer(dev.Get(),D3D11_BIND_INDEX_BUFFER,static_cast<UINT>(p.indices.size()*4),p.indices.data());
        if(!vb || !ib)return;
        UINT stride=33*sizeof(float),offset=0;ctx->IASetVertexBuffers(0,1,vb.GetAddressOf(),&stride,&offset);
        ctx->IASetIndexBuffer(ib.Get(),DXGI_FORMAT_R32_UINT,0);ctx->IASetInputLayout(prog.layout.Get());
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(prog.vs.Get(),nullptr,0);ctx->GSSetShader(prog.gs.Get(),nullptr,0);ctx->PSSetShader(prog.ps.Get(),nullptr,0);
        constants(dev.Get(),ctx.Get(),prog.vertex,p.values,true);constants(dev.Get(),ctx.Get(),prog.pixel,p.values,false);
        D3D11_RASTERIZER_DESC raster{};raster.FillMode=static_cast<D3D11_FILL_MODE>(prog.raster[9]);raster.CullMode=prog.cull?static_cast<D3D11_CULL_MODE>(prog.cull):p.materialCull;
        raster.FrontCounterClockwise=TRUE;raster.DepthClipEnable=TRUE;
        ComPtr<ID3D11RasterizerState> rs; dev->CreateRasterizerState(&raster,&rs);ctx->RSSetState(rs.Get());
        D3D11_DEPTH_STENCIL_DESC z{};z.DepthEnable=!p.fullscreen && prog.raster[6];z.DepthWriteMask=prog.depthWrite?D3D11_DEPTH_WRITE_MASK_ALL:D3D11_DEPTH_WRITE_MASK_ZERO;
        z.DepthFunc=static_cast<D3D11_COMPARISON_FUNC>(prog.raster[7]);
        z.StencilEnable=prog.raster[10];z.StencilReadMask=static_cast<UINT8>(prog.raster[11]);z.StencilWriteMask=static_cast<UINT8>(prog.raster[12]);
        z.FrontFace.StencilFunc=static_cast<D3D11_COMPARISON_FUNC>(prog.raster[14]);
        z.FrontFace.StencilPassOp=static_cast<D3D11_STENCIL_OP>(prog.raster[15]);z.FrontFace.StencilFailOp=static_cast<D3D11_STENCIL_OP>(prog.raster[16]);
        z.FrontFace.StencilDepthFailOp=static_cast<D3D11_STENCIL_OP>(prog.raster[17]);z.BackFace=z.FrontFace;
        ComPtr<ID3D11DepthStencilState> ds; dev->CreateDepthStencilState(&z,&ds);ctx->OMSetDepthStencilState(ds.Get(),prog.raster[13]);
        D3D11_BLEND_DESC blend{};auto& b=blend.RenderTarget[0];b.RenderTargetWriteMask=static_cast<UINT8>(prog.raster[8]);b.BlendEnable=prog.blend;
        b.SrcBlend=static_cast<D3D11_BLEND>(prog.raster[0]);b.DestBlend=static_cast<D3D11_BLEND>(prog.raster[1]);b.BlendOp=static_cast<D3D11_BLEND_OP>(prog.raster[2]);
        b.SrcBlendAlpha=static_cast<D3D11_BLEND>(prog.raster[3]);b.DestBlendAlpha=static_cast<D3D11_BLEND>(prog.raster[4]);b.BlendOpAlpha=static_cast<D3D11_BLEND_OP>(prog.raster[5]);
        ComPtr<ID3D11BlendState> bs;dev->CreateBlendState(&blend,&bs);ctx->OMSetBlendState(bs.Get(),nullptr,~0u);
        auto resources=[&](const Stage& stage,bool vertex){
            for(const auto& item:stage.textures){
                if(item.index>=p.textures.size() || !p.textures[item.index])continue;
                auto srv=resourceView(dev.Get(),p.textures[item.index].Get());
                if(vertex)ctx->VSSetShaderResources(item.slot,1,srv.GetAddressOf());else ctx->PSSetShaderResources(item.slot,1,srv.GetAddressOf());
            }
            for(const auto& item:stage.samplers){
                const std::array<int,6> defaults={21,1,1,1,1,0};
                const auto& s=item.index<prog.samplers.size()?prog.samplers[item.index]:defaults;
                D3D11_SAMPLER_DESC sample{};sample.Filter=static_cast<D3D11_FILTER>(s[0]);sample.MaxLOD=D3D11_FLOAT32_MAX;
                sample.AddressU=static_cast<D3D11_TEXTURE_ADDRESS_MODE>(s[1]);sample.AddressV=static_cast<D3D11_TEXTURE_ADDRESS_MODE>(s[2]);
                sample.AddressW=static_cast<D3D11_TEXTURE_ADDRESS_MODE>(s[3]);sample.MaxAnisotropy=s[4];memcpy(&sample.MipLODBias,&s[5],4);
                ComPtr<ID3D11SamplerState> sampler;dev->CreateSamplerState(&sample,&sampler);
                if(vertex)ctx->VSSetSamplers(item.slot,1,sampler.GetAddressOf());else ctx->PSSetSamplers(item.slot,1,sampler.GetAddressOf());
            }
        };
        resources(prog.vertex,true);resources(prog.pixel,false);
        ctx->DrawIndexed(static_cast<UINT>(p.indices.size()),0,0);
        ComPtr<ID3D11CommandList> list;if(SUCCEEDED(ctx->FinishCommandList(FALSE,&list)))immediate->ExecuteCommandList(list.Get(),TRUE);
    }catch(...){}
}
API void* ava_fx_event(){return reinterpret_cast<void*>(&draw);}
