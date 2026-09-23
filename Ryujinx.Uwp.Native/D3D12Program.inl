// Context member implementations. All calls execute under Context::mutex.
static D3D12_SHADER_VISIBILITY Visibility(UINT stage)
{
    constexpr D3D12_SHADER_VISIBILITY values[] = {D3D12_SHADER_VISIBILITY_ALL,
        D3D12_SHADER_VISIBILITY_VERTEX,D3D12_SHADER_VISIBILITY_HULL,D3D12_SHADER_VISIBILITY_DOMAIN,
        D3D12_SHADER_VISIBILITY_GEOMETRY,D3D12_SHADER_VISIBILITY_PIXEL};
    Require(stage < 6); return values[stage];
}

std::shared_ptr<Program> GetProgram(UINT64 id)
{
    auto it = programs.find(id);
    if (it == programs.end())
    {
        // A stale managed wrapper must be reported as such. The previous bare
        // Require returned E_INVALIDARG before DrawProgram could publish any
        // diagnostic, which made a program-lifetime fault look like a D3D12
        // graphics-state failure.
        programError = "D3D12 program handle is not live: id=" + std::to_string(id) +
            ", livePrograms=" + std::to_string(programs.size());
        Require(false);
    }

    return it->second;
}

static UINT GetShaderCompileFlags()
{
    static UINT s_flags = []() {
        wchar_t opt[4]{};
        if (GetEnvironmentVariableW(L"RYUJINX_UWP_D3D12_SHADER_OPT3", opt, 4) != 0 && opt[0] == L'1')
            return static_cast<UINT>(D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3);
        if (GetEnvironmentVariableW(L"RYUJINX_UWP_D3D12_SHADER_OPT0", opt, 4) != 0 && opt[0] == L'1')
            return static_cast<UINT>(D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL0);
        return static_cast<UINT>(D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL1);
    }();
    return s_flags;
}

static uint64_t HashShaderSource(UINT stage, UINT flags, const char* source, size_t bytes)
{
    uint64_t hash = 14695981039346656037ULL;
    auto add = [&](const void* data, size_t len) {
        const uint8_t* p = static_cast<const uint8_t*>(data);
        for (size_t i = 0; i < len; ++i) {
            hash ^= p[i];
            hash *= 1099511628211ULL;
        }
    };
    add(&stage, sizeof(stage));
    add(&flags, sizeof(flags));
    add(source, bytes);
    return hash;
}

static std::wstring GetDxbcCacheDir()
{
    static std::wstring s_dir = []() -> std::wstring {
        wchar_t path[MAX_PATH]{};
        if (GetEnvironmentVariableW(L"RYUJINX_UWP_D3D12_CACHE_DIR", path, MAX_PATH) > 0)
        {
            CreateDirectoryW(path, nullptr);
            return path;
        }
        if (GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH) > 0)
        {
            std::wstring dir = path;
            dir += L"\\Packages\\Ryujinx.Xbox.UWP_z7hww2mmeny5j\\LocalState\\D3D12ShaderCache";
            CreateDirectoryW(dir.c_str(), nullptr);
            return dir;
        }
        return L"";
    }();
    return s_dir;
}

UINT64 CreateProgram(const RyuboxD12Shader* sources, UINT count)
{
    programError.clear();
    Require(sources && count && count <= 5 && programs.size() < 2048);
    auto p = std::make_shared<Program>();
    constexpr const char* profiles[] = {"cs_5_1","vs_5_1","hs_5_1","ds_5_1","gs_5_1","ps_5_1"};
    std::vector<std::vector<D3D12_DESCRIPTOR_RANGE>> ranges;
    for (UINT i=0;i<count;++i)
    {
        const auto& s = sources[i];
        Require(s.stage < 6 && !p->shaders[s.stage] && s.source && s.bytes && s.bytes <= 4*MiB);
        if (s.bytes >= 4 && memcmp(s.source, "DXBC", 4) == 0)
        {
            Check(D3DCreateBlob(static_cast<SIZE_T>(s.bytes), &p->shaders[s.stage]));
            memcpy(p->shaders[s.stage]->GetBufferPointer(), s.source, static_cast<SIZE_T>(s.bytes));
        }
        else
        {
            ComPtr<ID3DBlob> error;
            std::wstring cacheDir = GetDxbcCacheDir();
            std::wstring cacheFile;
            bool loadedFromCache = false;
            if (!cacheDir.empty())
            {
                uint64_t h = HashShaderSource(s.stage, GetShaderCompileFlags(), s.source, static_cast<size_t>(s.bytes));
                wchar_t name[36];
                swprintf_s(name, L"\\%016llx.dxbc", static_cast<unsigned long long>(h));
                cacheFile = cacheDir + name;

                CREATEFILE2_EXTENDED_PARAMETERS readParams{};
                readParams.dwSize = sizeof(readParams);
                readParams.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
                HANDLE f = CreateFile2(cacheFile.c_str(), GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, &readParams);
                if (f != INVALID_HANDLE_VALUE)
                {
                    LARGE_INTEGER sz{};
                    if (GetFileSizeEx(f, &sz) && sz.QuadPart >= 4 && sz.QuadPart <= 16 * 1024 * 1024)
                    {
                        if (SUCCEEDED(D3DCreateBlob(static_cast<SIZE_T>(sz.QuadPart), &p->shaders[s.stage])))
                        {
                            DWORD read = 0;
                            if (ReadFile(f, p->shaders[s.stage]->GetBufferPointer(), static_cast<DWORD>(sz.QuadPart), &read, nullptr) &&
                                read == static_cast<DWORD>(sz.QuadPart) &&
                                memcmp(p->shaders[s.stage]->GetBufferPointer(), "DXBC", 4) == 0)
                            {
                                loadedFromCache = true;
                            }
                            else
                            {
                                p->shaders[s.stage].Reset();
                            }
                        }
                    }
                    CloseHandle(f);
                }
            }

            if (!loadedFromCache)
            {
                ++shaderCompilations;
                ++performanceStats.shaderCacheMisses;
                LARGE_INTEGER compileStart{}, compileEnd{};
                QueryPerformanceCounter(&compileStart);
                HRESULT hr = D3DCompile(s.source,static_cast<SIZE_T>(s.bytes),"RyuboxD12Program",nullptr,nullptr,
                    "main",profiles[s.stage],GetShaderCompileFlags(),
                    0,&p->shaders[s.stage],&error);
                QueryPerformanceCounter(&compileEnd);
                UINT64 compileUs = ElapsedMicroseconds(compileStart, compileEnd);
                performanceStats.shaderCompileMicroseconds += compileUs;
                ULONGLONG compileMs = compileUs / 1000;
                if (compileMs >= 100)
                {
                    char dbg[128];
                    snprintf(dbg, sizeof(dbg), "D3D12 shader compiled: stage=%s, bytes=%u, elapsed=%llu ms\n",
                        profiles[s.stage], (unsigned)s.bytes, (unsigned long long)compileMs);
                    OutputDebugStringA(dbg);
                }
                if (FAILED(hr) && error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
                if (FAILED(hr) && error) programError.assign(static_cast<const char*>(error->GetBufferPointer()),error->GetBufferSize());
                // Bad shader input is not a device fault. Do not poison a live context.
                Require(SUCCEEDED(hr));

                if (!cacheFile.empty() && p->shaders[s.stage])
                {
                    CREATEFILE2_EXTENDED_PARAMETERS writeParams{};
                    writeParams.dwSize = sizeof(writeParams);
                    writeParams.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
                    HANDLE f = CreateFile2(cacheFile.c_str(), GENERIC_WRITE, 0, CREATE_ALWAYS, &writeParams);
                    if (f != INVALID_HANDLE_VALUE)
                    {
                        DWORD written = 0;
                        WriteFile(f, p->shaders[s.stage]->GetBufferPointer(), static_cast<DWORD>(p->shaders[s.stage]->GetBufferSize()), &written, nullptr);
                        CloseHandle(f);
                    }
                }
            }
            else
            {
                ++performanceStats.shaderCacheHits;
            }
        }
        auto& blob = p->shaders[s.stage];
        if (s.stage==2)
        {
            std::string source(s.source,static_cast<SIZE_T>(s.bytes));
            if(source.find("// RYUBOX_HULL_INPUT_SPECIALIZATION_V1")!=std::string::npos)
                p->hullSource=std::move(source);
        }
        ComPtr<ID3D12ShaderReflection> reflection;
        Check(D3DReflect(blob->GetBufferPointer(),blob->GetBufferSize(),IID_PPV_ARGS(&reflection)));
        D3D12_SHADER_DESC desc{}; Check(reflection->GetDesc(&desc));
        if(s.stage==4) p->geometryOutput=desc.GSOutputTopology;
        if(s.stage==2) p->tessellationOutput=desc.HSOutputPrimitive;
        if(s.stage==1)
            for(UINT input=0;input<desc.InputParameters;++input)
            {
                D3D12_SIGNATURE_PARAMETER_DESC parameter{}; Check(reflection->GetInputParameterDesc(input,&parameter));
                if(parameter.SystemValueType!=D3D_NAME_UNDEFINED || !parameter.ReadWriteMask) continue;
                RyuboxD12VertexInput value{};
                Require(strlen(parameter.SemanticName)<sizeof(value.semantic));
                strcpy_s(value.semantic,parameter.SemanticName); value.index=parameter.SemanticIndex;
                value.componentType=parameter.ComponentType; value.mask=parameter.Mask; p->inputs.push_back(value);
            }
        for (UINT sampler=0;sampler<2;++sampler)
        {
            std::vector<D3D12_DESCRIPTOR_RANGE> table;
            UINT offset = 0;
            for (UINT r=0;r<desc.BoundResources;++r)
            {
                D3D12_SHADER_INPUT_BIND_DESC b{}; Check(reflection->GetResourceBindingDesc(r,&b));
                if (b.Type==D3D_SIT_CBUFFER && b.Space==0x7fffffffu && b.BindPoint==1)
                {
                    Require(s.stage==0 && b.BindCount==1);
                    p->dispatchRoot=0; continue;
                }
                if (b.Type==D3D_SIT_CBUFFER && b.Space==0x7fffffffu && b.BindPoint==0 &&
                    strcmp(b.Name,"SPIRV_Cross_VertexInfo")==0)
                {
                    Require(s.stage==1 && b.BindCount==1);
                    p->auxiliaryRoot=0; continue;
                }
                if ((b.Type == D3D_SIT_SAMPLER) != (sampler != 0)) continue;
                // Bounded arrays only. Append/counter/feedback resources need
                // their own counter allocation, never an uninitialized dummy.
                Require(b.BindCount && b.BindCount <= 512);
                D3D12_DESCRIPTOR_RANGE_TYPE type;
                switch (b.Type)
                {
                case D3D_SIT_CBUFFER: type = D3D12_DESCRIPTOR_RANGE_TYPE_CBV; break;
                case D3D_SIT_SAMPLER: type = D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER; break;
                case D3D_SIT_TEXTURE: case D3D_SIT_BYTEADDRESS: case D3D_SIT_STRUCTURED:
                    type = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; break;
                case D3D_SIT_UAV_RWTYPED: case D3D_SIT_UAV_RWBYTEADDRESS: case D3D_SIT_UAV_RWSTRUCTURED:
                    type = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; break;
                default: Require(false); type = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
                }
                Require(Fits(offset,b.BindCount,sampler ? SamplersPerFrame : DescriptorsPerFrame));
                table.push_back({type,b.BindCount,b.BindPoint,b.Space,offset});
                p->bindings.push_back({{s.stage,static_cast<UINT>(type),b.Space,b.BindPoint,b.BindCount,
                    static_cast<UINT>(b.Dimension)},b.Type,static_cast<UINT>(ranges.size()),offset});
                offset += b.BindCount;
            }
            if (!table.empty()) { ranges.push_back(std::move(table)); p->tables.emplace_back(s.stage,sampler); }
        }
    }
    bool compute = p->shaders[0] != nullptr;
    Require(compute ? count == 1 : p->shaders[1] != nullptr);
    Require(bool(p->shaders[2]) == bool(p->shaders[3]));
    std::vector<D3D12_ROOT_PARAMETER> roots(ranges.size());
    for (UINT i=0;i<roots.size();++i)
    {
        roots[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        roots[i].DescriptorTable = {static_cast<UINT>(ranges[i].size()),ranges[i].data()};
        roots[i].ShaderVisibility = Visibility(p->tables[i].first);
    }
    if (p->auxiliaryRoot!=UINT_MAX)
    {
        p->auxiliaryRoot=static_cast<UINT>(roots.size());
        D3D12_ROOT_PARAMETER aux{}; aux.ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        aux.Constants={0,0x7fffffffu,2}; aux.ShaderVisibility=D3D12_SHADER_VISIBILITY_VERTEX; roots.push_back(aux);
    }
    if (p->dispatchRoot!=UINT_MAX)
    {
        p->dispatchRoot=static_cast<UINT>(roots.size());
        D3D12_ROOT_PARAMETER aux{}; aux.ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        aux.Constants={1,0x7fffffffu,3}; aux.ShaderVisibility=D3D12_SHADER_VISIBILITY_ALL; roots.push_back(aux);
    }
    D3D12_ROOT_SIGNATURE_DESC rootDesc{static_cast<UINT>(roots.size()),roots.data(),0,nullptr,
        compute ? D3D12_ROOT_SIGNATURE_FLAG_NONE :
            (D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT | D3D12_ROOT_SIGNATURE_FLAG_ALLOW_STREAM_OUTPUT)};
    ComPtr<ID3DBlob> signature,error;
    HRESULT hr = D3D12SerializeRootSignature(&rootDesc,D3D_ROOT_SIGNATURE_VERSION_1,&signature,&error);
    if (FAILED(hr) && error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
    if (FAILED(hr) && error) programError.assign(static_cast<const char*>(error->GetBufferPointer()),error->GetBufferSize());
    Require(SUCCEEDED(hr));
    p->rootBlob.assign(static_cast<const BYTE*>(signature->GetBufferPointer()),
        static_cast<const BYTE*>(signature->GetBufferPointer()) + signature->GetBufferSize());
    Check(device->CreateRootSignature(0,signature->GetBufferPointer(),signature->GetBufferSize(),IID_PPV_ARGS(&p->root)));
    if (compute)
    {
        D3D12_COMPUTE_PIPELINE_STATE_DESC desc{};
        desc.pRootSignature=p->root.Get(); desc.CS={p->shaders[0]->GetBufferPointer(),p->shaders[0]->GetBufferSize()};
        std::wstring name=PipelineName(*p,"cs",{});
        HRESULT result=E_FAIL;
        if (pipelineLibrary)
        {
            result=pipelineLibrary->LoadComputePipeline(name.c_str(),&desc,IID_PPV_ARGS(&p->compute));
            if (SUCCEEDED(result)) ++pipelineCacheHits;
        }
        if (FAILED(result))
        {
            Check(device->CreateComputePipelineState(&desc,IID_PPV_ARGS(&p->compute)));
            if (pipelineLibrary && SUCCEEDED(pipelineLibrary->StorePipeline(name.c_str(),p->compute.Get())))
                pipelineCacheDirty=true;
        }
    }
    UINT64 id=nextResource++; programs.emplace(id,p); return id;
}

void ReadTransition(const std::shared_ptr<Resource>& resource, D3D12_RESOURCE_STATES state)
{
    auto previous = resource->state;
    if ((previous & ~D3D12_RESOURCE_STATE_GENERIC_READ) == 0) state |= previous;
    Transition(resource,state);
}

void ReadTransitionView(const TextureView& view, D3D12_RESOURCE_STATES state)
{
    auto resource = view.resource;
    if (!resource) return;
    EnsureSubresourceStates(resource.get());
    UINT mipLevels = resource->desc.MipLevels ? resource->desc.MipLevels : 1;
    UINT arraySize = (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D) ? 1 : (resource->desc.DepthOrArraySize ? resource->desc.DepthOrArraySize : 1);
    UINT planeCount = IsPlanarDepthStencil(resource->desc.Format) ? 2 : 1;
    // Texture3D subresources are indexed by mip/plane only. A W slice is a
    // descriptor range, so it cannot be used as an array-subresource index.
    UINT firstSlice = resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D ? 0 : view.firstSlice;
    UINT sliceCount = resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D ? 1 : view.sliceCount;

    for (UINT plane = 0; plane < planeCount; ++plane)
    {
        for (UINT slice = firstSlice; slice < firstSlice + sliceCount && slice < arraySize; ++slice)
        {
            for (UINT mip = view.firstMip; mip < view.firstMip + view.mipCount && mip < mipLevels; ++mip)
            {
                UINT subresource = mip + slice * mipLevels + plane * mipLevels * arraySize;
                if (subresource < resource->subresourceStates.size())
                {
                    auto previous = resource->subresourceStates[subresource];
                    auto targetState = state;
                    if ((previous & ~D3D12_RESOURCE_STATE_GENERIC_READ) == 0) targetState |= previous;
                    TransitionSubresource(resource, subresource, targetState);
                }
            }
        }
    }
}

std::vector<std::shared_ptr<Resource>> BindProgram(const std::shared_ptr<Program>& p,
    const RyuboxD12Binding* bindings, UINT count)
{
    Require(recording && (!count || bindings) && count <= 4096);
    bool compute = p->compute != nullptr;
    auto& frame=frames[frameIndex];
    std::vector<const RyuboxD12Binding*> matched;
    std::map<Resource*,std::pair<std::shared_ptr<Resource>,D3D12_RESOURCE_STATES>> states;
    std::vector<std::pair<TextureView,D3D12_RESOURCE_STATES>> viewStates;
    std::vector<UINT> sizes(p->tables.size());
    // Validate the entire packet before emitting barriers or consuming heap slots.
    for (const auto& reflected : p->bindings)
    {
        const auto& b=reflected.info;
        sizes[reflected.table] = std::max(sizes[reflected.table],reflected.tableOffset+b.count);
        for (UINT element=0;element<b.count;++element)
        {
            const RyuboxD12Binding* found=nullptr;
            for (UINT i=0;i<count;++i)
                if (bindings[i].stage==b.stage && bindings[i].type==b.type && bindings[i].space==b.space &&
                    bindings[i].binding==b.binding+element) { Require(!found); found=&bindings[i]; }
            Require(found); matched.push_back(found);
            if (b.type==D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER)
            {
                const auto& s=found->sampler;
                Require(s.AddressU>=1 && s.AddressU<=5 && s.AddressV>=1 && s.AddressV<=5 &&
                    s.AddressW>=1 && s.AddressW<=5 && s.MaxAnisotropy>=1 && s.MaxAnisotropy<=16 &&
                    s.ComparisonFunc>=1 && s.ComparisonFunc<=8 && s.MinLOD<=s.MaxLOD);
                continue;
            }
            auto resource=GetResource(found->resource);
            bool buffer=resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_BUFFER;
            auto state=D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
            if (b.type==D3D12_DESCRIPTOR_RANGE_TYPE_CBV)
            {
                Require(buffer && found->bytes && found->bytes<=65536 &&
                    Fits(found->offset,found->bytes,resource->desc.Width));
                state=D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER;
            }
            else if (b.type==D3D12_DESCRIPTOR_RANGE_TYPE_UAV)
            {
                Require((resource->desc.Flags&D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS)!=0);
                state=D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            }
            else if (b.stage==5) state=D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            if (b.type!=D3D12_DESCRIPTOR_RANGE_TYPE_CBV)
            {
                if (buffer)
                {
                    bool raw=reflected.inputType==D3D_SIT_BYTEADDRESS || reflected.inputType==D3D_SIT_UAV_RWBYTEADDRESS;
                    Require(!raw || found->offset%D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT==0);
                    UINT stride=raw ? 4 : found->stride;
                    Require(b.dimension==D3D_SRV_DIMENSION_BUFFER || b.dimension==D3D_SRV_DIMENSION_BUFFEREX);
                    Require(stride && found->bytes && found->offset%stride==0 && found->bytes%stride==0 &&
                        found->bytes/stride<=UINT_MAX && Fits(found->offset,found->bytes,resource->desc.Width));
                    // Typed Buffer<T> needs format-size validation beyond the raw/structured path.
                    Require(raw || reflected.inputType==D3D_SIT_STRUCTURED || reflected.inputType==D3D_SIT_UAV_RWSTRUCTURED);
                }
                else
                {
                    Require(b.dimension!=D3D_SRV_DIMENSION_BUFFER && b.dimension!=D3D_SRV_DIMENSION_BUFFEREX);
                }
            }
            if (buffer)
            {
                auto [entry,inserted]=states.emplace(resource.get(),std::make_pair(resource,state));
                if (!inserted)
                {
                    auto& combined=entry->second.second;
                    if (combined == D3D12_RESOURCE_STATE_UNORDERED_ACCESS || state == D3D12_RESOURCE_STATE_UNORDERED_ACCESS)
                    {
                        combined = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
                    }
                    else
                    {
                        combined |= state;
                    }
                }
            }
            else
            {
                viewStates.push_back({GetTextureView(found->resource), state});
            }
        }
    }
    UINT viewCount=0,samplerCount=0;
    for (UINT i=0;i<sizes.size();++i) (p->tables[i].second ? samplerCount : viewCount)+=sizes[i];
    // Allocate all tables of each type together: a draw can bind only one
    // shader-visible heap of each type, even when the pool has many pages.
    auto views=AllocateDescriptors(false,viewCount);
    DescriptorSlice samplers{};
    std::vector<D3D12_SAMPLER_DESC> samplerDescriptors;
    if (samplerCount)
    {
        samplerDescriptors.reserve(samplerCount);
        for (const auto* binding : matched)
            if (binding->type == D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER)
                samplerDescriptors.push_back(binding->sampler);

        auto& groups = frame.samplerGroups;
        auto existing = std::find_if(groups.begin(), groups.end(), [&](const auto& group)
        {
            return group.descriptors.size() == samplerDescriptors.size() &&
                std::memcmp(group.descriptors.data(), samplerDescriptors.data(),
                    samplerDescriptors.size() * sizeof(D3D12_SAMPLER_DESC)) == 0;
        });
        if (existing != groups.end())
        {
            samplers = {existing->heap, existing->offset};
        }
        else
        {
            samplers = AllocateDescriptors(true,samplerCount);
            auto cpu = samplers.heap->GetCPUDescriptorHandleForHeapStart();
            cpu.ptr += static_cast<SIZE_T>(samplers.offset) * samplerStride;
            for (const auto& descriptor : samplerDescriptors)
            {
                device->CreateSampler(&descriptor,cpu);
                cpu.ptr += samplerStride;
            }
            groups.push_back({std::move(samplerDescriptors), samplers.heap, samplers.offset});
        }
    }
    std::vector<std::shared_ptr<Resource>> writes;
    for (const auto& [pointer,entry] : states)
    {
        const auto& resource=entry.first;
        auto state=entry.second;
        if (state==D3D12_RESOURCE_STATE_UNORDERED_ACCESS) { Transition(resource,state); writes.push_back(resource); }
        else ReadTransition(resource,state);
    }
    for (const auto& [view, state] : viewStates)
    {
        if (state == D3D12_RESOURCE_STATE_UNORDERED_ACCESS)
        {
            TransitionView(view, state);
            writes.push_back(view.resource);
        }
        else
        {
            ReadTransitionView(view, state);
        }
    }
    if(frame.retainedRoots.insert(p->root.Get()).second) frame.roots.push_back(p->root);
    std::vector<D3D12_CPU_DESCRIPTOR_HANDLE> starts;
    std::vector<D3D12_GPU_DESCRIPTOR_HANDLE> gpuStarts;
    for (UINT table=0;table<sizes.size();++table)
    {
        bool sampler=p->tables[table].second!=0;
        auto heap=sampler ? samplers.heap : views.heap;
        UINT stride=sampler ? samplerStride : srvStride;
        UINT index=sampler ? samplers.offset : views.offset;
        (sampler ? samplers.offset : views.offset)+=sizes[table];
        auto cpu=heap->GetCPUDescriptorHandleForHeapStart(); cpu.ptr+=static_cast<SIZE_T>(index)*stride;
        auto gpu=heap->GetGPUDescriptorHandleForHeapStart(); gpu.ptr+=static_cast<UINT64>(index)*stride;
        starts.push_back(cpu);
        gpuStarts.push_back(gpu);
    }
    size_t match=0;
    for (const auto& reflected : p->bindings)
        for (UINT element=0;element<reflected.info.count;++element)
        {
            const auto& b=*matched[match++];
            auto cpu=starts[reflected.table];
            cpu.ptr+=static_cast<SIZE_T>(reflected.tableOffset+element)*(b.type==3 ? samplerStride : srvStride);
            if (b.type==3) { continue; }
            auto resource=GetResource(b.resource);
            if (b.type==2)
            {
                UINT64 offset=b.offset;
                UINT64 alignedSize = Align(std::max<UINT64>(b.bytes, 256ULL), 256ULL);
                if (offset % 256 != 0 || b.bytes % 256 != 0 || offset + alignedSize > resource->desc.Width)
                {
                    // GAL byte ranges need not satisfy D3D12 CBV alignment.
                    // Copy the requested range into an aligned GPU snapshot;
                    // never expose unrelated bytes beyond the guest range.
                    auto snapshotDesc = BufferDesc(alignedSize);
                    snapshotDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
                    auto snapshot = Allocate(snapshotDesc);
                    frame.retained.push_back(snapshot);
                    auto zero = Upload(snapshot->desc.Width, 256);
                    memset(zero.mapped, 0, static_cast<size_t>(snapshot->desc.Width));
                    Transition(snapshot, D3D12_RESOURCE_STATE_COPY_DEST);
                    commands->CopyBufferRegion(snapshot->object.Get(), 0, zero.object, zero.offset, snapshot->desc.Width);
                    UINT64 copyBytes = 0;
                    if (b.offset < resource->desc.Width)
                    {
                        copyBytes = std::min<UINT64>(b.bytes, resource->desc.Width - b.offset);
                    }
                    if (copyBytes > 0)
                    {
                        if ((b.offset | copyBytes) & 3)
                        {
                            auto previous = resource->state;
                            ByteCopy(resource, snapshot, b.offset, 0, copyBytes);
                            Transition(resource, previous);
                        }
                        else
                        {
                            auto previous = resource->state;
                            Transition(resource, D3D12_RESOURCE_STATE_COPY_SOURCE);
                            commands->CopyBufferRegion(snapshot->object.Get(), 0, resource->object.Get(), b.offset, copyBytes);
                            Transition(resource, previous);
                        }
                    }
                    Transition(snapshot, D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
                    resource = snapshot; offset = 0;
                }
                D3D12_CONSTANT_BUFFER_VIEW_DESC desc{resource->object->GetGPUVirtualAddress()+offset,static_cast<UINT>(alignedSize)};
                device->CreateConstantBufferView(&desc,cpu);
            }
            else if (b.type==0)
            {
                D3D12_SHADER_RESOURCE_VIEW_DESC desc{};
                // A GAL texture view may expose BGR, forced zero/one, or any
                // other valid D3D12 component permutation without copying the
                // texture. Buffers retain the standard identity mapping.
                // Some legacy Intel D3D12 drivers crash in CreateShaderResourceView
                // when a texture SRV carries a non-default component mapping.
                // Keep this opt-in desktop compatibility escape hatch scoped to
                // the direct-host harness; a real renderer can retain the native
                // mapping or provide shader-side swizzle emulation.
                char disableComponentSwizzle[2]{};
                bool useDefaultComponentMapping = GetEnvironmentVariableA(
                    "RYUJINX_UWP_D3D12_DISABLE_COMPONENT_SWIZZLE",
                    disableComponentSwizzle,
                    static_cast<DWORD>(sizeof(disableComponentSwizzle))) != 0 &&
                    disableComponentSwizzle[0] == '1';
                UINT mapping = b.componentMapping;
                if (resource->desc.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER && !mapping)
                {
                    auto view = GetTextureView(b.resource);
                    mapping = view.componentMapping;
                }
                desc.Shader4ComponentMapping = (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER ||
                    !mapping || useDefaultComponentMapping)
                    ? D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING : mapping;
                if (resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_BUFFER)
                {
                    bool raw=reflected.inputType==D3D_SIT_BYTEADDRESS;
                    UINT stride=raw ? 4 : b.stride;
                    desc.Format=raw ? DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT_UNKNOWN;
                    desc.ViewDimension=D3D12_SRV_DIMENSION_BUFFER;
                    desc.Buffer={b.offset/stride,static_cast<UINT>(b.bytes/stride),raw ? 0u : stride,
                        raw ? D3D12_BUFFER_SRV_FLAG_RAW : D3D12_BUFFER_SRV_FLAG_NONE};
                }
                else
                {
                    auto view=GetTextureView(b.resource);
                    desc.Format=GetDefaultSrvFormat(view.format!=DXGI_FORMAT_UNKNOWN ? view.format : resource->desc.Format, resource->sampleFormat);
                    if (resource->desc.SampleDesc.Count > 1)
                    {
                        if (reflected.info.dimension == D3D_SRV_DIMENSION_TEXTURE2DMSARRAY ||
                            view.sliceCount > 1 || resource->desc.DepthOrArraySize > 1)
                        {
                            desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DMSARRAY;
                            desc.Texture2DMSArray.FirstArraySlice = view.firstSlice;
                            desc.Texture2DMSArray.ArraySize = view.sliceCount;
                        }
                        else
                        {
                            desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DMS;
                        }
                    }
                    else if (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D)
                    {
                        desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE3D;
                        desc.Texture3D.MostDetailedMip = view.firstMip;
                        desc.Texture3D.MipLevels = view.mipCount;
                        desc.Texture3D.ResourceMinLODClamp = 0;
                    }
                    else if (reflected.info.dimension == D3D_SRV_DIMENSION_TEXTURECUBE)
                    {
                        desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURECUBE;
                        desc.TextureCube.MostDetailedMip = view.firstMip;
                        desc.TextureCube.MipLevels = view.mipCount;
                        desc.TextureCube.ResourceMinLODClamp = 0;
                    }
                    else if (reflected.info.dimension == D3D_SRV_DIMENSION_TEXTURECUBEARRAY)
                    {
                        desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURECUBEARRAY;
                        desc.TextureCubeArray.MostDetailedMip = view.firstMip;
                        desc.TextureCubeArray.MipLevels = view.mipCount;
                        desc.TextureCubeArray.First2DArrayFace = view.firstSlice;
                        desc.TextureCubeArray.NumCubes = std::max(1u, view.sliceCount / 6);
                        desc.TextureCubeArray.ResourceMinLODClamp = 0;
                    }
                    else if (resource->desc.SampleDesc.Count > 1)
                    {
                        if (reflected.info.dimension == D3D_SRV_DIMENSION_TEXTURE2DMSARRAY ||
                            (view.sliceCount > 1 || resource->desc.DepthOrArraySize > 1))
                        {
                            desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DMSARRAY;
                            desc.Texture2DMSArray.FirstArraySlice = view.firstSlice;
                            desc.Texture2DMSArray.ArraySize = view.sliceCount;
                        }
                        else
                        {
                            desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DMS;
                        }
                    }
                    else if (reflected.info.dimension == D3D_SRV_DIMENSION_TEXTURE2DARRAY ||
                             (reflected.info.dimension != D3D_SRV_DIMENSION_TEXTURE2D &&
                              (view.sliceCount > 1 || resource->desc.DepthOrArraySize > 1)))
                    {
                        desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DARRAY;
                        desc.Texture2DArray.MostDetailedMip = view.firstMip;
                        desc.Texture2DArray.MipLevels = view.mipCount;
                        desc.Texture2DArray.FirstArraySlice = view.firstSlice;
                        desc.Texture2DArray.ArraySize = view.sliceCount;
                        desc.Texture2DArray.PlaneSlice = (desc.Format == DXGI_FORMAT_X24_TYPELESS_G8_UINT || desc.Format == DXGI_FORMAT_X32_TYPELESS_G8X24_UINT) ? 1 : 0;
                        desc.Texture2DArray.ResourceMinLODClamp = 0;
                    }
                    else
                    {
                        desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                        desc.Texture2D.MostDetailedMip = view.firstMip;
                        desc.Texture2D.MipLevels = view.mipCount;
                        desc.Texture2D.PlaneSlice = (desc.Format == DXGI_FORMAT_X24_TYPELESS_G8_UINT || desc.Format == DXGI_FORMAT_X32_TYPELESS_G8X24_UINT) ? 1 : 0;
                        desc.Texture2D.ResourceMinLODClamp = 0;
                    }
                }
                device->CreateShaderResourceView(resource->object.Get(),&desc,cpu);
            }
            else
            {
                D3D12_UNORDERED_ACCESS_VIEW_DESC desc{};
                if(resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_BUFFER)
                {
                    bool raw=reflected.inputType==D3D_SIT_UAV_RWBYTEADDRESS;
                    UINT stride=raw ? 4 : b.stride;
                    desc.Format=raw ? DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT_UNKNOWN;
                    desc.ViewDimension=D3D12_UAV_DIMENSION_BUFFER;
                    desc.Buffer={b.offset/stride,static_cast<UINT>(b.bytes/stride),raw ? 0u : stride,0,
                        raw ? D3D12_BUFFER_UAV_FLAG_RAW : D3D12_BUFFER_UAV_FLAG_NONE};
                }
                else
                {
                    auto view=GetTextureView(b.resource);
                    DXGI_FORMAT uavFormat = (view.format!=DXGI_FORMAT_UNKNOWN && !IsTypelessFormat(view.format))
                        ? view.format
                        : (resource->sampleFormat!=DXGI_FORMAT_UNKNOWN ? resource->sampleFormat : GetDefaultTypedFormat(resource->desc.Format));
                    if (uavFormat == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) uavFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
                    else if (uavFormat == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) uavFormat = DXGI_FORMAT_B8G8R8A8_UNORM;
                    desc.Format=uavFormat;
                    if(resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_TEXTURE3D)
                    {
                        desc.ViewDimension=D3D12_UAV_DIMENSION_TEXTURE3D;
                        desc.Texture3D.MipSlice=view.firstMip;
                        desc.Texture3D.FirstWSlice=view.firstSlice;
                        desc.Texture3D.WSize=view.sliceCount;
                    }
                    else if (view.sliceCount > 1 || view.firstSlice > 0 || resource->desc.DepthOrArraySize > 1)
                    {
                        desc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2DARRAY;
                        desc.Texture2DArray.MipSlice = view.firstMip;
                        desc.Texture2DArray.FirstArraySlice = view.firstSlice;
                        desc.Texture2DArray.ArraySize = view.sliceCount;
                        desc.Texture2DArray.PlaneSlice = 0;
                    }
                    else
                    {
                        desc.ViewDimension=D3D12_UAV_DIMENSION_TEXTURE2D;
                        desc.Texture2D.MipSlice=view.firstMip;
                        desc.Texture2D.PlaneSlice = 0;
                    }
                }
                device->CreateUnorderedAccessView(resource->object.Get(),nullptr,&desc,cpu);
            }
        }
    // CBV snapshots may dispatch ByteCopy, changing the compute root signature
    // and the command-list PSO. Bind guest tables only after all utility work.
    BindDescriptorHeaps(views.heap,samplers.heap);
    if (compute) commands->SetComputeRootSignature(p->root.Get()); else commands->SetGraphicsRootSignature(p->root.Get());
    for (UINT table=0;table<gpuStarts.size();++table)
        if (compute) commands->SetComputeRootDescriptorTable(table,gpuStarts[table]);
        else commands->SetGraphicsRootDescriptorTable(table,gpuStarts[table]);
    return writes;
}

void FinishWrites(const std::vector<std::shared_ptr<Resource>>& writes)
{
    for (const auto& resource : writes)
    {
        D3D12_RESOURCE_BARRIER barrier{}; barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource=resource->object.Get(); commands->ResourceBarrier(1,&barrier);
        ++performanceStats.uavBarriers;
    }
}

void DispatchProgram(UINT64 id, const RyuboxD12Binding* bindings, UINT count, UINT x, UINT y, UINT z)
{
    auto p=GetProgram(id); Require(p->compute && x && y && z && x<=65535 && y<=65535 && z<=65535);
    auto writes=BindProgram(p,bindings,count);
    auto& frame=frames[frameIndex];
    if(frame.retainedPipelines.insert(p->compute.Get()).second) frame.pipelines.push_back(p->compute);
    commands->SetPipelineState(p->compute.Get());
    if(p->dispatchRoot!=UINT_MAX)
    {
        UINT dimensions[]={x,y,z};
        commands->SetComputeRoot32BitConstants(p->dispatchRoot,3,dimensions,0);
    }
    commands->Dispatch(x,y,z); FinishWrites(writes);
}

void ConfigureStreamOutput(UINT64 id,UINT stage,const RyuboxD12SOElement* data,UINT count)
{
    auto p=GetProgram(id);
    Require(!p->compute && p->graphics.empty() && p->streamOutputs.empty() && data && count && count<=128);
    UINT lastStage=p->shaders[4] ? 4 : p->shaders[3] ? 3 : 1;
    Require(stage==lastStage);
    ComPtr<ID3D12ShaderReflection> reflection;
    auto blob=p->shaders[stage];
    Check(D3DReflect(blob->GetBufferPointer(),blob->GetBufferSize(),IID_PPV_ARGS(&reflection)));
    D3D12_SHADER_DESC desc{}; Check(reflection->GetDesc(&desc));
    std::vector<RyuboxD12SOElement> elements(data,data+count);
    std::sort(elements.begin(),elements.end(),[](const auto& a,const auto& b) {
        return a.buffer!=b.buffer ? a.buffer<b.buffer : a.offset<b.offset;
    });
    UINT ends[4]{},strides[4]{},streams[4]{UINT_MAX,UINT_MAX,UINT_MAX,UINT_MAX};
    for (auto& e:elements)
    {
        Require(memchr(e.semantic,0,sizeof(e.semantic)) && e.semantic[0] && e.buffer<4 && e.stream<4 &&
            e.components && e.components<=4 && e.component<4 && e.components<=4-e.component && e.stride && e.stride<=2048 &&
            e.stride%4==0 && e.offset%4==0 && Fits(e.offset,e.components*4,e.stride));
        Require(e.offset>=ends[e.buffer] && (!strides[e.buffer] || strides[e.buffer]==e.stride) &&
            (streams[e.buffer]==UINT_MAX || streams[e.buffer]==e.stream));
        bool found=false;
        for(UINT i=0;i<desc.OutputParameters;++i)
        {
            D3D12_SIGNATURE_PARAMETER_DESC o{}; Check(reflection->GetOutputParameterDesc(i,&o));
            // Reflection masks are register lanes, whereas SO components are
            // relative to the semantic. FXC can pack e.g. a scalar into lane Y.
            UINT semanticMask=o.Mask;
            while (semanticMask && !(semanticMask&1u)) semanticMask>>=1;
            UINT mask=((1u<<e.components)-1)<<e.component;
            if(_stricmp(o.SemanticName,e.semantic)==0 && o.SemanticIndex==e.index && o.Stream==e.stream && (semanticMask&mask)==mask)
                found=true;
        }
        if (!found)
        {
            programError="Stream-output signature mismatch: stage="+std::to_string(stage)+
                " semantic="+std::string(e.semantic)+std::to_string(e.index)+
                " component="+std::to_string(e.component)+" count="+std::to_string(e.components)+
                " stream="+std::to_string(e.stream)+"; reflected outputs:";
            for (UINT i=0;i<desc.OutputParameters;++i)
            {
                D3D12_SIGNATURE_PARAMETER_DESC o{}; Check(reflection->GetOutputParameterDesc(i,&o));
                programError+=" "+std::string(o.SemanticName)+std::to_string(o.SemanticIndex)+
                    " mask="+std::to_string(o.Mask)+" stream="+std::to_string(o.Stream);
            }
            Require(false,E_INVALIDARG,programError);
        }
        ends[e.buffer]=e.offset+e.components*4; strides[e.buffer]=e.stride; streams[e.buffer]=e.stream;
    }
    p->streamOutputs=std::move(elements);
}

ComPtr<ID3DBlob> HullForPatch(const std::shared_ptr<Program>& p, UINT count)
{
    Require(count>=1 && count<=32);
    if(p->hullSource.empty() || count==32) return p->shaders[2];
    auto found=p->hullVariants.find(count);
    if(found!=p->hullVariants.end()) return found->second;
    std::string number=std::to_string(count);
    D3D_SHADER_MACRO defines[]={{"RYUBOX_HULL_INPUT_POINTS",number.c_str()},{nullptr,nullptr}};
    ComPtr<ID3DBlob> blob,error;
    HRESULT hr=D3DCompile(p->hullSource.data(),p->hullSource.size(),"RyuboxD12HullVariant",defines,nullptr,
        "main","hs_5_1",GetShaderCompileFlags(),0,&blob,&error);
    if(FAILED(hr) && error) programError.assign(static_cast<const char*>(error->GetBufferPointer()),error->GetBufferSize());
    Require(SUCCEEDED(hr));
    // Specializing the input signature must not introduce a binding absent
    // from the program's reflected root layout. Fail before recording a draw,
    // rather than binding an unrelated or uninitialized descriptor.
    ComPtr<ID3D12ShaderReflection> reflection;
    Check(D3DReflect(blob->GetBufferPointer(),blob->GetBufferSize(),IID_PPV_ARGS(&reflection)));
    D3D12_SHADER_DESC desc{}; Check(reflection->GetDesc(&desc));
    for(UINT i=0;i<desc.BoundResources;++i)
    {
        D3D12_SHADER_INPUT_BIND_DESC b{}; Check(reflection->GetResourceBindingDesc(i,&b));
        bool compatible=false;
        for(const auto& existing:p->bindings)
            if(existing.info.stage==2 && existing.inputType==b.Type && existing.info.binding==b.BindPoint &&
                existing.info.space==b.Space && existing.info.count>=b.BindCount && existing.info.dimension==static_cast<UINT>(b.Dimension))
                compatible=true;
        if(!compatible) programError="Hull patch specialization changed the reflected resource layout; shader relinking is required.";
        Require(compatible);
    }
    p->hullVariants.emplace(count,blob);
    return blob;
}

void DrawProgram(UINT64 id, const RyuboxD12Binding* bindings, UINT bindingCount, const RyuboxD12Draw& request,
    const RyuboxD12DepthState* depthState=nullptr)
{
    LARGE_INTEGER recordStart{};
    QueryPerformanceCounter(&recordStart);
    ++performanceStats.drawCalls;
    TraceD3D12("draw:begin");
    programError.clear();
    auto p=GetProgram(id);
    auto draw=request;
    auto RequireDraw=[&](bool condition,const char* rule)
    {
        if (!condition)
        {
            programError="Invalid D3D12 draw preflight: "+std::string(rule)+
                "; colorTargetCount="+std::to_string(draw.colorTargetCount)+
                ", colorTargetMask=0x"+std::to_string(draw.colorTargetMask)+
                ", topology="+std::to_string(draw.topology)+
                ", attributes="+std::to_string(draw.attributeCount)+
                ", vertices="+std::to_string(draw.vertexCount)+
                ", bindings="+std::to_string(bindingCount)+
                ", rasterizerDiscard="+std::to_string(draw.rasterizerDiscard)+
                ", recording="+std::to_string(recording)+
                ", programCompute="+std::to_string(p->compute ? 1 : 0);
            Require(false);
        }
    };
    RequireDraw(draw.colorTargetCount<=8,"color target count exceeds 8");
    RequireDraw((draw.colorTargetMask & ~((1u << draw.colorTargetCount) - 1u)) == 0,"color-target mask exceeds count");
    // D3D12 OM descriptors are positional. A completely unset, implicit single target
    // is the presentation backbuffer. Sparse explicit MRT masks bind null handles for empty slots.
    RequireDraw(draw.colorTargetMask != 0 || draw.colorTargetCount <= 1, "unmasked multi-target draw");
    RequireDraw((draw.rasterizerDiscard & ~3u)==0,"invalid rasterizer-discard mask");
    RequireDraw(!draw.streamOutput || !p->streamOutputs.empty(),"stream output requested without program declarations");
    RequireDraw(recording && !p->compute && (!bindingCount || bindings) && draw.attributeCount<=32 && draw.vertexCount<=32 &&
        (!draw.attributeCount || draw.attributes) && (!draw.vertexCount || draw.vertices),"invalid command-list, program, binding, attribute, or vertex input");
    D3D12_PRIMITIVE_TOPOLOGY_TYPE topology;
    switch (draw.topology)
    {
    case D3D_PRIMITIVE_TOPOLOGY_POINTLIST: topology=D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT; break;
    case D3D_PRIMITIVE_TOPOLOGY_LINELIST: case D3D_PRIMITIVE_TOPOLOGY_LINESTRIP:
    case D3D_PRIMITIVE_TOPOLOGY_LINELIST_ADJ: case D3D_PRIMITIVE_TOPOLOGY_LINESTRIP_ADJ:
        topology=D3D12_PRIMITIVE_TOPOLOGY_TYPE_LINE; break;
    case D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST: case D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP:
    case D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST_ADJ: case D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP_ADJ:
        topology=D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE; break;
    default:
        Require(draw.topology>=D3D_PRIMITIVE_TOPOLOGY_1_CONTROL_POINT_PATCHLIST && draw.topology<=D3D_PRIMITIVE_TOPOLOGY_32_CONTROL_POINT_PATCHLIST);
        topology=D3D12_PRIMITIVE_TOPOLOGY_TYPE_PATCH;
    }
    Require((topology==D3D12_PRIMITIVE_TOPOLOGY_TYPE_PATCH)==bool(p->shaders[2]));
    bool polygons=topology==D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    if(p->shaders[2])
        polygons=p->tessellationOutput==D3D_TESSELLATOR_OUTPUT_TRIANGLE_CW ||
                 p->tessellationOutput==D3D_TESSELLATOR_OUTPUT_TRIANGLE_CCW;
    if(p->shaders[4])
        polygons=p->geometryOutput==D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP;
    draw.rasterizerDiscard=((draw.rasterizerDiscard & 1u) ||
        ((draw.rasterizerDiscard & 2u) && polygons)) ? 1u : 0u;
    std::shared_ptr<Resource> depthTarget;
    D3D12_DEPTH_STENCIL_DESC depthDesc{};
    depthDesc.DepthFunc=D3D12_COMPARISON_FUNC_ALWAYS;
    depthDesc.FrontFace=depthDesc.BackFace={D3D12_STENCIL_OP_KEEP,D3D12_STENCIL_OP_KEEP,D3D12_STENCIL_OP_KEEP,D3D12_COMPARISON_FUNC_ALWAYS};
    bool noColorTarget = depthState && depthState->noColorTarget != 0;
    if(noColorTarget) Require(depthState->target && !draw.colorTargetCount);
    UINT colorTargetCount=draw.colorTargetCount;
    if(!noColorTarget && !draw.rasterizerDiscard) Require(colorTargetCount>0);
    auto ColorTarget=[&](UINT index) -> std::shared_ptr<Resource>
    {
        Require(index<colorTargetCount);
        UINT64 id=draw.colorTargets[index];
        // A completely unset, implicit single target is the presentation
        // backbuffer. Explicit GAL MRT arrays carry a mask, so a null slot is
        // not accidentally rebound to that backbuffer and SV_TargetN keeps
        // its guest slot number.
        if (!draw.colorTargetMask && colorTargetCount == 1)
            return targets[targetIndex];
        if ((draw.colorTargetMask & (1u << index)) == 0)
            return {};
        Require(id != 0);
        auto resource=GetResource(id);
        Require(resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
            resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_TEXTURE3D);
        return resource;
    };
    if(depthState && depthState->target && !draw.rasterizerDiscard)
    {
        Require(depthState->enable<=1 && depthState->write<=1 && depthState->comparison>=1 && depthState->comparison<=8);
        depthTarget=Get(depthState->target,D3D12_RESOURCE_DIMENSION_TEXTURE2D);
        Require(depthTarget->depthFormat!=DXGI_FORMAT_UNKNOWN);
        if(!noColorTarget)
        {
            // D3D12 hardware allows color and depth render targets with different dimensions;
            // the GPU renders into the intersection of their extents. Some titles (e.g. Pokémon
            // Let's Go) intentionally bind a smaller depth buffer alongside a full-resolution
            // color target. Skip the strict match check — the viewport already limits the
            // rendering area so no out-of-bounds writes occur.
            for(UINT i=0;i<colorTargetCount;++i)
            {
                auto color=ColorTarget(i);
                if (!color) continue;
                // Tolerate dimension mismatch: continue rather than Require-failing.
                // The draw will be clipped to min(color, depth) by the viewport/scissor.
                (void)color; // suppress unused-variable warning in release builds
            }
        }
        // Writable DSV/SRV aliasing is rejected before any draw state changes.
        for(UINT i=0;i<bindingCount;++i) if(bindings[i].type!=3) Require(bindings[i].resource!=depthState->target);
        depthDesc.DepthEnable=depthState->enable;
        depthDesc.DepthWriteMask=depthState->enable && depthState->write ? D3D12_DEPTH_WRITE_MASK_ALL : D3D12_DEPTH_WRITE_MASK_ZERO;
        depthDesc.DepthFunc=static_cast<D3D12_COMPARISON_FUNC>(depthState->comparison);
        Require(depthState->stencilEnable<=1);
        if(depthState->stencilEnable)
        {
            Require(depthTarget->depthFormat==DXGI_FORMAT_D24_UNORM_S8_UINT || depthTarget->depthFormat==DXGI_FORMAT_D32_FLOAT_S8X24_UINT);
            auto validOp=[](UINT value) { return value>=1 && value<=8; };
            auto validFunc=[](UINT value) { return value>=1 && value<=8; };
            Require(validOp(depthState->frontFail) && validOp(depthState->frontPass) && validOp(depthState->frontDepthFail) &&
                validFunc(depthState->frontFunc) && depthState->frontRef<=255 && depthState->frontReadMask<=255 && depthState->frontWriteMask<=255 &&
                validOp(depthState->backFail) && validOp(depthState->backPass) && validOp(depthState->backDepthFail) &&
                validFunc(depthState->backFunc) && depthState->backRef<=255 && depthState->backReadMask<=255 && depthState->backWriteMask<=255);
            depthDesc.StencilEnable=TRUE;
            depthDesc.StencilReadMask=static_cast<UINT8>(depthState->frontReadMask);
            depthDesc.StencilWriteMask=static_cast<UINT8>(depthState->frontWriteMask);
            depthDesc.FrontFace={static_cast<D3D12_STENCIL_OP>(depthState->frontFail),static_cast<D3D12_STENCIL_OP>(depthState->frontDepthFail),static_cast<D3D12_STENCIL_OP>(depthState->frontPass),static_cast<D3D12_COMPARISON_FUNC>(depthState->frontFunc)};
            depthDesc.BackFace={static_cast<D3D12_STENCIL_OP>(depthState->backFail),static_cast<D3D12_STENCIL_OP>(depthState->backDepthFail),static_cast<D3D12_STENCIL_OP>(depthState->backPass),static_cast<D3D12_COMPARISON_FUNC>(depthState->backFunc)};
        }
    }
    Require(std::isfinite(draw.viewport.TopLeftX) && std::isfinite(draw.viewport.TopLeftY) &&
        std::isfinite(draw.viewport.Width) && std::isfinite(draw.viewport.Height) && draw.viewport.Width>0 && draw.viewport.Height>0 &&
        draw.viewport.MinDepth>=0 && draw.viewport.MaxDepth<=1 && draw.viewport.MinDepth<=draw.viewport.MaxDepth);
    D3D12_BLEND_DESC blendDesc = draw.blend;
    for(UINT i=0;i<8;++i) if (blendDesc.RenderTarget[i].LogicOpEnable)
    {
        Require(logicOpSupported, DXGI_ERROR_UNSUPPORTED);
        blendDesc.RenderTarget[i].BlendEnable = FALSE;
    }
    std::string key;
    auto append=[&](const auto& value) { key.append(reinterpret_cast<const char*>(&value),sizeof(value)); };
    std::array<DXGI_FORMAT,8> targetFormats{};
    if(!draw.rasterizerDiscard && !noColorTarget)
    {
        for(UINT i=0;i<colorTargetCount;++i)
        {
            auto target=ColorTarget(i);
            if (!target) continue;
            Require((target->desc.Flags&D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET)!=0);
            for(UINT j=0;j<i;++j) if(draw.colorTargetMask & (1u << j)) Require(draw.colorTargets[j]!=draw.colorTargets[i]);
            UINT64 targetId = (!draw.colorTargetMask && colorTargetCount == 1) ? 0 : draw.colorTargets[i];
            targetFormats[i] = ResolveRenderTargetFormat(targetId);
        }
    }
    // D3D12 requires a DSV to cover every bound RTV (the only permitted size
    // difference is a larger DSV). Guest state can temporarily pair a
    // lower-resolution depth surface with a full-resolution color surface.
    // Binding that pair is invalid and poisons the command list. Until a
    // matching depth proxy/resolve exists, preserve the color draw and detach
    // depth for this one draw rather than submitting an illegal OM state.
    if (depthTarget && !noColorTarget && !draw.rasterizerDiscard)
    {
        TextureView depthView = GetTextureView(depthState->target);
        UINT depthWidth = MipSize(static_cast<UINT>(depthTarget->desc.Width), depthView.firstMip);
        UINT depthHeight = MipSize(depthTarget->desc.Height, depthView.firstMip);
        bool compatible = true;

        for (UINT i = 0; i < colorTargetCount && compatible; ++i)
        {
            auto color = ColorTarget(i);
            if (!color) continue;
            UINT64 targetId = (!draw.colorTargetMask && colorTargetCount == 1) ? 0 : draw.colorTargets[i];
            TextureView colorView = targetId ? GetTextureView(targetId) : TextureView{};
            UINT colorWidth = MipSize(static_cast<UINT>(color->desc.Width), colorView.firstMip);
            UINT colorHeight = MipSize(color->desc.Height, colorView.firstMip);
            compatible = color->desc.Dimension == depthTarget->desc.Dimension &&
                color->desc.SampleDesc.Count == depthTarget->desc.SampleDesc.Count &&
                color->desc.SampleDesc.Quality == depthTarget->desc.SampleDesc.Quality &&
                depthWidth >= colorWidth && depthHeight >= colorHeight;
        }

        if (!compatible)
        {
            ++performanceStats.depthAttachmentsDetached;
            ++performanceStats.invalidRenderPasses;
            depthTarget.reset();
            depthDesc = {};
        }
    }
    UINT sampleCount = 1;
    bool sampleCountSet = false;
    auto CheckSamples = [&](const std::shared_ptr<Resource>& target)
    {
        if (!target) return;
        UINT count = target->desc.SampleDesc.Count;
        if (!sampleCountSet) { sampleCount = count; sampleCountSet = true; }
        else Require(sampleCount == count, E_INVALIDARG);
    };
    if (!draw.rasterizerDiscard && !noColorTarget)
        for (UINT i=0; i<colorTargetCount; ++i) CheckSamples(ColorTarget(i));
    CheckSamples(depthTarget);
    append(colorTargetCount); append(targetFormats); append(sampleCount);
    DXGI_FORMAT depthFormat=depthTarget ? depthTarget->depthFormat : DXGI_FORMAT_UNKNOWN;
    append(depthFormat); append(depthDesc);
    append(topology); append(draw.rasterizer); append(blendDesc); append(draw.rasterizerDiscard); append(draw.streamOutput);
    if(topology==D3D12_PRIMITIVE_TOPOLOGY_TYPE_PATCH) append(draw.topology);
    for (UINT i=0;i<draw.attributeCount;++i)
    {
        const auto& a=draw.attributes[i];
        Require(a.SemanticName && a.InputSlot<draw.vertexCount && strlen(a.SemanticName)<256);
        key.append(a.SemanticName); key.push_back('\0'); append(a.SemanticIndex); append(a.Format);
        append(a.InputSlot); append(a.AlignedByteOffset); append(a.InputSlotClass); append(a.InstanceDataStepRate);
    }
    auto found=p->graphics.find(key);
    LARGE_INTEGER psoStart{}, psoEnd{};
    QueryPerformanceCounter(&psoStart);
    if (found==p->graphics.end())
    {
        // Eviction only drops the cache reference; submitted frames retain PSOs.
        if(p->graphics.size()>=256) p->graphics.erase(p->graphics.begin());
        D3D12_GRAPHICS_PIPELINE_STATE_DESC desc{}; desc.pRootSignature=p->root.Get();
        auto code=[&](UINT stage)->D3D12_SHADER_BYTECODE { auto& s=p->shaders[stage]; return s ? D3D12_SHADER_BYTECODE{s->GetBufferPointer(),s->GetBufferSize()} : D3D12_SHADER_BYTECODE{}; };
        desc.VS=code(1); desc.HS=code(2); desc.DS=code(3); desc.GS=code(4); desc.PS=code(5);
        ComPtr<ID3DBlob> hull;
        if(topology==D3D12_PRIMITIVE_TOPOLOGY_TYPE_PATCH)
        {
            hull=HullForPatch(p,draw.topology-D3D_PRIMITIVE_TOPOLOGY_1_CONTROL_POINT_PATCHLIST+1);
            desc.HS={hull->GetBufferPointer(),hull->GetBufferSize()};
        }
        desc.InputLayout={draw.attributes,draw.attributeCount}; desc.RasterizerState=draw.rasterizer;
        desc.BlendState=blendDesc; desc.SampleMask=UINT_MAX;
        desc.DepthStencilState=depthDesc; desc.DSVFormat=depthFormat;
        std::vector<D3D12_SO_DECLARATION_ENTRY> so;
        UINT strides[4]{},ends[4]{};
        if(draw.streamOutput)
        {
            for(const auto& e:p->streamOutputs)
            {
                while(ends[e.buffer]<e.offset)
                {
                    UINT n=std::min(4u,(e.offset-ends[e.buffer])/4);
                    so.push_back({e.stream,nullptr,0,0,static_cast<BYTE>(n),static_cast<BYTE>(e.buffer)});
                    ends[e.buffer]+=n*4;
                }
                so.push_back({e.stream,e.semantic,e.index,static_cast<BYTE>(e.component),static_cast<BYTE>(e.components),static_cast<BYTE>(e.buffer)});
                ends[e.buffer]=e.offset+e.components*4; strides[e.buffer]=e.stride;
            }
            desc.StreamOutput={so.data(),static_cast<UINT>(so.size()),strides,4,0};
        }
        desc.PrimitiveTopologyType=topology; desc.NumRenderTargets=noColorTarget ? 0u : colorTargetCount;
        for(UINT i=0;i<colorTargetCount;++i) desc.RTVFormats[i]=targetFormats[i]; desc.SampleDesc.Count=sampleCount;
        if (draw.rasterizerDiscard)
        {
            // Keep VS/HS/DS/GS execution (including UAV writes) while disabling
            // rasterization. Skipping Draw would lose those guest side effects.
            desc.StreamOutput.RasterizedStream=D3D12_SO_NO_RASTERIZED_STREAM;
            desc.PS={}; desc.NumRenderTargets=0; desc.RTVFormats[0]=DXGI_FORMAT_UNKNOWN;
            desc.BlendState={};
        }
        ComPtr<ID3D12PipelineState> pipeline;
        TraceD3D12("draw:create-pso-begin");
        std::wstring name=PipelineName(*p,"gfx",key);
        HRESULT pipelineResult=E_FAIL;
        if (pipelineLibrary)
        {
            pipelineResult=pipelineLibrary->LoadGraphicsPipeline(name.c_str(),&desc,IID_PPV_ARGS(&pipeline));
            if (SUCCEEDED(pipelineResult)) ++pipelineCacheHits;
        }
        if (FAILED(pipelineResult))
        {
            ++pipelineCreations;
            pipelineResult=device->CreateGraphicsPipelineState(&desc,IID_PPV_ARGS(&pipeline));
            if (SUCCEEDED(pipelineResult) && pipelineLibrary &&
                SUCCEEDED(pipelineLibrary->StorePipeline(name.c_str(),pipeline.Get())))
                pipelineCacheDirty=true;
        }
        TraceD3D12("draw:create-pso-end");
        if(FAILED(pipelineResult))
        {
            char failBuf[256];
            snprintf(failBuf, sizeof(failBuf), "CreateGraphicsPipelineState failed (hr=0x%08X, targets=%u [fmt0=%u], dsv=%u, top=%u).",
                static_cast<UINT>(pipelineResult), desc.NumRenderTargets, static_cast<UINT>(desc.RTVFormats[0]), static_cast<UINT>(desc.DSVFormat), static_cast<UINT>(desc.PrimitiveTopologyType));
            programError = failBuf;
            if(debugQueue)
            {
                UINT64 messages=debugQueue->GetNumStoredMessagesAllowedByRetrievalFilter();
                for(UINT64 i=debugRead;i<messages && programError.size()<12000;++i)
                {
                    SIZE_T size=0;
                    if(FAILED(debugQueue->GetMessage(i,nullptr,&size))) continue;
                    std::vector<BYTE> storage(size);
                    auto message=reinterpret_cast<D3D12_MESSAGE*>(storage.data());
                    if(SUCCEEDED(debugQueue->GetMessage(i,message,&size)) && message->Severity<=D3D12_MESSAGE_SEVERITY_WARNING)
                    { programError+='\n'; programError+=message->pDescription; }
                }
            }
        }
        Check(pipelineResult);
        ++performanceStats.psoCreations;
        found=p->graphics.emplace(std::move(key),pipeline).first;
    }
    else
    {
        ++performanceStats.psoCacheHits;
    }
    QueryPerformanceCounter(&psoEnd);
    performanceStats.psoMicroseconds += ElapsedMicroseconds(psoStart, psoEnd);
    TraceD3D12("draw:pso-ready");
    std::vector<D3D12_VERTEX_BUFFER_VIEW> views;
    // Stream-output destinations cannot simultaneously be shader/IA inputs.
    // Reject the alias before recording ANY transitions for these resources.
    if(draw.streamOutput) for(UINT s=0;s<4;++s)
    {
        const auto& b=draw.streamBuffers[s];
        if(!b.resource) { Require(!b.bytes && !b.counter); continue; }
        Require(b.counter && b.resource!=b.counter && b.resource!=draw.indexResource && b.resource!=draw.indirectResource && b.resource!=draw.countResource &&
            b.counter!=draw.indexResource && b.counter!=draw.indirectResource && b.counter!=draw.countResource);
        for(UINT t=0;t<s;++t) if(draw.streamBuffers[t].resource)
        {
            const auto& previous=draw.streamBuffers[t];
            // GAL/D3D11 permit different SO slots to address disjoint ranges
            // of one buffer. D3D12 supports the same layout through separate
            // SO views. Counters, however, remain exclusive GPU-write state.
            bool rangesDisjoint=b.offset>=previous.offset
                ? b.offset-previous.offset>=previous.bytes
                : previous.offset-b.offset>=b.bytes;
            bool compatible=(b.resource!=previous.resource || rangesDisjoint) &&
                b.counter!=previous.counter && b.resource!=previous.counter && b.counter!=previous.resource;
            if (!compatible)
            {
                std::string detail="stream-output resource/counter alias: slot="+std::to_string(s)+
                    " resource="+std::to_string(b.resource)+" counter="+std::to_string(b.counter)+
                    " offset="+std::to_string(b.offset)+" bytes="+std::to_string(b.bytes)+
                    "; previousSlot="+std::to_string(t)+" resource="+std::to_string(previous.resource)+
                    " counter="+std::to_string(previous.counter)+" offset="+std::to_string(previous.offset)+
                    " bytes="+std::to_string(previous.bytes);
                Require(false,E_INVALIDARG,detail+" disjoint="+std::to_string(rangesDisjoint));
            }
        }
        for(UINT v=0;v<draw.vertexCount;++v) Require(b.resource!=draw.vertices[v].resource && b.counter!=draw.vertices[v].resource);
        for(UINT r=0;r<bindingCount;++r) if(bindings[r].type!=3)
            Require(b.resource!=bindings[r].resource && b.counter!=bindings[r].resource);
        auto dst=Get(b.resource,D3D12_RESOURCE_DIMENSION_BUFFER),counter=Get(b.counter,D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(b.bytes && b.offset%4==0 && b.bytes%4==0 && Fits(b.offset,b.bytes,dst->desc.Width) && counter->desc.Width>=8);
    }
    TraceD3D12("draw:vertices-begin");
    for (UINT i=0;i<draw.vertexCount;++i)
    {
        const auto& v=draw.vertices[i]; auto resource=Get(v.resource,D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(v.bytes && Fits(v.offset,v.bytes,resource->desc.Width));
        ReadTransition(resource,D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
        views.push_back({resource->object->GetGPUVirtualAddress()+v.offset,v.bytes,v.stride});
    }
    TraceD3D12("draw:vertices-end");
    if (draw.indexResource)
    {
        TraceD3D12("draw:index-begin");
        auto index=Get(draw.indexResource,D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(draw.indexFormat==DXGI_FORMAT_R16_UINT || draw.indexFormat==DXGI_FORMAT_R32_UINT);
        UINT stride=draw.indexFormat==DXGI_FORMAT_R16_UINT ? 2 : 4;
        Require(draw.indexOffset%stride==0 && draw.indexBytes%stride==0 && Fits(draw.indexOffset,draw.indexBytes,index->desc.Width));
        if (!draw.indirectResource) Require(Fits(draw.first,draw.count,draw.indexBytes/stride));
        ReadTransition(index,D3D12_RESOURCE_STATE_INDEX_BUFFER);
        D3D12_INDEX_BUFFER_VIEW view{index->object->GetGPUVirtualAddress()+draw.indexOffset,draw.indexBytes,static_cast<DXGI_FORMAT>(draw.indexFormat)};
        commands->IASetIndexBuffer(&view);
        TraceD3D12("draw:index-end");
    }
    else commands->IASetIndexBuffer(nullptr);
    // Reject simultaneous writable shader bindings and IA/indirect use; those
    // require an explicit snapshot, not incompatible D3D12 resource states.
    TraceD3D12("draw:binding-alias-begin");
    auto& frame=frames[frameIndex];
    std::unordered_map<Resource*, std::shared_ptr<Resource>> targetSnapshots;
    std::vector<RyuboxD12Binding> resolvedBindings(bindings, bindings + bindingCount);
    for (UINT i = 0; i < bindingCount; ++i)
    {
        if (draw.rasterizerDiscard || resolvedBindings[i].type == 3) continue;
        for (UINT t = 0; t < colorTargetCount; ++t)
        {
            if ((draw.colorTargetMask & (1u << t)) == 0) continue;
            auto target = ColorTarget(t);
            if (!target) continue;
            auto bRes = GetResource(resolvedBindings[i].resource);
            if (resolvedBindings[i].resource == draw.colorTargets[t] || bRes == target)
            {
                if (resolvedBindings[i].type == 0) // SRV
                {
                    std::shared_ptr<Resource> snapshot;
                    auto sIt = targetSnapshots.find(target.get());
                    if (sIt != targetSnapshots.end())
                    {
                        snapshot = sIt->second;
                    }
                    else
                    {
                        snapshot = Allocate(target->desc);
                        snapshot->sampleFormat = target->sampleFormat;
                        frame.retained.push_back(snapshot);

                        Transition(target, D3D12_RESOURCE_STATE_COPY_SOURCE);
                        Transition(snapshot, D3D12_RESOURCE_STATE_COPY_DEST);
                        commands->CopyResource(snapshot->object.Get(), target->object.Get());
                        Transition(snapshot, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
                        Transition(target, D3D12_RESOURCE_STATE_RENDER_TARGET);

                        targetSnapshots.emplace(target.get(), snapshot);
                    }

                    UINT64 snapshotId = nextResource++;
                    resources.emplace(snapshotId, snapshot);
                    frame.retainedResourceIds.push_back(snapshotId);

                    auto vIt = textureViews.find(resolvedBindings[i].resource);
                    if (vIt != textureViews.end())
                    {
                        TextureView v = vIt->second;
                        v.resource = snapshot;
                        textureViews.emplace(snapshotId, v);
                    }

                    resolvedBindings[i].resource = snapshotId;
                }
            }
        }
    }
    for (UINT i=0;i<bindingCount;++i) if (resolvedBindings[i].type==1)
    {
        Require(resolvedBindings[i].resource!=draw.indexResource && resolvedBindings[i].resource!=draw.indirectResource && resolvedBindings[i].resource!=draw.countResource);
        for (UINT v=0;v<draw.vertexCount;++v) Require(resolvedBindings[i].resource!=draw.vertices[v].resource);
    }
    for (UINT i=0;i<bindingCount;++i) if(!draw.rasterizerDiscard && resolvedBindings[i].type!=3)
        for(UINT t=0;t<colorTargetCount;++t) if(draw.colorTargetMask & (1u << t)) Require(resolvedBindings[i].resource!=draw.colorTargets[t]);
    TraceD3D12("draw:binding-alias-end");
    // Bind the graphics PSO before output-merger state. This order is valid on
    // all D3D12 implementations and avoids a legacy Intel UMD fault observed
    // when OMSetRenderTargets is called after descriptor table setup. Keep the
    // cache entry alive for this submitted frame before handing its native
    // pointer to the command list.
    if(frame.retainedPipelines.insert(found->second.Get()).second) frame.pipelines.push_back(found->second);
    TraceD3D12("draw:pso-bind-begin");
    commands->SetPipelineState(found->second.Get());
    TraceD3D12("draw:pso-bind-end");
    if (draw.rasterizerDiscard)
    {
        commands->OMSetRenderTargets(0,nullptr,FALSE,nullptr);
        defaultTargetBound=false;
    }
    else if(noColorTarget)
    {
        TraceD3D12("draw:dsv-begin");
        TextureView dView = depthState ? GetTextureView(depthState->target) : TextureView{};
        D3D12_CPU_DESCRIPTOR_HANDLE dsv=DepthView(depthTarget, depthState ? &dView : nullptr);
        TraceD3D12("draw:dsv-end");
        commands->OMSetRenderTargets(0,nullptr,FALSE,&dsv);
        defaultTargetBound=false;
    }
    else
    {
        D3D12_CPU_DESCRIPTOR_HANDLE firstRtv{};
        if (!draw.colorTargetMask && colorTargetCount == 1)
        {
            TraceD3D12("draw:rtv-begin");
            firstRtv = TargetView(0);
            TraceD3D12("draw:rtv-end");
        }
        else
        {
            TraceD3D12("draw:rtv-begin");
            auto& count = frames[frameIndex].rtvCount;
            Require(count + colorTargetCount <= DescriptorsPerFrame, E_OUTOFMEMORY);
            auto baseHandle = rtvHeap->GetCPUDescriptorHandleForHeapStart();
            baseHandle.ptr += static_cast<SIZE_T>(FrameCount + frameIndex * DescriptorsPerFrame + count) * rtvStride;
            count += colorTargetCount;
            firstRtv = baseHandle;

            for (UINT i = 0; i < colorTargetCount; ++i)
            {
                D3D12_CPU_DESCRIPTOR_HANDLE slotHandle{baseHandle.ptr + static_cast<SIZE_T>(i) * rtvStride};
                auto target = ColorTarget(i);
                if (target)
                {
                    UINT64 targetId = draw.colorTargets[i];
                    TextureView view{};
                    if (targetId) view = GetTextureView(targetId);
                    Require((target->desc.Flags & D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) != 0);
                    // Preserve the state of mips/layers outside the selected
                    // RTV. Whole-resource transitions lose valid SRV/UAV
                    // state and are especially expensive for 3D textures.
                    if (targetId) TransitionView(view, D3D12_RESOURCE_STATE_RENDER_TARGET);
                    else Transition(target, D3D12_RESOURCE_STATE_RENDER_TARGET);
                    DXGI_FORMAT rtvFormat = ResolveRenderTargetFormat(targetId);
                    if (target->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D)
                    {
                        D3D12_RENDER_TARGET_VIEW_DESC desc{};
                        desc.Format = rtvFormat;
                        desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE3D;
                        desc.Texture3D.MipSlice = view.firstMip;
                        desc.Texture3D.FirstWSlice = view.firstSlice;
                        desc.Texture3D.WSize = view.sliceCount;
                        device->CreateRenderTargetView(target->object.Get(), &desc, slotHandle);
                    }
                    else
                    {
                        D3D12_RENDER_TARGET_VIEW_DESC desc{};
                        desc.Format = rtvFormat;
                        if (target->desc.SampleDesc.Count > 1)
                        {
                            if (target->desc.DepthOrArraySize > 1 || view.firstSlice > 0)
                            {
                                desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2DMSARRAY;
                                desc.Texture2DMSArray.FirstArraySlice = view.firstSlice;
                                desc.Texture2DMSArray.ArraySize = view.sliceCount;
                            }
                            else
                            {
                                desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2DMS;
                            }
                        }
                        else
                        {
                            if (target->desc.DepthOrArraySize > 1 || view.firstSlice > 0)
                            {
                                desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2DARRAY;
                                desc.Texture2DArray.MipSlice = view.firstMip;
                                desc.Texture2DArray.FirstArraySlice = view.firstSlice;
                                desc.Texture2DArray.ArraySize = view.sliceCount;
                                desc.Texture2DArray.PlaneSlice = 0;
                            }
                            else
                            {
                                desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
                                desc.Texture2D.MipSlice = view.firstMip;
                                desc.Texture2D.PlaneSlice = 0;
                            }
                        }
                        device->CreateRenderTargetView(target->object.Get(), &desc, slotHandle);
                    }
                }
                else
                {
                    D3D12_RENDER_TARGET_VIEW_DESC nullDesc{};
                    nullDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                    nullDesc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
                    device->CreateRenderTargetView(nullptr, &nullDesc, slotHandle);
                }
            }
            TraceD3D12("draw:rtv-end");
        }
        D3D12_CPU_DESCRIPTOR_HANDLE dsv{};
        if(depthTarget)
        {
            TraceD3D12("draw:dsv-begin");
            TextureView dView = depthState ? GetTextureView(depthState->target) : TextureView{};
            dsv=DepthView(depthTarget, depthState ? &dView : nullptr);
            TraceD3D12("draw:dsv-end");
        }
        // Begin() has already bound this exact implicit backbuffer. Reusing
        // it is semantically identical and avoids a driver-side deadlock on
        // Intel 27.20.100.8729 when the duplicate bind follows descriptor
        // table setup. Explicit guest attachments and depth attachments must
        // always be rebound.
        bool implicitDefaultTarget=!draw.colorTargetMask && colorTargetCount==1 && !depthTarget;
        if(implicitDefaultTarget && defaultTargetBound)
        {
            TraceD3D12("draw:omset-skip-current");
        }
        else
        {
            TraceD3D12("draw:omset-begin");
            Require(firstRtv.ptr != 0);
            commands->OMSetRenderTargets(colorTargetCount, &firstRtv, TRUE, depthTarget ? &dsv : nullptr);
            TraceD3D12("draw:omset-end");
        }
        defaultTargetBound=implicitDefaultTarget;
    }
    TraceD3D12("draw:targets-bound");
    // The output-merger is deliberately bound before shader descriptor heaps.
    // This is legal D3D12 command-list ordering and avoids a reproducible UMD
    // deadlock on Intel 27.20.100.8729. BindProgram only changes root/heap
    // state and does not alter the OM attachments selected above.
    TraceD3D12("draw:bindings-begin");
    LARGE_INTEGER bindingsStart{}, bindingsEnd{};
    QueryPerformanceCounter(&bindingsStart);
    auto writes=BindProgram(p,resolvedBindings.data(),bindingCount);
    QueryPerformanceCounter(&bindingsEnd);
    performanceStats.descriptorBindMicroseconds += ElapsedMicroseconds(bindingsStart, bindingsEnd);
    // A byte-aligned CBV snapshot can use an internal compute pipeline.
    commands->SetPipelineState(found->second.Get());
    TraceD3D12("draw:bindings-end");
    if (depthTarget)
    {
        if (depthState && depthState->target)
        {
            TextureView dView = GetTextureView(depthState->target);
            TransitionView(dView, D3D12_RESOURCE_STATE_DEPTH_WRITE);
        }
        else
        {
            Transition(depthTarget, D3D12_RESOURCE_STATE_DEPTH_WRITE);
        }
    }
    if (p->auxiliaryRoot!=UINT_MAX)
    {
        UINT base[]={draw.indexResource ? static_cast<UINT>(draw.baseVertex) : draw.first,draw.firstInstance};
        commands->SetGraphicsRoot32BitConstants(p->auxiliaryRoot,2,base,0);
    }
    commands->OMSetBlendFactor(draw.blendConstant);
    // The stencil reference is dynamic OM state, not part of the PSO.
    // Leaving it unset makes every guest comparison/replacement use zero.
    if (depthState && depthDesc.StencilEnable)
        commands->OMSetStencilRef(depthState->frontRef);
    D3D12_STREAM_OUTPUT_BUFFER_VIEW streamViews[4]{};
    if(draw.streamOutput) for(UINT s=0;s<4;++s) if(draw.streamBuffers[s].resource)
    {
        const auto& b=draw.streamBuffers[s];
        auto dst=Get(b.resource,D3D12_RESOURCE_DIMENSION_BUFFER),counter=Get(b.counter,D3D12_RESOURCE_DIMENSION_BUFFER);
        Transition(dst,D3D12_RESOURCE_STATE_STREAM_OUT); Transition(counter,D3D12_RESOURCE_STATE_STREAM_OUT);
        streamViews[s]={dst->object->GetGPUVirtualAddress()+b.offset,b.bytes,counter->object->GetGPUVirtualAddress()};
    }
    commands->SOSetTargets(0,4,streamViews);
    commands->IASetPrimitiveTopology(static_cast<D3D_PRIMITIVE_TOPOLOGY>(draw.topology));
    commands->IASetVertexBuffers(0,static_cast<UINT>(views.size()),views.data());
    commands->RSSetViewports(1,&draw.viewport); commands->RSSetScissorRects(1,&draw.scissor);
    if (draw.indirectResource)
    {
        auto args=Get(draw.indirectResource,D3D12_RESOURCE_DIMENSION_BUFFER);
        UINT bytes=draw.indexResource ? sizeof(D3D12_DRAW_INDEXED_ARGUMENTS) : sizeof(D3D12_DRAW_ARGUMENTS);
        Require(draw.maxCommands && draw.indirectStride>=bytes && draw.indirectStride%4==0 && draw.indirectOffset%4==0 &&
            Fits(draw.indirectOffset,static_cast<UINT64>(draw.maxCommands)*draw.indirectStride,args->desc.Width));
        D3D12_INDIRECT_ARGUMENT_DESC arguments[2]{};
        UINT argumentCount=1,stride=draw.indirectStride;
        UINT64 argumentOffset=draw.indirectOffset;
        if (p->auxiliaryRoot!=UINT_MAX)
        {
            // SM5.1 does not expose base vertex/instance to the shader. Build
            // [root constants, original draw] records entirely on the GPU.
            Require(draw.maxCommands<=4096);
            stride=bytes+8;
            auto expanded=Allocate(BufferDesc(static_cast<UINT64>(draw.maxCommands)*stride));
            frames[frameIndex].retained.push_back(expanded);
            Transition(args,D3D12_RESOURCE_STATE_COPY_SOURCE); Transition(expanded,D3D12_RESOURCE_STATE_COPY_DEST);
            for (UINT i=0;i<draw.maxCommands;++i)
            {
                UINT64 from=draw.indirectOffset+static_cast<UINT64>(i)*draw.indirectStride;
                UINT64 to=static_cast<UINT64>(i)*stride;
                commands->CopyBufferRegion(expanded->object.Get(),to,args->object.Get(),from+(draw.indexResource ? 12 : 8),8);
                commands->CopyBufferRegion(expanded->object.Get(),to+8,args->object.Get(),from,bytes);
            }
            args=expanded; argumentOffset=0; argumentCount=2;
            arguments[0].Type=D3D12_INDIRECT_ARGUMENT_TYPE_CONSTANT;
            arguments[0].Constant={p->auxiliaryRoot,0,2};
        }
        arguments[argumentCount-1].Type=draw.indexResource ? D3D12_INDIRECT_ARGUMENT_TYPE_DRAW_INDEXED : D3D12_INDIRECT_ARGUMENT_TYPE_DRAW;
        D3D12_COMMAND_SIGNATURE_DESC signatureDesc{stride,argumentCount,arguments,0};
        ComPtr<ID3D12CommandSignature> signature; Check(device->CreateCommandSignature(&signatureDesc,
            p->auxiliaryRoot!=UINT_MAX ? p->root.Get() : nullptr,IID_PPV_ARGS(&signature)));
        frames[frameIndex].signatures.push_back(signature);
        ReadTransition(args,D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT);
        std::shared_ptr<Resource> counter;
        if (draw.countResource)
        {
            counter=Get(draw.countResource,D3D12_RESOURCE_DIMENSION_BUFFER);
            Require(draw.countOffset%4==0 && Fits(draw.countOffset,4,counter->desc.Width));
            ReadTransition(counter,D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT);
        }
        commands->ExecuteIndirect(signature.Get(),draw.maxCommands,args->object.Get(),argumentOffset,
            counter ? counter->object.Get() : nullptr,draw.countOffset);
    }
    else if (draw.indexResource) commands->DrawIndexedInstanced(draw.count,draw.instances,draw.first,draw.baseVertex,draw.firstInstance);
    else commands->DrawInstanced(draw.count,draw.instances,draw.first,draw.firstInstance);
    TraceD3D12("draw:recorded");
    FinishWrites(writes);
    ++performanceStats.renderPasses;
    LARGE_INTEGER recordEnd{};
    QueryPerformanceCounter(&recordEnd);
    performanceStats.recordMicroseconds += ElapsedMicroseconds(recordStart, recordEnd);
}
