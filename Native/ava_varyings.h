#pragma once

struct Varying {
    std::string name;
    UINT index, slot, mask;
    D3D_REGISTER_COMPONENT_TYPE type;
    D3D_NAME system;
    int count() const {
        return ((mask & 1) != 0) + ((mask & 2) != 0) + ((mask & 4) != 0) + ((mask & 8) != 0);
    }
    std::string semantic() const { return name + (index ? std::to_string(index) : ""); }
    std::string scalar() const {
        return type == D3D_REGISTER_COMPONENT_UINT32 ? "uint" : type == D3D_REGISTER_COMPONENT_SINT32 ? "int" : "float";
    }
    bool matches(const Varying& other) const { return index == other.index && !_stricmp(name.c_str(), other.name.c_str()); }
};

static std::vector<Varying> varyingSignature(ID3DBlob* bytecode,bool inputs) {
    ComPtr<ID3D11ShaderReflection> reflection;
    std::vector<Varying> fields;
    if(FAILED(D3DReflect(bytecode->GetBufferPointer(),bytecode->GetBufferSize(),IID_PPV_ARGS(&reflection))))return fields;
    D3D11_SHADER_DESC shader{};reflection->GetDesc(&shader);
    UINT count=inputs?shader.InputParameters:shader.OutputParameters;
    for(UINT i=0;i<count;i++){
        D3D11_SIGNATURE_PARAMETER_DESC d{};
        HRESULT hr=inputs?reflection->GetInputParameterDesc(i,&d):reflection->GetOutputParameterDesc(i,&d);
        if(SUCCEEDED(hr))fields.push_back({d.SemanticName,d.SemanticIndex,d.Register,d.Mask,d.ComponentType,d.SystemValueType});
    }
    std::stable_sort(fields.begin(),fields.end(),[](const Varying& a,const Varying& b){return a.slot<b.slot || (a.slot==b.slot && a.mask<b.mask);});
    return fields;
}

// D3D11 interpolators connect by register and component. Independently compiled legacy entry
// points can request the same semantic at different registers. Repack only those stage pairs.
static bool linkVaryings(Program& program,char* error,int capacity) {
    auto vs=varyingSignature(program.vertex.code.Get(),false);
    auto ps=varyingSignature(program.pixel.code.Get(),true);
    std::vector<Varying> outputs;
    for(auto& dst:ps){
        if(dst.system==D3D_NAME_IS_FRONT_FACE || dst.system==D3D_NAME_PRIMITIVE_ID || dst.system==D3D_NAME_SAMPLE_INDEX || dst.system==D3D_NAME_COVERAGE)continue;
        outputs.push_back(dst);
    }
    for(auto& field:vs){
        if(field.system!=D3D_NAME_POSITION && field.system!=D3D_NAME_CLIP_DISTANCE && field.system!=D3D_NAME_CULL_DISTANCE)continue;
        if(std::none_of(outputs.begin(),outputs.end(),[&](const Varying& f){return f.matches(field);}))outputs.push_back(field);
    }
    std::ostringstream hlsl;
    auto declare=[&](const char* name,const std::vector<Varying>& fields){
        hlsl<<"struct "<<name<<" {\n";
        for(size_t i=0;i<fields.size();i++){
            const auto& f=fields[i];if(f.type!=D3D_REGISTER_COMPONENT_FLOAT32)hlsl<<"nointerpolation ";
            hlsl<<f.scalar();if(f.count()>1)hlsl<<f.count();
            hlsl<<" v"<<i<<":"<<f.semantic()<<";\n";
        }
        hlsl<<"};\n";
    };
    hlsl<<"cbuffer SurfaceRaster : register(b0) { float2 PixelOffset; float ReverseDepth; float Padding; };\n";
    declare("Input",vs);declare("Output",outputs);
    hlsl<<"[maxvertexcount(3)] void Link(triangle Input v[3],inout TriangleStream<Output> s){"
           "[unroll] for(int i=0;i<3;i++){Output o=(Output)0;\n";
    for(size_t j=0;j<outputs.size();j++){
        const auto& dst=outputs[j];auto found=std::find_if(vs.begin(),vs.end(),[&](const Varying& f){return f.matches(dst);});
        if(found==vs.end())continue;
        std::string field="v[i].v"+std::to_string(found-vs.begin());
        if(found->count()==dst.count())hlsl<<"o.v"<<j<<"="<<field<<";\n";
        else {
            hlsl<<"o.v"<<j<<"="<<dst.scalar();if(dst.count()>1)hlsl<<dst.count();hlsl<<"(";
            for(int n=0;n<dst.count();n++){
                if(n)hlsl<<",";
                if(n<found->count()){hlsl<<field;if(found->count()>1)hlsl<<"."<<"xyzw"[n];}
                else hlsl<<(n==3?1:0);
            }
            hlsl<<");\n";
        }
        if(dst.system==D3D_NAME_POSITION){
            hlsl<<"o.v"<<j<<".y=-o.v"<<j<<".y; o.v"<<j<<".xy+=PixelOffset*o.v"<<j<<".w;\n";
            hlsl<<"if(ReverseDepth!=0)o.v"<<j<<".z=o.v"<<j<<".w-o.v"<<j<<".z;\n";
        }
    }
    hlsl<<"s.Append(o);}s.RestartStrip();}";
    const auto code=hlsl.str();ComPtr<ID3DBlob> messages;
    HRESULT hr=D3DCompile(code.data(),code.size(),"stage interface",nullptr,nullptr,"Link","gs_5_0",0,0,&program.link,&messages);
    if(FAILED(hr))snprintf(error,capacity,"%s",messages?static_cast<const char*>(messages->GetBufferPointer()):"Stage interface compilation failed");
    return SUCCEEDED(hr);
}
