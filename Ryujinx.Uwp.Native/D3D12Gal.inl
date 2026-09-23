D3D12_CPU_DESCRIPTOR_HANDLE DepthView(const std::shared_ptr<Resource>& resource, const TextureView* pView = nullptr)
{
    Require(recording && resource->depthFormat != DXGI_FORMAT_UNKNOWN);
    auto& count = frames[frameIndex].dsvCount;
    Require(count < DescriptorsPerFrame, E_OUTOFMEMORY);
    auto handle = dsvHeap->GetCPUDescriptorHandleForHeapStart();
    handle.ptr += static_cast<SIZE_T>(frameIndex * DescriptorsPerFrame + count++) * dsvStride;
    D3D12_DEPTH_STENCIL_VIEW_DESC desc{};
    desc.Format = resource->depthFormat;
    UINT firstSlice = pView ? pView->firstSlice : 0;
    UINT sliceCount = pView ? pView->sliceCount : resource->desc.DepthOrArraySize;
    UINT mipSlice = pView ? pView->firstMip : 0;

    if (resource->desc.SampleDesc.Count > 1)
    {
        if (resource->desc.DepthOrArraySize > 1 || firstSlice > 0)
        {
            desc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2DMSARRAY;
            desc.Texture2DMSArray.FirstArraySlice = firstSlice;
            desc.Texture2DMSArray.ArraySize = sliceCount;
        }
        else
        {
            desc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2DMS;
        }
    }
    else
    {
        if (resource->desc.DepthOrArraySize > 1 || firstSlice > 0)
        {
            desc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2DARRAY;
            desc.Texture2DArray.MipSlice = mipSlice;
            desc.Texture2DArray.FirstArraySlice = firstSlice;
            desc.Texture2DArray.ArraySize = sliceCount;
        }
        else
        {
            desc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
            desc.Texture2D.MipSlice = mipSlice;
        }
    }
    device->CreateDepthStencilView(resource->object.Get(), &desc, handle);
    if (pView)
        TransitionView(*pView, D3D12_RESOURCE_STATE_DEPTH_WRITE);
    else
        Transition(resource, D3D12_RESOURCE_STATE_DEPTH_WRITE);
    return handle;
}

D3D12_CPU_DESCRIPTOR_HANDLE TargetView(UINT64 id)
{
    Require(recording);
    TextureView view{};
    auto resource = id ? GetTextureView(id).resource : targets[targetIndex];
    if (id) view = GetTextureView(id);
    Require((resource->desc.Flags & D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) != 0);
    Transition(resource, D3D12_RESOURCE_STATE_RENDER_TARGET);
    auto handle = rtvHeap->GetCPUDescriptorHandleForHeapStart();
    if (id)
    {
        auto& count = frames[frameIndex].rtvCount;
        Require(count < DescriptorsPerFrame, E_OUTOFMEMORY);
        handle.ptr += static_cast<SIZE_T>(FrameCount + frameIndex * DescriptorsPerFrame + count++) * rtvStride;
        DXGI_FORMAT rtvFormat = ResolveRenderTargetFormat(id);
        if (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D)
        {
            D3D12_RENDER_TARGET_VIEW_DESC desc{};
            desc.Format = rtvFormat;
            desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE3D;
            desc.Texture3D.MipSlice = view.firstMip;
            desc.Texture3D.FirstWSlice = view.firstSlice;
            desc.Texture3D.WSize = view.sliceCount;
            device->CreateRenderTargetView(resource->object.Get(), &desc, handle);
        }
        else
        {
            D3D12_RENDER_TARGET_VIEW_DESC desc{};
            desc.Format = rtvFormat;
            if (resource->desc.SampleDesc.Count > 1)
            {
                if (resource->desc.DepthOrArraySize > 1 || view.firstSlice > 0)
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
                if (resource->desc.DepthOrArraySize > 1 || view.firstSlice > 0)
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
            device->CreateRenderTargetView(resource->object.Get(), &desc, handle);
        }
    }
    else handle.ptr += static_cast<SIZE_T>(targetIndex) * rtvStride;
    return handle;
}

void CopyTexture(UINT64 from, UINT64 to, UINT srcMip, UINT dstMip, UINT sx, UINT sy, UINT sz,
    UINT dx, UINT dy, UINT dz, UINT w, UINT h, UINT d)
{
    auto srcView = GetTextureView(from), dstView = GetTextureView(to);
    auto src = srcView.resource, dst = dstView.resource;
    Require(src->desc.Format == dst->desc.Format || AreFormatsInSameFamily(src->desc.Format, dst->desc.Format),
        E_INVALIDARG, "CopyTexture: format mismatch");
    Require(src->desc.Dimension == dst->desc.Dimension && srcMip < srcView.mipCount && dstMip < dstView.mipCount,
        E_INVALIDARG, "CopyTexture: dimension or mip level out of range");
    UINT physicalSrcMip = srcView.firstMip + srcMip, physicalDstMip = dstView.firstMip + dstMip;
    UINT srcWidth = MipSize(static_cast<UINT>(src->desc.Width), physicalSrcMip);
    UINT srcHeight = MipSize(src->desc.Height, physicalSrcMip);
    UINT dstWidth = MipSize(static_cast<UINT>(dst->desc.Width), physicalDstMip);
    UINT dstHeight = MipSize(dst->desc.Height, physicalDstMip);

    bool is3D = src->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D;
    UINT srcDepth = is3D ? MipSize(src->desc.DepthOrArraySize, physicalSrcMip) : src->desc.DepthOrArraySize;
    UINT dstDepth = is3D ? MipSize(dst->desc.DepthOrArraySize, physicalDstMip) : dst->desc.DepthOrArraySize;
    UINT sourceZ = srcView.firstSlice + sz;
    UINT destinationZ = dstView.firstSlice + dz;

    bool depth = src->depthFormat != DXGI_FORMAT_UNKNOWN || dst->depthFormat != DXGI_FORMAT_UNKNOWN;
    if (depth) Require(!sx && !sy && !sz && !dx && !dy && !dz && d == 1 &&
        w == srcWidth && h == srcHeight && w == dstWidth && h == dstHeight,
        E_INVALIDARG, "CopyTexture: depth texture copy requires entire resource matching");
    Require(w && h && d && Fits(sx, w, srcWidth) && Fits(sy, h, srcHeight) && Fits(sourceZ, d, srcDepth) &&
        Fits(dx, w, dstWidth) && Fits(dy, h, dstHeight) && Fits(destinationZ, d, dstDepth),
        E_INVALIDARG, "CopyTexture: bounds out of range");

    if (src == dst)
    {
        // Exact self-copies are identities. Overlapping copies need an explicit
        // temporary and must not be submitted as an invalid D3D12 operation.
        Require(physicalSrcMip == physicalDstMip && sx == dx && sy == dy && sourceZ == destinationZ,
            E_INVALIDARG, "CopyTexture: overlapping self copy");
        return;
    }

    if (src->desc.SampleDesc.Count > 1 && dst->desc.SampleDesc.Count == 1)
    {
        Require(srcWidth == dstWidth && srcHeight == dstHeight, E_INVALIDARG, "ResolveSubresource: dimension mismatch");
        Require(!sx && !sy && !dx && !dy && w == srcWidth && h == srcHeight, E_INVALIDARG, "ResolveSubresource: full subresource required");
        DXGI_FORMAT resolveFormat = ResolveRenderTargetFormat(to);
        if (resolveFormat == DXGI_FORMAT_UNKNOWN || IsTypelessFormat(resolveFormat)) resolveFormat = dst->desc.Format;
        if (resolveFormat == DXGI_FORMAT_UNKNOWN || IsTypelessFormat(resolveFormat)) resolveFormat = ResolveRenderTargetFormat(from);
        Require(resolveFormat != DXGI_FORMAT_UNKNOWN && !IsTypelessFormat(resolveFormat), E_INVALIDARG, "ResolveSubresource: typed format required");

        Transition(src, D3D12_RESOURCE_STATE_RESOLVE_SOURCE);
        Transition(dst, D3D12_RESOURCE_STATE_RESOLVE_DEST);

        UINT srcMipLevels = src->desc.MipLevels;
        UINT dstMipLevels = dst->desc.MipLevels;
        for (UINT slice = 0; slice < d; ++slice)
        {
            UINT srcSubresource = physicalSrcMip + (sourceZ + slice) * srcMipLevels;
            UINT dstSubresource = physicalDstMip + (destinationZ + slice) * dstMipLevels;
            commands->ResolveSubresource(dst->object.Get(), dstSubresource, src->object.Get(), srcSubresource, resolveFormat);
        }
        return;
    }

    Transition(src, D3D12_RESOURCE_STATE_COPY_SOURCE);
    Transition(dst, D3D12_RESOURCE_STATE_COPY_DEST);

    D3D12_TEXTURE_COPY_LOCATION a{}, b{};
    a.pResource = src->object.Get();
    b.pResource = dst->object.Get();
    a.Type = b.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;

    if (is3D)
    {
        a.SubresourceIndex = physicalSrcMip;
        b.SubresourceIndex = physicalDstMip;
        D3D12_BOX box{sx, sy, sourceZ, sx + w, sy + h, sourceZ + d};
        commands->CopyTextureRegion(&b, dx, dy, destinationZ, &a, depth ? nullptr : &box);
    }
    else
    {
        UINT srcMipLevels = src->desc.MipLevels;
        UINT dstMipLevels = dst->desc.MipLevels;
        UINT srcArraySize = src->desc.DepthOrArraySize;
        UINT dstArraySize = dst->desc.DepthOrArraySize;
        UINT planes = (IsPlanarDepthStencil(src->desc.Format) && IsPlanarDepthStencil(dst->desc.Format)) ? 2 : 1;
        for (UINT plane = 0; plane < planes; ++plane)
        {
            for (UINT slice = 0; slice < d; ++slice)
            {
                UINT srcSubresource = physicalSrcMip + (sourceZ + slice) * srcMipLevels + plane * srcMipLevels * srcArraySize;
                UINT dstSubresource = physicalDstMip + (destinationZ + slice) * dstMipLevels + plane * dstMipLevels * dstArraySize;
                a.SubresourceIndex = srcSubresource;
                b.SubresourceIndex = dstSubresource;
                D3D12_BOX box{sx, sy, 0, sx + w, sy + h, 1};
                commands->CopyTextureRegion(&b, dx, dy, 0, &a, depth ? nullptr : &box);
            }
        }
    }
}

UINT64 BeginCounter(UINT type)
{
    Require(recording && type<=2 && counters.size()<1024);
    // One active query of each kind. Native scopes must end before submission.
    for(const auto& [id,c] : counters) Require(c.fence || c.type!=type);
    Counter counter{}; counter.type=type;
    D3D12_QUERY_HEAP_DESC desc{type==0 ? D3D12_QUERY_HEAP_TYPE_OCCLUSION : type==1 ? D3D12_QUERY_HEAP_TYPE_PIPELINE_STATISTICS : D3D12_QUERY_HEAP_TYPE_SO_STATISTICS,type==2 ? 4u : 1u,0};
    Check(device->CreateQueryHeap(&desc,IID_PPV_ARGS(&counter.heap)));
    counter.result=std::make_shared<Resource>();
    auto properties=Heap(D3D12_HEAP_TYPE_READBACK);
    auto buffer=BufferDesc(type==0 ? 8 : type==1 ? sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS) : 4*sizeof(D3D12_QUERY_DATA_SO_STATISTICS));
    Check(device->CreateCommittedResource(&properties,D3D12_HEAP_FLAG_NONE,&buffer,D3D12_RESOURCE_STATE_COPY_DEST,
        nullptr,IID_PPV_ARGS(&counter.result->object)));
    counter.result->desc=buffer; counter.result->state=D3D12_RESOURCE_STATE_COPY_DEST;
    frames[frameIndex].queries.push_back(counter.heap);
    if(type==2) for(UINT i=0;i<4;++i)
        commands->BeginQuery(counter.heap.Get(),static_cast<D3D12_QUERY_TYPE>(D3D12_QUERY_TYPE_SO_STATISTICS_STREAM0+i),i);
    else commands->BeginQuery(counter.heap.Get(),type==0 ? D3D12_QUERY_TYPE_OCCLUSION : D3D12_QUERY_TYPE_PIPELINE_STATISTICS,0);
    UINT64 id=nextResource++; counters.emplace(id,counter); ++activeCounters; return id;
}

void EndCounter(UINT64 id)
{
    auto it=counters.find(id); Require(recording && it!=counters.end() && !it->second.fence);
    auto& counter=it->second;
    UINT count=counter.type==2 ? 4 : 1;
    for(UINT i=0;i<count;++i)
    {
        auto type=counter.type==0 ? D3D12_QUERY_TYPE_OCCLUSION : counter.type==1 ? D3D12_QUERY_TYPE_PIPELINE_STATISTICS :
            static_cast<D3D12_QUERY_TYPE>(D3D12_QUERY_TYPE_SO_STATISTICS_STREAM0+i);
        commands->EndQuery(counter.heap.Get(),type,i);
        commands->ResolveQueryData(counter.heap.Get(),type,i,1,counter.result->object.Get(),i*sizeof(D3D12_QUERY_DATA_SO_STATISTICS));
    }
    frames[frameIndex].retained.push_back(counter.result);
    counter.fence=submitted+1; --activeCounters;
}

UINT64 ReadCounter(UINT64 id)
{
    auto it=counters.find(id); Require(it!=counters.end() && it->second.fence && it->second.fence<=submitted);
    auto& counter=it->second; Wait(counter.fence);
    void* data=nullptr; D3D12_RANGE range{0,static_cast<SIZE_T>(counter.result->desc.Width)};
    Check(counter.result->object->Map(0,&range,&data));
    UINT64 result=counter.type==0 ? *static_cast<UINT64*>(data) : counter.type==1 ? static_cast<D3D12_QUERY_DATA_PIPELINE_STATISTICS*>(data)->CInvocations : 0;
    if(counter.type==2) for(UINT i=0;i<4;++i) result+=static_cast<D3D12_QUERY_DATA_SO_STATISTICS*>(data)[i].NumPrimitivesWritten;
    D3D12_RANGE written{0,0}; counter.result->object->Unmap(0,&written); return result;
}
