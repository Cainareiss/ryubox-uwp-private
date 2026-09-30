void TestPrograms(HMODULE dll, void* context)
{
#define API(name) auto name=Load<decltype(&RyuboxD3D12_##name)>(dll,"RyuboxD3D12_" #name)
    API(CreateProgram); API(ReleaseProgram); API(GetProgramBindings); API(DispatchProgram); API(DrawProgram);
    API(CreateBuffer); API(ReleaseResource); API(UploadBuffer); API(ReadBuffer);
    API(BeginFrame); API(EndFrame); API(Capture);
#undef API
    uint64_t program=0,src=0,dst=0,cb=0,vertices=0,indices=0,indirect=0;
    const char* cs=R"(
ByteAddressBuffer inputs[2] : register(t3,space2);
RWByteAddressBuffer output : register(u5,space4);
cbuffer Params : register(b7,space1) { uint addend; };
[numthreads(1,1,1)] void main(uint3 id : SV_DispatchThreadID) {
output.Store(id.x*4,inputs[0].Load(id.x*4)+inputs[1].Load(id.x*4)+addend);
})";
    RyuboxD12Shader shader{0,cs,strlen(cs)};
    Check(CreateProgram(context,&shader,1,&program),"reflected compute program");
    UINT bindingCount=0; Check(GetProgramBindings(context,program,nullptr,&bindingCount),"reflection size");
    Require(bindingCount==3,"three reflected ranges");
    std::vector<RyuboxD12BindingInfo> reflection(bindingCount);
    Check(GetProgramBindings(context,program,reflection.data(),&bindingCount),"reflection values");
    bool array=false; for (const auto& b:reflection) if(b.space==2 && b.binding==3 && b.count==2) array=true;
    Require(array,"register spaces and descriptor arrays preserved");
    Check(CreateBuffer(context,256,&src),"reflected source"); Check(CreateBuffer(context,256,&dst),"reflected output");
    Check(CreateBuffer(context,256,&cb),"reflected cbuffer");
    UINT numbers[16]{},result[16]{},constants[64]{}; constants[0]=19;
    for (UINT i=0;i<16;++i) numbers[i]=i*11;
    RyuboxD12Binding bindings[4]{};
    bindings[0]={0,0,2,3,src,0,64}; bindings[1]={0,0,2,4,src,0,64};
    bindings[2]={0,1,4,5,dst,0,64}; bindings[3]={0,2,1,7,cb,17,4};
    uint64_t fence=0;
    Check(BeginFrame(context,0,0,0,1),"reflected begin");
    Check(UploadBuffer(context,src,0,numbers,sizeof(numbers)),"reflected upload");
    Check(UploadBuffer(context,cb,17,constants,4),"unaligned cbuffer upload");
    Require(DispatchProgram(context,program,bindings,3,16,1,1)==E_INVALIDARG,"missing binding rejects before recording");
    Check(DispatchProgram(context,program,bindings,4,16,1,1),"reflected dispatch");
    Check(ReleaseProgram(context,program),"deferred reflected root and PSO release");
    Check(EndFrame(context,0,&fence),"reflected submit");
    Check(ReadBuffer(context,dst,0,result,sizeof(result)),"reflected result");
    for(UINT i=0;i<16;++i) Require(result[i]==numbers[i]*2+19,"reflected array/CBV compute output");

    const char* vs=R"(
cbuffer SPIRV_Cross_VertexInfo : register(b0, space2147483647) { int baseVertex; int baseInstance; };
struct Out { float4 position:SV_Position; float4 color:COLOR0; };
Out main(float2 pos:POSITION, float4 instanceColor:COLOR0) {
Out o; o.position=float4(pos,0,1); o.color=baseVertex==1 && baseInstance==3 ? instanceColor : float4(0,0,1,1); return o;
})";
    const char* ps="float4 main(float4 position:SV_Position,float4 color:COLOR0):SV_Target { return color; }";
    RyuboxD12Shader stages[]={{1,vs,strlen(vs)},{5,ps,strlen(ps)}};
    Check(CreateProgram(context,stages,2,&program),"graphics program");
    Check(CreateBuffer(context,256,&vertices),"vertex buffer"); Check(CreateBuffer(context,256,&indices),"index buffer");
    Check(CreateBuffer(context,256,&indirect),"indirect buffer");
    float verts[]={0,0, -1,-1, -1,3, 3,-1}; // baseVertex=1 must skip first vertex.
    uint16_t index[]={99,0,1,2}; // firstIndex=1 must skip 99.
    UINT args[]={3,1,1,1,3,1}; // count,instances,firstIndex,baseVertex,firstInstance; count buffer at byte20.
    D3D12_INPUT_ELEMENT_DESC attribs[]={
        {"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0},
        {"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,1,0,D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA,1}};
    float instanceColors[]={1,0,0,1, 1,0,0,1, 1,0,0,1, 0,1,0,1};
    RyuboxD12VertexBuffer vertex[]={{vertices,0,sizeof(verts),8},{cb,0,sizeof(instanceColors),16}};
    RyuboxD12Draw draw{};
    draw.colorTargetCount = 1;
    draw.attributes=attribs; draw.attributeCount=2; draw.vertices=vertex; draw.vertexCount=2;
    draw.indexResource=indices; draw.indexBytes=sizeof(index); draw.indexFormat=DXGI_FORMAT_R16_UINT;
    draw.topology=D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST; draw.count=3; draw.instances=1; draw.first=1;
    draw.baseVertex=1; draw.firstInstance=3;
    draw.viewport={0,0,70,50,0,1}; draw.scissor={0,0,70,50};
    draw.rasterizer.FillMode=D3D12_FILL_MODE_SOLID; draw.rasterizer.CullMode=D3D12_CULL_MODE_NONE; draw.rasterizer.DepthClipEnable=TRUE;
    auto& blend=draw.blend.RenderTarget[0]; blend.SrcBlend=blend.SrcBlendAlpha=D3D12_BLEND_ONE;
    blend.DestBlend=blend.DestBlendAlpha=D3D12_BLEND_ZERO; blend.BlendOp=blend.BlendOpAlpha=D3D12_BLEND_OP_ADD;
    blend.LogicOp=D3D12_LOGIC_OP_NOOP; blend.RenderTargetWriteMask=15;
    for (UINT mode=0;mode<3;++mode)
    {
        Check(BeginFrame(context,0,0,0,1),"graphics begin");
        Check(UploadBuffer(context,vertices,0,verts,sizeof(verts)),"vertices upload");
        Check(UploadBuffer(context,indices,0,index,sizeof(index)),"indices upload");
        Check(UploadBuffer(context,indirect,0,args,sizeof(args)),"indirect upload");
        Check(UploadBuffer(context,cb,0,instanceColors,sizeof(instanceColors)),"instance data upload");
        draw.indirectResource=mode ? indirect : 0;
        draw.indirectStride=20; draw.maxCommands=1; draw.countResource=mode==2 ? indirect : 0; draw.countOffset=mode==2 ? 20 : 0;
        Check(DrawProgram(context,program,nullptr,0,&draw),"indexed instanced/indirect draw");
        if(mode==2) Check(ReleaseProgram(context,program),"inflight graphics release");
        Check(EndFrame(context,0,&fence),"graphics submit");
        std::vector<UINT> pixels(70*50); Check(Capture(context,pixels.data(),pixels.size()*4),"graphics capture");
        if(pixels[25*70+35]!=0xff00ff00) std::cerr<<"draw mode="<<mode<<" center=0x"<<std::hex<<pixels[25*70+35]<<std::dec<<'\n';
        Require(pixels[25*70+35]==0xff00ff00,"indexed/indirect firstInstance/baseVertex pixel");
    }
    const char* gs=R"(
struct Out { float4 position:SV_Position; float4 color:COLOR0; };
[maxvertexcount(3)] void main(triangle Out v[3], inout TriangleStream<Out> stream) {
for(uint i=0;i<3;++i) stream.Append(v[i]);
})";
    const char* hs=R"(
struct Out { float4 position:SV_Position; float4 color:COLOR0; };
struct Factors { float edges[3]:SV_TessFactor; float inner:SV_InsideTessFactor; };
Factors Patch(InputPatch<Out,3> v) { Factors f; f.edges[0]=f.edges[1]=f.edges[2]=1; f.inner=1; return f; }
[domain("tri")][partitioning("integer")][outputtopology("triangle_cw")][outputcontrolpoints(3)][patchconstantfunc("Patch")]
Out main(InputPatch<Out,3> v, uint i:SV_OutputControlPointID) { return v[i]; }
)";
    const char* ds=R"(
struct Out { float4 position:SV_Position; float4 color:COLOR0; };
struct Factors { float edges[3]:SV_TessFactor; float inner:SV_InsideTessFactor; };
[domain("tri")] Out main(Factors factors, float3 coord:SV_DomainLocation, const OutputPatch<Out,3> v) {
Out o; o.position=v[0].position*coord.x+v[1].position*coord.y+v[2].position*coord.z; o.color=v[0].color; return o;
})";
    for(UINT mode=0;mode<2;++mode)
    {
        std::vector<RyuboxD12Shader> shaders={{1,vs,strlen(vs)},{5,ps,strlen(ps)}};
        if(mode==0) shaders.push_back({4,gs,strlen(gs)});
        else { shaders.push_back({2,hs,strlen(hs)}); shaders.push_back({3,ds,strlen(ds)}); }
        Check(CreateProgram(context,shaders.data(),static_cast<UINT>(shaders.size()),&program),"geometry/tessellation program");
        draw.indirectResource=draw.countResource=0;
        draw.topology=mode==0 ? D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST : D3D_PRIMITIVE_TOPOLOGY_3_CONTROL_POINT_PATCHLIST;
        Check(BeginFrame(context,0,0,0,1),"geometry/tessellation begin");
        Check(DrawProgram(context,program,nullptr,0,&draw),"geometry/tessellation draw");
        Check(ReleaseProgram(context,program),"geometry/tessellation release");
        Check(EndFrame(context,0,&fence),"geometry/tessellation submit");
        std::vector<UINT> pixels(70*50); Check(Capture(context,pixels.data(),pixels.size()*4),"geometry/tessellation capture");
        Require(pixels[25*70+35]==0xff00ff00,"geometry/tessellation output pixel");
    }
    for(auto id : {src,dst,cb,vertices,indices,indirect}) Check(ReleaseResource(context,id),"program test release");
    std::cout<<"PASS reflected register spaces, resource arrays, CBV/SRV/UAV, indexed instancing, indirect/count and deferred programs\n";
}
