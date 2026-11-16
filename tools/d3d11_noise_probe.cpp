// Offline D3D11 execution probe for the draft Phira-Pro mask kernels.
// It does not use Unity and cannot validate ShaderLab, depth or final visuals.
#include <windows.h>
#include <d3d11.h>
#include <d3d11shader.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
static const GUID ShaderReflectionIID = {0x8d536ca1, 0x0cca, 0x4956, {0xa8, 0x37, 0x78, 0x69, 0x63, 0x75, 0x55, 0x84}};
static const GUID DxgiDeviceIID = {0x54ec77fa, 0x1377, 0x44e6, {0x8c, 0x32, 0x88, 0xfd, 0x5f, 0x44, 0xc8, 0x4c}};

static void check(HRESULT hr, const char *what) {
    if (FAILED(hr)) { std::ostringstream s; s << what << " failed: 0x" << std::hex << (uint32_t)hr; throw std::runtime_error(s.str()); }
}
static std::vector<uint8_t> read(const std::string &path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("cannot open " + path);
    return std::vector<uint8_t>(std::istreambuf_iterator<char>(f), {});
}

struct Area { float x,y,hx,hy,angle,yscale,opacity; uint32_t flags; };
struct Touch { float x,y,scale,padding; };
static_assert(sizeof(Area) == 32, "Area layout");

struct Kernel {
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3D11ShaderReflection> reflect;
    ComPtr<ID3D11Buffer> constants;
    std::vector<uint8_t> bytes;
    UINT cbSlot = 0;
    Kernel(ID3D11Device *dev, const std::vector<uint8_t> &source, const char *entry) {
        ComPtr<ID3DBlob> binary, errors;
        HRESULT hr = D3DCompile(source.data(), source.size(), "NoiseFieldCoverage.compute", nullptr, nullptr,
                                entry, "cs_5_0", D3DCOMPILE_ENABLE_STRICTNESS, 0, &binary, &errors);
        if (FAILED(hr)) {
            std::string message = errors ? std::string((const char *)errors->GetBufferPointer(), errors->GetBufferSize()) : "";
            throw std::runtime_error(std::string("compile ") + entry + ": " + message);
        }
        check(dev->CreateComputeShader(binary->GetBufferPointer(), binary->GetBufferSize(), nullptr, &shader), "CreateComputeShader");
        check(D3DReflect(binary->GetBufferPointer(), binary->GetBufferSize(), ShaderReflectionIID, (void **)reflect.GetAddressOf()), "D3DReflect");
        auto *cb = reflect->GetConstantBufferByName("$Globals");
        D3D11_SHADER_BUFFER_DESC cbDesc{};
        check(cb->GetDesc(&cbDesc), "constant buffer reflection");
        bytes.resize((cbDesc.Size + 15u) & ~15u, 0);
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = (UINT)bytes.size(); desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        check(dev->CreateBuffer(&desc, nullptr, &constants), "CreateBuffer constants");
        D3D11_SHADER_INPUT_BIND_DESC binding{};
        check(reflect->GetResourceBindingDescByName("$Globals", &binding), "constant buffer binding");
        cbSlot = binding.BindPoint;
    }
    void set(const char *name, const void *value, size_t size) {
        auto *v = reflect->GetConstantBufferByName("$Globals")->GetVariableByName(name);
        D3D11_SHADER_VARIABLE_DESC desc{};
        check(v->GetDesc(&desc), name);
        if (desc.StartOffset + size > bytes.size() || size > desc.Size) throw std::runtime_error(std::string("constant size ") + name);
        std::memcpy(bytes.data() + desc.StartOffset, value, size);
    }
    UINT binding(const char *name) {
        D3D11_SHADER_INPUT_BIND_DESC desc{};
        check(reflect->GetResourceBindingDescByName(name, &desc), name);
        return desc.BindPoint;
    }
    void dispatch(ID3D11DeviceContext *ctx, UINT width, UINT height) {
        ctx->UpdateSubresource(constants.Get(), 0, nullptr, bytes.data(), 0, 0);
        ID3D11Buffer *cb = constants.Get();
        ctx->CSSetConstantBuffers(cbSlot, 1, &cb);
        ctx->CSSetShader(shader.Get(), nullptr, 0);
        ctx->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
        ID3D11UnorderedAccessView *nullUav[8]{};
        ID3D11ShaderResourceView *nullSrv[8]{};
        ctx->CSSetUnorderedAccessViews(0, 8, nullUav, nullptr);
        ctx->CSSetShaderResources(0, 8, nullSrv);
    }
};

struct Texture {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11UnorderedAccessView> uav;
    UINT channels;
    bool normalized;
    Texture(ID3D11Device *dev, UINT width, UINT height, UINT channelCount=4, bool unorm=false)
        : channels(channelCount), normalized(unorm) {
        D3D11_TEXTURE2D_DESC d{};
        d.Width=width; d.Height=height; d.MipLevels=1; d.ArraySize=1;
        d.Format=unorm ? DXGI_FORMAT_R8G8B8A8_UNORM :
            (channelCount==1 ? DXGI_FORMAT_R32_UINT : DXGI_FORMAT_R32G32B32A32_UINT);
        d.SampleDesc.Count=1;
        d.Usage=D3D11_USAGE_DEFAULT; d.BindFlags=D3D11_BIND_UNORDERED_ACCESS;
        check(dev->CreateTexture2D(&d, nullptr, &texture), "CreateTexture2D");
        check(dev->CreateUnorderedAccessView(texture.Get(), nullptr, &uav), "CreateUnorderedAccessView");
    }
    std::vector<uint8_t> readBytes(ID3D11Device *dev, ID3D11DeviceContext *ctx) {
        D3D11_TEXTURE2D_DESC d{}; texture->GetDesc(&d);
        d.Usage=D3D11_USAGE_STAGING; d.BindFlags=0; d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        check(dev->CreateTexture2D(&d, nullptr, &staging), "CreateTexture2D staging");
        ctx->CopyResource(staging.Get(), texture.Get());
        D3D11_MAPPED_SUBRESOURCE m{};
        check(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m), "Map staging");
        std::vector<uint8_t> output((size_t)d.Width*d.Height*channels);
        for (UINT y=0; y<d.Height; y++) {
            const auto *raw = (const uint8_t *)m.pData + (size_t)y*m.RowPitch;
            if (normalized) {
                std::memcpy(output.data()+(size_t)y*d.Width*channels,raw,(size_t)d.Width*channels);
            } else {
                const auto *row = (const uint32_t *)raw;
                for (UINT x=0; x<d.Width*channels; x++) {
                    if (row[x] > 255) { ctx->Unmap(staging.Get(), 0); throw std::runtime_error("mask channel exceeds R8"); }
                    output[(size_t)y*d.Width*channels+x] = (uint8_t)row[x];
                }
            }
        }
        ctx->Unmap(staging.Get(), 0);
        return output;
    }
};

struct ReadTexture {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    ReadTexture(ID3D11Device *dev, UINT width, UINT height, const std::vector<uint8_t> &bytes) {
        if (bytes.size()!=(size_t)width*height*4) throw std::runtime_error("RGBA texture data length mismatch");
        D3D11_TEXTURE2D_DESC d{};
        d.Width=width; d.Height=height; d.MipLevels=1; d.ArraySize=1; d.SampleDesc.Count=1;
        d.Format=DXGI_FORMAT_R8G8B8A8_UNORM; d.Usage=D3D11_USAGE_IMMUTABLE; d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{}; data.pSysMem=bytes.data(); data.SysMemPitch=width*4;
        check(dev->CreateTexture2D(&d,&data,&texture), "CreateTexture2D source");
        check(dev->CreateShaderResourceView(texture.Get(),nullptr,&srv), "CreateShaderResourceView texture");
    }
};

static void bindUav(ID3D11DeviceContext *ctx, Kernel &k, const char *name, Texture &t) {
    ID3D11UnorderedAccessView *view=t.uav.Get();
    ctx->CSSetUnorderedAccessViews(k.binding(name), 1, &view, nullptr);
}
static void bindSrv(ID3D11DeviceContext *ctx, Kernel &k, const char *name, ID3D11ShaderResourceView *view) {
    ctx->CSSetShaderResources(k.binding(name), 1, &view);
}
static void compare(const std::string &label, const std::vector<uint8_t> &a, const std::vector<uint8_t> &b) {
    if (a.size()!=b.size()) throw std::runtime_error(label+" length mismatch");
    size_t different=0, first=0;
    for (size_t i=0;i<a.size();i++) if (a[i]!=b[i]) { if (!different) first=i; different++; }
    std::cout << label << ": " << different << " differing bytes";
    if (different) { std::cout << " first=" << first << " actual=" << (int)a[first] << " expected=" << (int)b[first] << std::endl; throw std::runtime_error(label+" mismatch"); }
    std::cout << std::endl;
}

int main(int argc, char **argv) {
    try {
        if (argc!=7) throw std::runtime_error("usage: d3d11_noise_probe <compute source> <coverage fixtures> <color fixtures> <flipped noise RGBA> <flipped touch RGBA> <normal|faded>");
        bool faded=std::string(argv[6])=="faded";
        if (!faded && std::string(argv[6])!="normal") throw std::runtime_error("case must be normal or faded");
        auto source=read(argv[1]);
        ComPtr<ID3D11Device> dev; ComPtr<ID3D11DeviceContext> ctx;
        D3D_FEATURE_LEVEL level{};
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                               D3D11_SDK_VERSION, &dev, &level, &ctx), "D3D11CreateDevice");
        if (level < D3D_FEATURE_LEVEL_11_0) throw std::runtime_error("D3D11 feature level 11 is required");
        ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC adapterDesc{};
        check(dev->QueryInterface(DxgiDeviceIID, (void **)dxgi.GetAddressOf()), "Query IDXGIDevice");
        check(dxgi->GetAdapter(&adapter), "GetAdapter"); check(adapter->GetDesc(&adapterDesc), "GetAdapterDesc");
        char adapterName[512]{};
        WideCharToMultiByte(CP_UTF8,0,adapterDesc.Description,-1,adapterName,sizeof(adapterName),nullptr,nullptr);
        std::cout << "D3D11 device " << adapterName << ", feature level 0x" << std::hex << (unsigned)level << std::dec << std::endl;
        const Area normalAreas[] = {
            {-.2f,.05f,.45f,.27f,.23f,1,1,1}, {.3f,-.1f,.35f,.18f,0,1,1,3},
            {.05f,.1f,.28f,.22f,0,1,.35f,0}, {-.3f,-.12f,.24f,.20f,0,1,.65f,2},
            {.05f,-.05f,.21f,.13f,0,1,1,4}, {.15f,.08f,.17f,.14f,0,1,.4f,6}
        };
        const Area fadedAreas[] = {
            {0,0,.45f,.25f,-.31f,1,.37f,1}, {.15f,.05f,.32f,.17f,.15f,1,.67f,1},
            {.1f,0,.22f,.18f,0,1,1,3}, {.1f,0,.22f,.18f,0,1,1,3},
            {.1f,0,.22f,.18f,0,1,1,3}, {-.1f,.08f,.27f,.16f,0,1,.52f,4}
        };
        static_assert(sizeof(normalAreas)==sizeof(fadedAreas), "fixture area counts");
        const Area *areas=faded ? fadedAreas : normalAreas;
        const UINT areaCount=(UINT)(sizeof(normalAreas)/sizeof(normalAreas[0]));
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth=sizeof(normalAreas); bd.Usage=D3D11_USAGE_DEFAULT;
        bd.BindFlags=D3D11_BIND_SHADER_RESOURCE; bd.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; bd.StructureByteStride=sizeof(Area);
        D3D11_SUBRESOURCE_DATA data{}; data.pSysMem=areas;
        ComPtr<ID3D11Buffer> buffer; ComPtr<ID3D11ShaderResourceView> areaView;
        check(dev->CreateBuffer(&bd, &data, &buffer), "CreateBuffer areas");
        D3D11_SHADER_RESOURCE_VIEW_DESC sv{}; sv.Format=DXGI_FORMAT_UNKNOWN; sv.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;
        sv.Buffer.NumElements=areaCount;
        check(dev->CreateShaderResourceView(buffer.Get(), &sv, &areaView), "CreateShaderResourceView areas");
        ReadTexture noise(dev.Get(),256,256,read(argv[4]));
        ReadTexture touchImage(dev.Get(),44,44,read(argv[5]));
        Kernel build(dev.Get(), source, "BuildSources"), compose(dev.Get(), source, "ComposeSources");
        Kernel warp(dev.Get(), source, "WarpActive"), ring(dev.Get(), source, "DilateRing");
        Kernel exportMasks(dev.Get(), source, "ExportMasks"), warpColors(dev.Get(), source, "WarpColors");
        Kernel touch(dev.Get(), source, "BuildTouch");
        for (auto viewport : {std::pair<UINT,UINT>{256,144}, {255,143}}) {
            UINT dims[2]={std::max(1u,viewport.first/8)*2,std::max(1u,viewport.second/8)*2};
            float aspect=16.f/9.f;
            Texture src(dev.Get(),dims[0],dims[1]), aux(dev.Get(),dims[0],dims[1]);
            Texture comp(dev.Get(),dims[0],dims[1]), auxComp(dev.Get(),dims[0],dims[1]);
            build.set("_Dimensions",dims,sizeof(dims)); build.set("_AreaCount",&areaCount,sizeof(areaCount)); build.set("_Aspect",&aspect,sizeof(aspect));
            ID3D11ShaderResourceView *view=areaView.Get();
            ctx->CSSetShaderResources(build.binding("_Areas"),1,&view);
            bindUav(ctx.Get(),build,"_Sources",src); bindUav(ctx.Get(),build,"_AuxSources",aux);
            build.dispatch(ctx.Get(),dims[0],dims[1]);
            std::string prefix=std::string(argv[2])+"/"+(faded ? "faded-" : "")+std::to_string(viewport.first)+"x"+std::to_string(viewport.second);
            compare(prefix+" sources",src.readBytes(dev.Get(),ctx.Get()),read(prefix+"-sources.bin"));
            auto a=aux.readBytes(dev.Get(),ctx.Get());
            const char *names[]={"ready-normal","ready-inverse","disabled-green","ready-green"};
            std::vector<uint8_t> expected(a.size());
            for (int channel=0;channel<4;channel++) {
                auto input=read(prefix+"-"+names[channel]+".bin");
                if (input.size()*4!=expected.size()) throw std::runtime_error("aux fixture length mismatch");
                for (size_t i=0;i<input.size();i++) expected[i*4+channel]=input[i];
            }
            compare(prefix+" aux sources",a,expected);
            compose.set("_Dimensions",dims,sizeof(dims));
            bindUav(ctx.Get(),compose,"_Sources",src); bindUav(ctx.Get(),compose,"_AuxSources",aux);
            bindUav(ctx.Get(),compose,"_Compose",comp); bindUav(ctx.Get(),compose,"_AuxCompose",auxComp);
            compose.dispatch(ctx.Get(),dims[0],dims[1]);
            auto active=read(prefix+"-active-compose.bin"), base=read(prefix+"-base.bin");
            std::vector<uint8_t> expectedComp(base.size(),0);
            for (size_t i=0;i<active.size();i++) { expectedComp[i*4]=active[i]; expectedComp[i*4+3]=base[i*4+3]; }
            compare(prefix+" compose",comp.readBytes(dev.Get(),ctx.Get()),expectedComp);
            compare(prefix+" aux compose",auxComp.readBytes(dev.Get(),ctx.Get()),read(prefix+"-aux-compose.bin"));
            const float releaseScale=1.f-(.3f-.25f)/.1f;
            const std::vector<std::pair<std::string,std::vector<Touch>>> touchCases={
                {"half",{{.5f,.5f,.5f,0}}},
                {"two",{{.5f,.5f,1,0},{.55f,.5f,1,0}}},
                {"release",{{.5f,.5f,releaseScale,0},{.55f,.5f,1,0}}}
            };
            UINT touchDims[2]={44,44}; float screenAspect=(float)viewport.first/(float)viewport.second;
            for (const auto &item : touchCases) {
                D3D11_BUFFER_DESC td{}; td.ByteWidth=(UINT)(item.second.size()*sizeof(Touch)); td.Usage=D3D11_USAGE_IMMUTABLE;
                td.BindFlags=D3D11_BIND_SHADER_RESOURCE; td.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; td.StructureByteStride=sizeof(Touch);
                D3D11_SUBRESOURCE_DATA ti{}; ti.pSysMem=item.second.data();
                ComPtr<ID3D11Buffer> tb; ComPtr<ID3D11ShaderResourceView> tv;
                check(dev->CreateBuffer(&td,&ti,&tb), "CreateBuffer touches");
                D3D11_SHADER_RESOURCE_VIEW_DESC ts{}; ts.Format=DXGI_FORMAT_UNKNOWN; ts.ViewDimension=D3D11_SRV_DIMENSION_BUFFER; ts.Buffer.NumElements=(UINT)item.second.size();
                check(dev->CreateShaderResourceView(tb.Get(),&ts,&tv), "CreateShaderResourceView touches");
                UINT count=(UINT)item.second.size();
                touch.set("_Dimensions",dims,sizeof(dims)); touch.set("_TouchCount",&count,sizeof(count));
                touch.set("_ScreenAspect",&screenAspect,sizeof(screenAspect)); touch.set("_TouchTextureDimensions",touchDims,sizeof(touchDims));
                bindSrv(ctx.Get(),touch,"_Touches",tv.Get()); bindSrv(ctx.Get(),touch,"_TouchTexture",touchImage.srv.Get());
                bindUav(ctx.Get(),touch,"_AuxCompose",auxComp); touch.dispatch(ctx.Get(),dims[0],dims[1]);
                auto rgba=auxComp.readBytes(dev.Get(),ctx.Get()); std::vector<uint8_t> alpha(rgba.size()/4);
                for (size_t i=0;i<alpha.size();i++) alpha[i]=rgba[i*4+3];
                compare(prefix+" touch "+item.first,alpha,read(prefix+"-touch-"+item.first+".bin"));
            }
            // Clear touch alpha for the following no-touch auxiliary fixture.
            bindUav(ctx.Get(),compose,"_Sources",src); bindUav(ctx.Get(),compose,"_AuxSources",aux);
            bindUav(ctx.Get(),compose,"_Compose",comp); bindUav(ctx.Get(),compose,"_AuxCompose",auxComp);
            compose.dispatch(ctx.Get(),dims[0],dims[1]);
            Texture effect(dev.Get(),dims[0],dims[1]), ping(dev.Get(),dims[0],dims[1],1), pong(dev.Get(),dims[0],dims[1],1);
            Texture effectUnorm(dev.Get(),dims[0],dims[1],4,true), auxUnorm(dev.Get(),dims[0],dims[1],4,true);
            std::string colorPrefix=std::string(argv[3])+"/"+std::to_string(viewport.first)+"x"+std::to_string(viewport.second);
            std::unique_ptr<ReadTexture> rawColor;
            if (!faded) rawColor.reset(new ReadTexture(dev.Get(),dims[0],dims[1],read(colorPrefix+"-raw-active.bin")));
            Texture colorOut(dev.Get(),dims[0],dims[1],4,true);
            UINT noiseDims[2]={256,256};
            float total=0; for (int i=1;i<=6;i++) total+=std::pow((float)i,2.65f);
            for (float time : {0.f,1.f,9.f}) {
                std::string label=std::to_string((int)time);
                warp.set("_Dimensions",dims,sizeof(dims)); warp.set("_NoiseDimensions",noiseDims,sizeof(noiseDims)); warp.set("_TimeSeconds",&time,sizeof(time));
                bindSrv(ctx.Get(),warp,"_NoiseTexture",noise.srv.Get());
                bindUav(ctx.Get(),warp,"_Compose",comp); bindUav(ctx.Get(),warp,"_Effect",effect); bindUav(ctx.Get(),warp,"_RingOutput",ping);
                warp.dispatch(ctx.Get(),dims[0],dims[1]);
                Texture *input=&ping,*output=&pong;
                for (UINT pass=0;pass<6;pass++) {
                    float weight=std::pow((float)(6-pass),2.65f)/total;
                    if (weight<0.01f) break;
                    ring.set("_Dimensions",dims,sizeof(dims)); ring.set("_GlowWeight",&weight,sizeof(weight)); ring.set("_RingPass",&pass,sizeof(pass));
                    bindUav(ctx.Get(),ring,"_Effect",effect); bindUav(ctx.Get(),ring,"_RingInput",*input); bindUav(ctx.Get(),ring,"_RingOutput",*output);
                    ring.dispatch(ctx.Get(),dims[0],dims[1]); std::swap(input,output);
                }
                auto expectedEffect=read(prefix+"-effect-"+label+".bin");
                compare(prefix+" effect t="+label,effect.readBytes(dev.Get(),ctx.Get()),expectedEffect);
                exportMasks.set("_Dimensions",dims,sizeof(dims));
                bindUav(ctx.Get(),exportMasks,"_Effect",effect); bindUav(ctx.Get(),exportMasks,"_AuxCompose",auxComp);
                bindUav(ctx.Get(),exportMasks,"_EffectNormalized",effectUnorm); bindUav(ctx.Get(),exportMasks,"_AuxNormalized",auxUnorm);
                exportMasks.dispatch(ctx.Get(),dims[0],dims[1]);
                compare(prefix+" R8 effect t="+label,effectUnorm.readBytes(dev.Get(),ctx.Get()),expectedEffect);
                compare(prefix+" R8 auxiliary t="+label,auxUnorm.readBytes(dev.Get(),ctx.Get()),read(prefix+"-aux-compose.bin"));
                if (!faded) {
                    warpColors.set("_Dimensions",dims,sizeof(dims)); warpColors.set("_NoiseDimensions",noiseDims,sizeof(noiseDims)); warpColors.set("_TimeSeconds",&time,sizeof(time));
                    bindSrv(ctx.Get(),warpColors,"_NoiseTexture",noise.srv.Get()); bindSrv(ctx.Get(),warpColors,"_RawActiveColors",rawColor->srv.Get());
                    bindUav(ctx.Get(),warpColors,"_WarpedActiveColors",colorOut); warpColors.dispatch(ctx.Get(),dims[0],dims[1]);
                    compare(colorPrefix+" warp t="+label,colorOut.readBytes(dev.Get(),ctx.Get()),read(colorPrefix+"-active-"+label+".bin"));
                }
            }
        }
        std::cout << "D3D11 mask, R8 export and color warp fixtures passed; Unity and full visual output remain untested." << std::endl;
        if (std::getenv("PEV_BENCH_NOISE_GPU")) {
            // GPU time only: one six-area layer at 1600x900, without touch, palette,
            // final fragment or capture. This cannot establish game-frame performance.
            UINT dims[2]={400,224}, noiseDims[2]={256,256}; float aspect=16.f/9.f;
            Texture src(dev.Get(),dims[0],dims[1]), aux(dev.Get(),dims[0],dims[1]);
            Texture comp(dev.Get(),dims[0],dims[1]), auxComp(dev.Get(),dims[0],dims[1]);
            Texture effect(dev.Get(),dims[0],dims[1]), ping(dev.Get(),dims[0],dims[1],1), pong(dev.Get(),dims[0],dims[1],1);
            Texture effectOut(dev.Get(),dims[0],dims[1],4,true), auxOut(dev.Get(),dims[0],dims[1],4,true);
            D3D11_QUERY_DESC disjointDesc{}; disjointDesc.Query=D3D11_QUERY_TIMESTAMP_DISJOINT;
            D3D11_QUERY_DESC timestampDesc{}; timestampDesc.Query=D3D11_QUERY_TIMESTAMP;
            ComPtr<ID3D11Query> disjoint; check(dev->CreateQuery(&disjointDesc,&disjoint),"CreateQuery disjoint");
            constexpr int frames=120;
            std::vector<ComPtr<ID3D11Query>> starts(frames),ends(frames);
            for (int i=0;i<frames;i++) {
                check(dev->CreateQuery(&timestampDesc,&starts[i]),"CreateQuery start");
                check(dev->CreateQuery(&timestampDesc,&ends[i]),"CreateQuery end");
            }
            float total=0; for (int i=1;i<=6;i++) total+=std::pow((float)i,2.65f);
            ctx->Begin(disjoint.Get());
            for (int frame=0;frame<frames;frame++) {
                ctx->End(starts[frame].Get());
                float time=frame/60.f;
                build.set("_Dimensions",dims,sizeof(dims)); build.set("_AreaCount",&areaCount,sizeof(areaCount)); build.set("_Aspect",&aspect,sizeof(aspect));
                bindSrv(ctx.Get(),build,"_Areas",areaView.Get()); bindUav(ctx.Get(),build,"_Sources",src); bindUav(ctx.Get(),build,"_AuxSources",aux);
                build.dispatch(ctx.Get(),dims[0],dims[1]);
                compose.set("_Dimensions",dims,sizeof(dims)); bindUav(ctx.Get(),compose,"_Sources",src); bindUav(ctx.Get(),compose,"_AuxSources",aux);
                bindUav(ctx.Get(),compose,"_Compose",comp); bindUav(ctx.Get(),compose,"_AuxCompose",auxComp); compose.dispatch(ctx.Get(),dims[0],dims[1]);
                warp.set("_Dimensions",dims,sizeof(dims)); warp.set("_NoiseDimensions",noiseDims,sizeof(noiseDims)); warp.set("_TimeSeconds",&time,sizeof(time));
                bindSrv(ctx.Get(),warp,"_NoiseTexture",noise.srv.Get()); bindUav(ctx.Get(),warp,"_Compose",comp);
                bindUav(ctx.Get(),warp,"_Effect",effect); bindUav(ctx.Get(),warp,"_RingOutput",ping); warp.dispatch(ctx.Get(),dims[0],dims[1]);
                Texture *input=&ping,*output=&pong;
                for (UINT pass=0;pass<6;pass++) {
                    float weight=std::pow((float)(6-pass),2.65f)/total; if (weight<.01f) break;
                    ring.set("_Dimensions",dims,sizeof(dims)); ring.set("_GlowWeight",&weight,sizeof(weight)); ring.set("_RingPass",&pass,sizeof(pass));
                    bindUav(ctx.Get(),ring,"_Effect",effect); bindUav(ctx.Get(),ring,"_RingInput",*input); bindUav(ctx.Get(),ring,"_RingOutput",*output);
                    ring.dispatch(ctx.Get(),dims[0],dims[1]); std::swap(input,output);
                }
                exportMasks.set("_Dimensions",dims,sizeof(dims)); bindUav(ctx.Get(),exportMasks,"_Effect",effect);
                bindUav(ctx.Get(),exportMasks,"_AuxCompose",auxComp); bindUav(ctx.Get(),exportMasks,"_EffectNormalized",effectOut);
                bindUav(ctx.Get(),exportMasks,"_AuxNormalized",auxOut); exportMasks.dispatch(ctx.Get(),dims[0],dims[1]);
                ctx->End(ends[frame].Get());
            }
            ctx->End(disjoint.Get()); ctx->Flush();
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT timing{};
            auto wait=[&](ID3D11Query *query,void *value,UINT size) {
                ULONGLONG deadline=GetTickCount64()+15000;
                for (;;) {
                    HRESULT hr=ctx->GetData(query,value,size,D3D11_ASYNC_GETDATA_DONOTFLUSH);
                    if (hr==S_OK) return;
                    if (FAILED(hr)) check(hr,"GetData timestamp");
                    if (GetTickCount64()>deadline) throw std::runtime_error("GPU timestamp timeout");
                    Sleep(1);
                }
            };
            wait(disjoint.Get(),&timing,sizeof(timing));
            if (timing.Disjoint || !timing.Frequency) throw std::runtime_error("GPU timestamp clock was disjoint");
            std::vector<double> samples;
            for (int i=0;i<frames;i++) {
                UINT64 start=0,end=0; wait(starts[i].Get(),&start,sizeof(start)); wait(ends[i].Get(),&end,sizeof(end));
                if (i>=20) samples.push_back((double)(end-start)*1000.0/(double)timing.Frequency);
            }
            std::sort(samples.begin(),samples.end());
            double sum=0; for (double value:samples) sum+=value;
            std::cout << "GPU mask-only 1600x900, 6 areas, 100 measured frames: mean=" << sum/samples.size()
                      << " ms p95=" << samples[(samples.size()*95+99)/100-1] << " ms max=" << samples.back() << " ms" << std::endl;
        }
        return 0;
    } catch (const std::exception &e) { std::cerr << e.what() << std::endl; return 1; }
}
