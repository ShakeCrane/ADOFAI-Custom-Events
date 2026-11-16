// Cross-API diagnostic for the draft default-red fragment. Not a Unity test.
#define main pev_mask_probe_main
#include "d3d11_noise_probe.cpp"
#undef main

struct Stage {
    ComPtr<ID3DBlob> bytecode;
    ComPtr<ID3D11ShaderReflection> reflect;
    ComPtr<ID3D11Buffer> constants;
    std::vector<uint8_t> bytes;
    UINT cbSlot=0;
    Stage(ID3D11Device *dev, const std::vector<uint8_t> &source, const char *entry, const char *target) {
        ComPtr<ID3DBlob> errors;
        HRESULT hr=D3DCompile(source.data(),source.size(),"draft-frame.hlsl",nullptr,nullptr,entry,target,
                              D3DCOMPILE_ENABLE_STRICTNESS,0,&bytecode,&errors);
        if (FAILED(hr)) throw std::runtime_error(std::string("compile ")+entry+": "+
            (errors ? std::string((const char *)errors->GetBufferPointer(),errors->GetBufferSize()) : ""));
        check(D3DReflect(bytecode->GetBufferPointer(),bytecode->GetBufferSize(),ShaderReflectionIID,
                         (void **)reflect.GetAddressOf()),"D3DReflect stage");
        D3D11_SHADER_BUFFER_DESC d{}; check(reflect->GetConstantBufferByName("$Globals")->GetDesc(&d),"stage constants");
        bytes.resize((d.Size+15u)&~15u,0);
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth=(UINT)bytes.size(); bd.Usage=D3D11_USAGE_DEFAULT; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        check(dev->CreateBuffer(&bd,nullptr,&constants),"stage constant buffer");
        D3D11_SHADER_INPUT_BIND_DESC b{}; check(reflect->GetResourceBindingDescByName("$Globals",&b),"stage constant binding"); cbSlot=b.BindPoint;
    }
    void set(const char *name,const void *value,size_t size) {
        D3D11_SHADER_VARIABLE_DESC d{};
        auto *var=reflect->GetConstantBufferByName("$Globals")->GetVariableByName(name);
        if (FAILED(var->GetDesc(&d))) return; // specialization removes unused uniforms
        if (size>d.Size || d.StartOffset+size>bytes.size()) throw std::runtime_error(std::string("bad uniform ")+name);
        std::memcpy(bytes.data()+d.StartOffset,value,size);
    }
    void scalar(const char *name,float value) { set(name,&value,sizeof(value)); }
    void vector(const char *name,float x,float y,float z,float w) { float v[4]={x,y,z,w}; set(name,v,sizeof(v)); }
    void vector3(const char *name,float x,float y,float z) { float v[3]={x,y,z}; set(name,v,sizeof(v)); }
    bool slot(const char *name,UINT &result) {
        D3D11_SHADER_INPUT_BIND_DESC d{};
        if (FAILED(reflect->GetResourceBindingDescByName(name,&d))) return false;
        result=d.BindPoint; return true;
    }
    void upload(ID3D11DeviceContext *ctx,bool vertex) {
        ctx->UpdateSubresource(constants.Get(),0,nullptr,bytes.data(),0,0);
        ID3D11Buffer *buffer=constants.Get();
        if (vertex) ctx->VSSetConstantBuffers(cbSlot,1,&buffer); else ctx->PSSetConstantBuffers(cbSlot,1,&buffer);
    }
};

static void bindPixel(ID3D11Device *dev, ID3D11DeviceContext *ctx, Stage &stage, const char *name,
                      ID3D11ShaderResourceView *view, D3D11_FILTER filter, D3D11_TEXTURE_ADDRESS_MODE wrap) {
    UINT slot=0;
    if (stage.slot(name,slot)) ctx->PSSetShaderResources(slot,1,&view);
    std::string samplerName=std::string("sampler")+name;
    if (stage.slot(samplerName.c_str(),slot)) {
        D3D11_SAMPLER_DESC d{}; d.Filter=filter; d.AddressU=d.AddressV=d.AddressW=wrap;
        d.ComparisonFunc=D3D11_COMPARISON_NEVER; d.MaxLOD=D3D11_FLOAT32_MAX;
        ComPtr<ID3D11SamplerState> sampler;
        check(dev->CreateSamplerState(&d,&sampler),"CreateSamplerState");
        ID3D11SamplerState *value=sampler.Get(); ctx->PSSetSamplers(slot,1,&value);
    }
}

static void write(const std::string &path,const std::vector<uint8_t> &data) {
    std::ofstream f(path,std::ios::binary);
    if (!f.write((const char *)data.data(),(std::streamsize)data.size())) throw std::runtime_error("cannot write "+path);
}

int main(int argc,char **argv) {
    try {
        if (argc!=5) throw std::runtime_error("usage: d3d11_frame_probe <combined hlsl> <raw texture dir> <frame fixtures> <output dir>");
        auto source=read(argv[1]);
        ComPtr<ID3D11Device> dev; ComPtr<ID3D11DeviceContext> ctx; D3D_FEATURE_LEVEL level{};
        check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&dev,&level,&ctx),"D3D11CreateDevice");
        const bool debug=std::getenv("PEV_FRAME_DEBUG")!=nullptr;
        const char *requested=std::getenv("PEV_FRAME_CASE");
        std::string caseName=requested ? requested : "normal";
        if (caseName!="normal" && caseName!="shifted" && caseName!="disabled" && caseName!="ready" && caseName!="ready-full" && caseName!="green" && caseName!="hover" && caseName!="palette" && caseName!="nocapture")
            throw std::runtime_error("unknown frame case");
        Stage vs(dev.Get(),source,"pev_vertex","vs_5_0"), ps(dev.Get(),source,debug ? "pev_debug" : "pev_fragment","ps_5_0");
        if (std::getenv("PEV_FRAME_SIGNATURE")) {
            D3D11_SHADER_DESC vd{},pd{}; vs.reflect->GetDesc(&vd); ps.reflect->GetDesc(&pd);
            for (UINT i=0;i<vd.OutputParameters;i++) { D3D11_SIGNATURE_PARAMETER_DESC d{}; vs.reflect->GetOutputParameterDesc(i,&d); std::cout << "VS out " << d.SemanticName << d.SemanticIndex << " register=" << d.Register << std::endl; }
            for (UINT i=0;i<pd.InputParameters;i++) { D3D11_SIGNATURE_PARAMETER_DESC d{}; ps.reflect->GetInputParameterDesc(i,&d); std::cout << "PS in  " << d.SemanticName << d.SemanticIndex << " register=" << d.Register << std::endl; }
        }
        if (!debug) {
            D3D11_SHADER_DESC vd{},pd{}; vs.reflect->GetDesc(&vd); ps.reflect->GetDesc(&pd);
            for (UINT i=0;i<pd.InputParameters;i++) {
                D3D11_SIGNATURE_PARAMETER_DESC input{}; check(ps.reflect->GetInputParameterDesc(i,&input),"pixel signature");
                bool matched=false;
                for (UINT j=0;j<vd.OutputParameters;j++) {
                    D3D11_SIGNATURE_PARAMETER_DESC output{}; check(vs.reflect->GetOutputParameterDesc(j,&output),"vertex signature");
                    if (_stricmp(input.SemanticName,output.SemanticName)==0 && input.SemanticIndex==output.SemanticIndex) {
                        if (input.Register!=output.Register || (input.Mask & output.Mask)!=input.Mask)
                            throw std::runtime_error(std::string("stage linkage mismatch: ")+input.SemanticName+std::to_string(input.SemanticIndex));
                        matched=true; break;
                    }
                }
                if (!matched) throw std::runtime_error(std::string("missing vertex output: ")+input.SemanticName+std::to_string(input.SemanticIndex));
            }
            if (std::getenv("PEV_FRAME_SIGNATURE_ONLY")) { std::cout << "D3D11 stage signatures match" << std::endl; return 0; }
        }
        ComPtr<ID3D11VertexShader> vertex; ComPtr<ID3D11PixelShader> pixel;
        check(dev->CreateVertexShader(vs.bytecode->GetBufferPointer(),vs.bytecode->GetBufferSize(),nullptr,&vertex),"CreateVertexShader");
        check(dev->CreatePixelShader(ps.bytecode->GetBufferPointer(),ps.bytecode->GetBufferSize(),nullptr,&pixel),"CreatePixelShader");
        D3D11_INPUT_ELEMENT_DESC elems[]={{"POSITION",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},
                                          {"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,16,D3D11_INPUT_PER_VERTEX_DATA,0}};
        ComPtr<ID3D11InputLayout> layout;
        check(dev->CreateInputLayout(elems,2,vs.bytecode->GetBufferPointer(),vs.bytecode->GetBufferSize(),&layout),"CreateInputLayout");
        const float vertices[]={-1,-1,0,1, 1,1,1,1, -1,1,0,1, 1,1,1,1,
                                 1,-1,0,1, 1,1,1,1,  1,1,0,1, 1,1,1,1};
        D3D11_BUFFER_DESC vbDesc{}; vbDesc.ByteWidth=sizeof(vertices); vbDesc.Usage=D3D11_USAGE_IMMUTABLE; vbDesc.BindFlags=D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA vd{}; vd.pSysMem=vertices; ComPtr<ID3D11Buffer> vb;
        check(dev->CreateBuffer(&vbDesc,&vd,&vb),"CreateBuffer vertices");
        UINT stride=sizeof(float)*8,offset=0; ID3D11Buffer *vbp=vb.Get();
        ctx->IASetInputLayout(layout.Get()); ctx->IASetVertexBuffers(0,1,&vbp,&stride,&offset);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        ctx->VSSetShader(vertex.Get(),nullptr,0); ctx->PSSetShader(pixel.Get(),nullptr,0);
        D3D11_RASTERIZER_DESC rd{}; rd.FillMode=D3D11_FILL_SOLID; rd.CullMode=D3D11_CULL_NONE; rd.DepthClipEnable=TRUE;
        ComPtr<ID3D11RasterizerState> raster; check(dev->CreateRasterizerState(&rd,&raster),"CreateRasterizerState"); ctx->RSSetState(raster.Get());
        D3D11_DEPTH_STENCIL_DESC depthDesc{}; depthDesc.DepthEnable=FALSE; depthDesc.StencilEnable=FALSE;
        ComPtr<ID3D11DepthStencilState> depth;
        check(dev->CreateDepthStencilState(&depthDesc,&depth),"CreateDepthStencilState"); ctx->OMSetDepthStencilState(depth.Get(),0);
        D3D11_BLEND_DESC blendDesc{}; auto &b=blendDesc.RenderTarget[0]; b.BlendEnable=TRUE;
        b.SrcBlend=D3D11_BLEND_ONE; b.DestBlend=caseName=="disabled" ? D3D11_BLEND_ONE : D3D11_BLEND_INV_SRC_ALPHA; b.BlendOp=D3D11_BLEND_OP_ADD;
        b.SrcBlendAlpha=D3D11_BLEND_ONE; b.DestBlendAlpha=b.DestBlend; b.BlendOpAlpha=D3D11_BLEND_OP_ADD; b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        ComPtr<ID3D11BlendState> blend; check(dev->CreateBlendState(&blendDesc,&blend),"CreateBlendState");
        ctx->OMSetBlendState(blend.Get(),nullptr,0xffffffffu);
        const UINT width=256,height=144,mw=64,mh=36;
        D3D11_VIEWPORT viewport{}; viewport.Width=(float)width; viewport.Height=(float)height; viewport.MaxDepth=1;
        ctx->RSSetViewports(1,&viewport);
        D3D11_TEXTURE2D_DESC rtDesc{}; rtDesc.Width=width; rtDesc.Height=height; rtDesc.MipLevels=1; rtDesc.ArraySize=1;
        rtDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM; rtDesc.SampleDesc.Count=1; rtDesc.Usage=D3D11_USAGE_DEFAULT; rtDesc.BindFlags=D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> target; ComPtr<ID3D11RenderTargetView> rtv;
        check(dev->CreateTexture2D(&rtDesc,nullptr,&target),"CreateTexture2D target");
        check(dev->CreateRenderTargetView(target.Get(),nullptr,&rtv),"CreateRenderTargetView");
        ID3D11RenderTargetView *targetView=rtv.Get(); ctx->OMSetRenderTargets(1,&targetView,nullptr);
        const std::string assets=argv[2],fixtures=argv[3],out=argv[4];
        std::string fixturePrefix=fixtures+"/256x144-"+(caseName=="normal" ? "" : (caseName=="nocapture" ? "palette-" : caseName+"-"));
        ReadTexture displacement(dev.Get(),256,256,read(assets+"/BlockNoise1.rgba"));
        ReadTexture spark(dev.Get(),128,128,read(assets+"/PointNoise.rgba"));
        ReadTexture hoverNoise(dev.Get(),256,256,read(assets+"/FD_Noise_00000.rgba"));
        bool paletteScene=caseName=="palette" || caseName=="nocapture";
        auto sceneBytes=paletteScene ? read(assets+"/palette-source.rgba") : std::vector<uint8_t>{25,25,25,255};
        ReadTexture scene(dev.Get(),paletteScene ? width : 1,paletteScene ? height : 1,sceneBytes);
        ReadTexture aux(dev.Get(),mw,mh,read(fixturePrefix+"aux.bin"));
        std::vector<uint8_t> greenPixels((size_t)mw*mh*4);
        for (size_t i=0;i<greenPixels.size();i+=4) { greenPixels[i]=84; greenPixels[i+1]=255; greenPixels[i+2]=84; greenPixels[i+3]=255; }
        ReadTexture green(dev.Get(),mw,mh,greenPixels);
        bindPixel(dev.Get(),ctx.Get(),ps,"uDisplaceTex",displacement.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_MIRROR);
        bindPixel(dev.Get(),ctx.Get(),ps,"uSparkTex",spark.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_WRAP);
        bindPixel(dev.Get(),ctx.Get(),ps,"_GrabTexture",scene.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_LINEAR,D3D11_TEXTURE_ADDRESS_CLAMP);
        bindPixel(dev.Get(),ctx.Get(),ps,"uAuxMasks",aux.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_LINEAR,D3D11_TEXTURE_ADDRESS_CLAMP);
        bindPixel(dev.Get(),ctx.Get(),ps,"uNoiseTex",hoverNoise.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_MIRROR);
        bindPixel(dev.Get(),ctx.Get(),ps,"uActiveColors",green.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_CLAMP);
        bindPixel(dev.Get(),ctx.Get(),ps,"uDisabledColors",green.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_CLAMP);
        vs.vector3("uView",(float)width,(float)height,16.f/9.f); vs.upload(ctx.Get(),true);
        ps.vector("_PEVScreenParams",(float)width,(float)height,1.f+1.f/width,1.f+1.f/height);
        ps.vector("_EffectRT_TexelSize",1.f/mw,1.f/mh,(float)mw,(float)mh);
        const std::pair<const char *,float> floats[]={
            {"_EdgeOpacity",.8f},{"_FillStrength",.667f},{"_FillOpacity",.667f},{"_GlowIntensity",.8f},
            {"_SparkMapOpacity",5.69f},{"_SparkHueShiftAmount",.2f},{"_SparkDisplaceIntensity",2.39f},
            {"_DisplaceBlendIntensity",.411f},{"_DisplaceSpeed",1.5f},{"_DisplaceStrength",.15f},
            {"_BackgroundPixelScale",6.f},{"uReadyExtra",0.f}, {"uDisabledFillOpacity",.4f},
            {"uDisabledSparkOpacity",3.5f},{"uDisabledSparkIntensity",2.29f},{"uDisabledSpeed",.3f},
            {"_ShineSpeed",37.9f},{"_ShineBrightness",.12f}, {"_TouchPosRadius",.5f},
            {"_TouchPosSDFSmoothness",.47f},{"_TouchPosSDFFalloff",.41f},{"_TouchDisplaceSpeed",2.9f},
            {"_TouchDisplaceStrength",.08f},{"_NoiseEvoSpeed",.03f},{"_NoiseDirChangeSpeed",60.f},
            {"_NoiseDisplaceStrength",1.f},{"_NoiseRadius",.48f},{"_NoiseSmoothness",1.f},
            {"_SDFCellSize",.11f},{"_SDFSmoothness",.63f},{"_SDFFalloff",.34f},
            {"_SDFMoveSpeed",9.3f},{"_TouchBackgroundPixelScale",8.f}
        };
        for (auto &f:floats) ps.scalar(f.first,f.second);
        ps.vector("_EdgeColor",1.f,.33018857f,.33018857f,1.f);
        ps.vector("_FillColor",.7132075f,.23549296f,.23549296f,1.f);
        ps.vector("_GlowColor",1.f,.17924517f,.17924517f,1.f);
        ps.vector("_DisplaceDirection",1,1,0,0);
        ps.vector("uDisabledFillColor",.497f,.13766898f,.13766898f,1.f);
        ps.vector("_ShineColor",1,1,1,1);
        ps.vector("_TouchDisplaceDirection",1,1,0,0);
        ps.vector("_NoiseTint",1,0,0,1);
        ps.vector("_TouchGlowColor",1,0,0,1);
        ps.vector("_PEVBackground",0,0,0,1);
        ps.vector3("_SparkTint",1.f,.28490567f,.28490567f);
        ps.vector3("uDisabledSparkTint",.31132078f,.077830195f,.077830195f);
        if (caseName=="hover") {
            const int touchCount=2;
            const float touchPositions[8]={.55f*width/height,.48f,0,0,.43f*width/height,.55f,0,0};
            ps.set("_TouchPosCount",&touchCount,sizeof(touchCount));
            ps.set("_TouchPos",touchPositions,sizeof(touchPositions));
        }
        ps.scalar("_DebugColor",1.f);
        for (int time: {0,1,9}) {
            std::string label=std::to_string(time);
            ReadTexture masks(dev.Get(),mw,mh,read(fixturePrefix+"effect-"+label+".bin"));
            bindPixel(dev.Get(),ctx.Get(),ps,"uMasks",masks.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_LINEAR,D3D11_TEXTURE_ADDRESS_CLAMP);
            ps.scalar("_TouchPosShine",(.63f+.37f*(std::sin(time*43.f)*.5f+.5f))*2.f);
            ps.vector("uUnityTime",time/20.f,(float)time,time*2.f,time*3.f); ps.upload(ctx.Get(),false);
            if (paletteScene) {
                ctx->UpdateSubresource(target.Get(),0,nullptr,sceneBytes.data(),width*4,0);
            } else if (caseName=="ready-full") {
                auto underlay=read(out+"/disabled-frame-"+label+".rgba");
                if (underlay.size()!=(size_t)width*height*4) throw std::runtime_error("ready underlay size mismatch");
                ctx->UpdateSubresource(target.Get(),0,nullptr,underlay.data(),width*4,0);
            } else {
                const float gray=25.f/255.f, clear[]={gray,gray,gray,1}; ctx->ClearRenderTargetView(rtv.Get(),clear);
            }
            ctx->Draw(4,0);
            D3D11_TEXTURE2D_DESC sd=rtDesc; sd.Usage=D3D11_USAGE_STAGING; sd.BindFlags=0; sd.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging; check(dev->CreateTexture2D(&sd,nullptr,&staging),"Create staging target");
            ctx->CopyResource(staging.Get(),target.Get()); D3D11_MAPPED_SUBRESOURCE mapped{};
            check(ctx->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped),"Map target");
            std::vector<uint8_t> output((size_t)width*height*4);
            for (UINT y=0;y<height;y++) std::memcpy(output.data()+(size_t)y*width*4,(uint8_t *)mapped.pData+(size_t)y*mapped.RowPitch,width*4);
            ctx->Unmap(staging.Get(),0);
            write(out+"/frame-"+label+".rgba",output);
            ID3D11ShaderResourceView *nulls[8]{}; ctx->PSSetShaderResources(0,8,nulls);
            // Rebind the static textures for the next iteration.
            bindPixel(dev.Get(),ctx.Get(),ps,"uDisplaceTex",displacement.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_MIRROR);
            bindPixel(dev.Get(),ctx.Get(),ps,"uSparkTex",spark.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_WRAP);
            bindPixel(dev.Get(),ctx.Get(),ps,"_GrabTexture",scene.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_LINEAR,D3D11_TEXTURE_ADDRESS_CLAMP);
            bindPixel(dev.Get(),ctx.Get(),ps,"uAuxMasks",aux.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_LINEAR,D3D11_TEXTURE_ADDRESS_CLAMP);
            bindPixel(dev.Get(),ctx.Get(),ps,"uNoiseTex",hoverNoise.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_MIRROR);
            bindPixel(dev.Get(),ctx.Get(),ps,"uActiveColors",green.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_CLAMP);
            bindPixel(dev.Get(),ctx.Get(),ps,"uDisabledColors",green.srv.Get(),D3D11_FILTER_MIN_MAG_MIP_POINT,D3D11_TEXTURE_ADDRESS_CLAMP);
            std::cout << "D3D11 rendered " << caseName << " reference inputs at t=" << time << std::endl;
        }
        return 0;
    } catch (const std::exception &e) { std::cerr << e.what() << std::endl; return 1; }
}
