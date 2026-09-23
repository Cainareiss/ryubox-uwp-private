using Ryujinx.Common.Memory;
using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.D3D11;

internal sealed class D3D11BufferResource : IDisposable
{
    // Guest uniform buffers are lowered by SPIRV-Cross to an SM5 cbuffer with
    // a 4096 x float4 backing array. D3D11 validates the physical buffer
    // against that complete 64 KiB declaration even when the shader only
    // touches a much smaller guest range. Binding a range-sized resource (for
    // example 768 bytes) makes all accesses beyond it read zero and breaks
    // matrices/state used by 3D titles such as Captain Toad.
    private const int ShaderModel5ConstantBufferSize = 65536;
    private const int MaximumCachedConstantBufferSlices = 8;
    private readonly D3D11Device _device;
    private readonly D3D11DeviceContext _context;
    // Many GPU-only buffers are never read by the CPU. Allocate the managed
    // mirror only when a write or a constant-buffer slice actually needs it;
    // eagerly mirroring every buffer needlessly consumes the Xbox memory cap.
    private byte[] _shadow;
    private sealed class ConstantSlice
    {
        public nint Resource;
        public bool Dirty = true;
    }
    private readonly Dictionary<(int Offset, int Size), ConstantSlice> _constantBuffers = new();
    internal long ConstantUploadCount { get; private set; }
    internal long ConstantUploadBytes => ConstantUploadCount * ShaderModel5ConstantBufferSize;
    internal long GuestWriteBytes { get; private set; }
    internal long ConstantCacheHits { get; private set; }
    private readonly int _size;
    private nint _stagingResource;
    private int _stagingCapacity;
    internal long ReadbackBytes { get; private set; }
    private nint _streamOutputResource;
    private long _generation;
    private bool _shadowValid = true;
    private static long _liveBufferCount;

    public static long LiveBufferCount => Interlocked.Read(ref _liveBufferCount);

    public nint Resource { get; }
    public nint ShaderResourceView { get; }
    public nint UnorderedAccessView { get; }
    public int Size => _size;
    public long Generation => _generation;

    public D3D11BufferResource(D3D11Device device, D3D11DeviceContext context, int size)
    {
        Interlocked.Increment(ref _liveBufferCount);
        _device = device;
        _context = context;
        _size = size;

        int nativeSize = Math.Max(4, (size + 3) & ~3);
        D3D11BufferDesc resourceDesc = new()
        {
            ByteWidth = (uint)nativeSize,
            Usage = D3D11Usage.Default,
            BindFlags = D3D11BindFlags.VertexBuffer |
                        D3D11BindFlags.IndexBuffer |
                        D3D11BindFlags.ShaderResource |
                        D3D11BindFlags.UnorderedAccess,
            CpuAccessFlags = D3D11CpuAccessFlags.None,
            MiscFlags = D3D11ResourceMiscFlags.BufferAllowRawViews,
            StructureByteStride = 0,
        };

        Resource = _device.CreateBuffer(in resourceDesc);

        uint elementCount = (uint)nativeSize / 4u;
        D3D11ShaderResourceViewDesc shaderResourceDesc = new()
        {
            Format = D3D11Format.R32Typeless,
            ViewDimension = D3D11SrvDimension.BufferExtended,
            View = new D3D11SrvUnion
            {
                BufferExtended = new D3D11BufferExtendedSrv
                {
                    FirstElement = 0,
                    NumElements = elementCount,
                    Flags = 1, // D3D11_BUFFEREX_SRV_FLAG_RAW
                },
            },
        };
        ShaderResourceView = _device.CreateShaderResourceView(Resource, in shaderResourceDesc);

        D3D11UnorderedAccessViewDesc unorderedAccessDesc = new()
        {
            Format = D3D11Format.R32Typeless,
            ViewDimension = D3D11UavDimension.Buffer,
            View = new D3D11UavUnion
            {
                Buffer = new D3D11BufferUav
                {
                    FirstElement = 0,
                    NumElements = elementCount,
                    Flags = 1, // D3D11_BUFFER_UAV_FLAG_RAW
                },
            },
        };
        UnorderedAccessView = _device.CreateUnorderedAccessView(Resource, in unorderedAccessDesc);

    }

    public void SetData(int offset, ReadOnlySpan<byte> data)
    {
        ValidateRange(offset, data.Length);
        // A full CPU replacement needs none of the old GPU contents.
        if (offset == 0 && data.Length == _size)
        {
            _shadow ??= new byte[_size];
            _shadowValid = true;
        }
        else EnsureShadow();
        data.CopyTo(_shadow.AsSpan(offset));
        if (data.Length != 0)
        {
            D3D11Box destination = new()
            {
                Left = (uint)offset,
                Right = (uint)(offset + data.Length),
                Bottom = 1,
                Back = 1,
            };
            _context.UpdateSubresource(Resource, data, in destination);
            _generation++;

            int updateEnd = offset + data.Length;
            GuestWriteBytes += data.Length;
            foreach (((int rangeOffset, int rangeSize), ConstantSlice constantBuffer) in _constantBuffers)
            {
                if (offset < rangeOffset + rangeSize && updateEnd > rangeOffset)
                {
                    constantBuffer.Dirty = true;
                }
            }
        }
    }

    public void CopyData(int offset, Span<byte> destination)
    {
        ValidateRange(offset, destination.Length);

        if (_shadowValid && _shadow is not null)
        {
            _shadow.AsSpan(offset, destination.Length).CopyTo(destination);
            return;
        }

        using PinnedSpan<byte> data = GetData(offset, destination.Length);
        data.Get().CopyTo(destination);
    }

    public void MarkGpuModified()
    {
        _shadowValid = false;
        _generation++;
        foreach (ConstantSlice slice in _constantBuffers.Values) slice.Dirty = true;
    }

    public void NotifyBufferCopy(D3D11BufferResource source, int srcOffset, int dstOffset, int size)
    {
        source.ValidateRange(srcOffset, size);
        ValidateRange(dstOffset, size);
        if (source._shadowValid && source._shadow is not null && _shadowValid)
        {
            // BufferCache merges use a GPU copy. Preserve a known CPU mirror
            // as well, so subsequent constant slices neither read stale bytes
            // nor need a synchronous GPU readback for an ordinary merge.
            EnsureShadow();
            source._shadow.AsSpan(srcOffset, size).CopyTo(_shadow.AsSpan(dstOffset, size));
            _generation++;
            foreach (((int offset, int length), ConstantSlice constantBuffer) in _constantBuffers)
            {
                if (dstOffset < offset + length && dstOffset + size > offset)
                {
                    constantBuffer.Dirty = true;
                }
            }
        }
        else
        {
            MarkGpuModified();
        }
    }

    public nint GetConstantBuffer(int offset, int size)
    {
        ValidateRange(offset, size);
        if (size == 0)
        {
            return 0;
        }

        if (size > 65536)
        {
            size = 65536;
        }

        (int Offset, int Size) key = (offset, size);
        // A GPU write invalidates cached slices too, not only CopyData's
        // shadow. Synchronize before returning an existing constant buffer.
        EnsureShadow();
        if (_constantBuffers.TryGetValue(key, out ConstantSlice constantBuffer))
        {
            ConstantCacheHits++;
            if (constantBuffer.Dirty) UpdateConstantBuffer(constantBuffer, offset, size);
            return constantBuffer.Resource;
        }

        if (_constantBuffers.Count >= MaximumCachedConstantBufferSlices)
        {
            List<(int Offset, int Size)> keysToRemove = new();
            int removeCount = Math.Max(1, _constantBuffers.Count - (MaximumCachedConstantBufferSlices / 2));
            foreach ((int Offset, int Size) existingKey in _constantBuffers.Keys)
            {
                keysToRemove.Add(existingKey);
                if (keysToRemove.Count >= removeCount) break;
            }
            foreach ((int Offset, int Size) k in keysToRemove)
            {
                if (_constantBuffers.Remove(k, out ConstantSlice oldCb))
                {
                    D3D11Com.Release(oldCb.Resource);
                }
            }
        }

        D3D11BufferDesc constantDesc = new()
        {
            // Match the cbuffer declaration emitted by SPIRV-Cross. The
            // requested guest slice is copied at byte zero and the unused
            // tail remains zero-filled, which is the behavior expected by the
            // translated indexed loads.
            ByteWidth = ShaderModel5ConstantBufferSize,
            Usage = D3D11Usage.Dynamic,
            BindFlags = D3D11BindFlags.ConstantBuffer,
            CpuAccessFlags = D3D11CpuAccessFlags.Write,
            MiscFlags = D3D11ResourceMiscFlags.None,
            StructureByteStride = 0,
        };

        constantBuffer = new ConstantSlice { Resource = _device.CreateBuffer(in constantDesc) };
        _constantBuffers.Add(key, constantBuffer);
        UpdateConstantBuffer(constantBuffer, offset, size);
        return constantBuffer.Resource;
    }

    public void Clear(int offset, int size, uint value)
    {
        ValidateRange(offset, size);
        byte[] data = new byte[size];
        for (int index = 0; index < data.Length; index += sizeof(uint))
        {
            int remaining = Math.Min(sizeof(uint), data.Length - index);
            for (int byteIndex = 0; byteIndex < remaining; byteIndex++)
            {
                data[index + byteIndex] = (byte)(value >> (byteIndex * 8));
            }
        }

        SetData(offset, data);
    }

    public unsafe PinnedSpan<byte> GetData(int offset, int size)
    {
        ValidateRange(offset, size);
        if (size == 0) return new PinnedSpan<byte>(null, 0);
        if (_shadowValid && _shadow is not null) return Pin(_shadow.AsSpan(offset, size).ToArray());
        int alignedStart = offset & ~3;
        int alignedEnd = checked((offset + size + 3) & ~3);
        int copySize = alignedEnd - alignedStart;
        EnsureStagingResource(copySize);
        D3D11Box box = new() { Left = (uint)alignedStart, Right = (uint)alignedEnd, Bottom = 1, Back = 1 };
        _context.CopySubresourceRegion(_stagingResource, 0, 0, 0, 0, Resource, 0, in box);
        ReadbackBytes += copySize;
        D3D11MappedSubresource mapped = _context.Map(_stagingResource, D3D11MapType.Read);
        byte[] copy = new byte[size];
        try { Marshal.Copy(mapped.Data + offset - alignedStart, copy, 0, size); }
        finally { _context.Unmap(_stagingResource); }
        return Pin(copy);
    }

    public nint PrepareStreamOutput()
    {
        if (_streamOutputResource == 0)
        {
            D3D11BufferDesc desc = new()
            {
                ByteWidth = (uint)Math.Max(4, (_size + 3) & ~3),
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.StreamOutput | D3D11BindFlags.VertexBuffer,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
                StructureByteStride = 0,
            };
            _streamOutputResource = _device.CreateBuffer(in desc);
        }

        _context.CopyResource(_streamOutputResource, Resource);
        return _streamOutputResource;
    }

    public void CommitStreamOutput()
    {
        if (_streamOutputResource != 0)
        {
            _context.CopyResource(Resource, _streamOutputResource);
            MarkGpuModified();
        }
    }

    private static unsafe PinnedSpan<byte> Pin(byte[] data)
    {
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        return new PinnedSpan<byte>((void*)pin.AddrOfPinnedObject(), data.Length, pin.Free);
    }

    public void Dispose()
    {
        D3D11Com.Release(UnorderedAccessView);
        D3D11Com.Release(ShaderResourceView);
        foreach (ConstantSlice constantBuffer in _constantBuffers.Values)
        {
            D3D11Com.Release(constantBuffer.Resource);
        }
        _constantBuffers.Clear();
        D3D11Com.Release(_streamOutputResource);
        D3D11Com.Release(_stagingResource);
        D3D11Com.Release(Resource);
        Interlocked.Decrement(ref _liveBufferCount);
    }

    private void ValidateRange(int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > _size - size)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
    }

    private void EnsureStagingResource(int size)
    {
        if (_stagingResource != 0 && _stagingCapacity >= size)
        {
            return;
        }

        D3D11BufferDesc stagingDesc = new()
        {
            ByteWidth = (uint)Math.Max(4, size),
            Usage = D3D11Usage.Staging,
            BindFlags = D3D11BindFlags.None,
            CpuAccessFlags = D3D11CpuAccessFlags.Read,
            MiscFlags = D3D11ResourceMiscFlags.None,
            StructureByteStride = 0,
        };
        nint replacement = _device.CreateBuffer(in stagingDesc);
        D3D11Com.Release(_stagingResource);
        _stagingResource = replacement;
        _stagingCapacity = (int)stagingDesc.ByteWidth;
    }

    private unsafe void UpdateConstantBuffer(ConstantSlice constantBuffer, int offset, int size)
    {
        EnsureShadow();
        // DISCARD is supported for dynamic constant buffers on D3D11.0.
        // The driver preserves backing memory referenced by in-flight draws.
        // No D3D11.1 range binding or optional NO_OVERWRITE support is assumed.
        D3D11MappedSubresource mapped = _context.Map(constantBuffer.Resource, D3D11MapType.WriteDiscard);
        try
        {
            Span<byte> upload = new((void*)mapped.Data, ShaderModel5ConstantBufferSize);
            _shadow.AsSpan(offset, size).CopyTo(upload);
            upload[size..].Clear();
        }
        finally { _context.Unmap(constantBuffer.Resource); }
        constantBuffer.Dirty = false;
        ConstantUploadCount++;
    }

    private void EnsureShadow()
    {
        _shadow ??= new byte[_size];
        if (!_shadowValid)
        {
            using PinnedSpan<byte> data = GetData(0, _size);
            data.Get().CopyTo(_shadow);
            _shadowValid = true;
            // MarkGpuModified dirtied every slice. Other slices must remain
            // dirty after this mirror becomes valid, until their bind/draw.
        }
    }
}

internal sealed class D3D11Program : IProgram
{
    private readonly byte[] _binary;
    private readonly Dictionary<ShaderStage, nint> _shaders = new();
    private readonly Dictionary<ShaderStage, byte[]> _bytecodes = new();
    private ProgramLinkStatus _status;
    public D3D11BindingLayout BindingLayout { get; }

    public D3D11Program(
        D3D11Device device,
        Func<ShaderStage, string, byte[]> shaderCompiler,
        Func<ShaderStage, byte[], byte[]> spirvCompiler,
        Func<ShaderStage, byte[], ShaderInfo, byte[]> spirvCompilerWithLayout,
        ShaderSource[] shaders,
        ShaderInfo info)
    {
        BindingLayout = new D3D11BindingLayout(info);
        Dictionary<ShaderStage, byte[]> binaries = new();
        _status = ProgramLinkStatus.Success;

        foreach (ShaderSource source in shaders)
        {
            byte[] bytecode = source.BinaryCode;
            try
            {
                if (source.Language == TargetLanguage.Spirv && bytecode is not null && spirvCompilerWithLayout is not null)
                {
                    bytecode = spirvCompilerWithLayout(source.Stage, bytecode, info);
                }
                else if (source.Language == TargetLanguage.Spirv && bytecode is not null && spirvCompiler is not null)
                {
                    bytecode = spirvCompiler(source.Stage, bytecode);
                }
                else if ((bytecode is null || bytecode.Length == 0) && shaderCompiler is not null && source.Code is not null)
                {
                    bytecode = shaderCompiler(source.Stage, source.Code);
                }
            }
            catch (Exception exception)
            {
                // A single unsupported shader (for example a subgroup/Wave
                // operation that has no SM5 equivalent) must not kill the
                // whole GPU FIFO. Keep the program partial and let the
                // pipeline skip only draws that require this stage.
                _status = ProgramLinkStatus.Failure;
                D3D11DebugTelemetry.Append(
                    $"shader-translate-failed: stage={source.Stage}, language={source.Language}, " +
                    $"reason={exception.GetType().Name}: {exception.Message}\n");
                continue;
            }

            if (bytecode is null || bytecode.Length == 0)
            {
                _status = ProgramLinkStatus.Failure;
                continue;
            }

            binaries[source.Stage] = bytecode;
            _bytecodes[source.Stage] = bytecode;
            TryCreateShader(device, source.Stage, bytecode);
        }

        _binary = _status == ProgramLinkStatus.Success
            ? D3D11ProgramBinary.Pack(binaries)
            : Array.Empty<byte>();
    }

    public D3D11Program(D3D11Device device, byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
    {
        BindingLayout = new D3D11BindingLayout(info);
        _binary = programBinary ?? Array.Empty<byte>();
        _status = ProgramLinkStatus.Failure;

        if (D3D11ProgramBinary.TryUnpack(_binary, out List<(ShaderStage Stage, byte[] Bytecode)> stages))
        {
            _status = ProgramLinkStatus.Success;

            foreach ((ShaderStage stage, byte[] bytecode) in stages)
            {
                _bytecodes[stage] = bytecode;
                TryCreateShader(device, stage, bytecode);
            }
        }
    }

    private void TryCreateShader(D3D11Device device, ShaderStage stage, byte[] bytecode)
    {
        try
        {
            nint shader = stage switch
            {
                ShaderStage.Vertex => device.CreateVertexShader(bytecode),
                ShaderStage.Fragment => device.CreatePixelShader(bytecode),
                ShaderStage.Geometry => device.CreateGeometryShader(bytecode),
                ShaderStage.TessellationControl => device.CreateHullShader(bytecode),
                ShaderStage.TessellationEvaluation => device.CreateDomainShader(bytecode),
                ShaderStage.Compute => device.CreateComputeShader(bytecode),
                _ => throw new NotSupportedException($"Unsupported shader stage: {stage}"),
            };

            _shaders[stage] = shader;
        }
        catch (Exception exception) when (exception is D3D11Exception or NotSupportedException or ArgumentException)
        {
            _status = ProgramLinkStatus.Failure;
            D3D11DebugTelemetry.Append(
                $"shader-create-failed: stage={stage}, bytecode={bytecode?.Length ?? 0}, reason={exception.GetType().Name}: {exception.Message}\n");
        }
    }

    public ProgramLinkStatus CheckProgramLink(bool blocking)
    {
        return _status;
    }

    public nint GetShader(ShaderStage stage)
    {
        return _shaders.TryGetValue(stage, out nint shader) ? shader : 0;
    }

    public byte[] GetShaderBytecode(ShaderStage stage)
    {
        return _bytecodes.TryGetValue(stage, out byte[] bytecode) ? bytecode : null;
    }

    public byte[] GetBinary()
    {
        return _binary;
    }

    public bool HasAnyNativeShader()
    {
        return _shaders.Count != 0;
    }

    public bool HasNativeShader(ShaderStage stage)
    {
        return _shaders.TryGetValue(stage, out nint shader) && shader != 0;
    }

    public void Dispose()
    {
        foreach (nint shader in _shaders.Values)
        {
            D3D11Com.Release(shader);
        }

        _shaders.Clear();
    }
}

internal sealed class D3D11Sampler : ISampler
{
    public nint NativeSampler { get; }

    public D3D11Sampler(D3D11Device device, SamplerCreateInfo info)
    {
        D3D11SamplerDesc desc = new()
        {
            Filter = D3D11FormatConversion.ToFilter(
                info.MinFilter,
                info.MagFilter,
                info.CompareMode == CompareMode.CompareRToTexture,
                info.MaxAnisotropy),
            AddressU = D3D11FormatConversion.ToAddressMode(info.AddressU),
            AddressV = D3D11FormatConversion.ToAddressMode(info.AddressV),
            AddressW = D3D11FormatConversion.ToAddressMode(info.AddressP),
            MipLODBias = info.MipLodBias,
            MaxAnisotropy = (uint)Math.Clamp(info.MaxAnisotropy, 1f, 16f),
            ComparisonFunc = D3D11FormatConversion.ToComparisonFunc(info.CompareOp),
            BorderColor0 = info.BorderColor.Red,
            BorderColor1 = info.BorderColor.Green,
            BorderColor2 = info.BorderColor.Blue,
            BorderColor3 = info.BorderColor.Alpha,
            MinLOD = info.MinLod,
            MaxLOD = info.MaxLod,
        };

        NativeSampler = device.CreateSamplerState(in desc);
    }

    public void Dispose()
    {
        D3D11Com.Release(NativeSampler);
    }
}

internal sealed class D3D11ImageArray : IImageArray
{
    private readonly ITexture[] _images;

    public D3D11ImageArray(int size)
    {
        _images = new ITexture[size];
    }

    public void SetImages(int index, ITexture[] images)
    {
        images.AsSpan().CopyTo(_images.AsSpan(index));
    }

    internal ReadOnlySpan<ITexture> Images => _images;

    public void Dispose()
    {
        Array.Clear(_images);
    }
}

internal sealed class D3D11TextureArray : ITextureArray
{
    private readonly ITexture[] _textures;
    private readonly ISampler[] _samplers;

    public D3D11TextureArray(int size)
    {
        _textures = new ITexture[size];
        _samplers = new ISampler[size];
    }

    public void SetSamplers(int index, ISampler[] samplers)
    {
        samplers.AsSpan().CopyTo(_samplers.AsSpan(index));
    }

    public void SetTextures(int index, ITexture[] textures)
    {
        textures.AsSpan().CopyTo(_textures.AsSpan(index));
    }

    internal ReadOnlySpan<ITexture> Textures => _textures;
    internal ReadOnlySpan<ISampler> Samplers => _samplers;

    public void Dispose()
    {
        Array.Clear(_textures);
        Array.Clear(_samplers);
    }
}

internal sealed class D3D11Texture : ITexture
{
    private readonly D3D11Device _device;
    private readonly D3D11DeviceContext _context;
    private readonly TextureCreateInfo _info;
    private readonly Func<BufferHandle, D3D11BufferResource> _bufferResolver;
    private readonly D3D11Texture _storageOwner;
    private readonly bool _ownsResource;
    private readonly bool _ownsViews;
    private readonly int _viewFirstLayer;
    private readonly int _viewFirstLevel;
    private readonly int _viewLayerCount;
    private readonly int _viewLevelCount;
    private readonly int _resourceLayerCount;
    private readonly int _resourceLevelCount;
    private byte[] _shadow;
    private nint _stagingResource;
    private nint _resolveResource;
    private sealed class FeedbackSnapshot
    {
        internal D3D11Texture Texture;
        internal long Generation = -1;
        internal int References;
    }
    private readonly Dictionary<(int Mip, int Layer, Format Format), FeedbackSnapshot> _feedbackSnapshots = new();
    private FeedbackSnapshot _feedbackSnapshot;
    private readonly Func<D3D11Texture, D3D11Texture, Extents2D, Extents2D, bool, bool> _gpuBlit;

    /// <summary>
    /// Incremented every time this texture is modified (draw, clear, copy write, compute UAV write).
    /// Used by the hazard-feedback system to avoid unnecessary CopySubresourceRegion calls.
    /// </summary>
    private readonly Dictionary<uint, long> _subresourceGenerations = new();
    private long _writeSerial;
    public long WriteGeneration => _storageOwner._subresourceGenerations.TryGetValue(
        GetD3DSubresource(_viewFirstLayer, _viewFirstLevel, _resourceLevelCount), out long generation) ? generation : 0;

    /// <summary>
    /// Increment the write-generation counter. Call this whenever the resource is written to.
    /// </summary>
    internal void NotifyWrite()
    {
        for (int layer = _viewFirstLayer; layer < _viewFirstLayer + _viewLayerCount; layer++)
            for (int level = _viewFirstLevel; level < _viewFirstLevel + _viewLevelCount; level++)
                NotifySubresourceWrite(layer, level);
    }

    private void NotifySubresourceWrite(int layer, int level)
    {
        uint subresource = GetD3DSubresource(_storageOwner._info.Target == Target.Texture3D ? 0 : layer, level, _resourceLevelCount);
        _storageOwner._subresourceGenerations[subresource] = ++_storageOwner._writeSerial;
    }

    internal void NotifyRenderTargetWrite()
    {
        // An RTV addresses one mip, even if its parent SRV spans all mips.
        for (int layer = _viewFirstLayer; layer < _viewFirstLayer + _viewLayerCount; layer++)
            NotifySubresourceWrite(layer, _viewFirstLevel);
    }

    public nint Resource { get; private set; }
    public nint ShaderResourceView { get; private set; }
    public nint RenderTargetView { get; private set; }
    public nint DepthStencilView { get; private set; }
    public nint UnorderedAccessView { get; private set; }

    public int Width => _info.Width;
    public int Height => _info.Height;
    internal TextureCreateInfo Info => _info;
    internal int ViewFirstLayer => _viewFirstLayer;
    internal int ViewFirstLevel => _viewFirstLevel;
    internal int ViewLayerCount => _viewLayerCount;
    internal int ViewLevelCount => _viewLevelCount;
    public bool IsDepthStencil => _info.Format.HasDepth;
    internal string PresentationDescription => $"GAL={_info.Format}, DXGI={D3D11FormatConversion.ToDxgi(_info.Format)}, target={_info.Target}, samples={_info.Samples}, levels={_info.Levels}, layers={_info.GetLayers()}";

    public D3D11Texture(D3D11Device device, D3D11DeviceContext context, TextureCreateInfo info, Func<BufferHandle, D3D11BufferResource> bufferResolver = null,
        Func<D3D11Texture, D3D11Texture, Extents2D, Extents2D, bool, bool> gpuBlit = null)
    {
        _device = device;
        _context = context;
        _bufferResolver = bufferResolver;
        _storageOwner = this;
        _gpuBlit = gpuBlit;
        _info = info;
        _ownsResource = info.Target != Target.TextureBuffer;
        _ownsViews = true;
        _viewFirstLayer = 0;
        _viewFirstLevel = 0;
        _viewLayerCount = info.Target == Target.Texture3D
            ? Math.Max(1, info.Depth)
            : Math.Max(1, info.GetLayers());
        _viewLevelCount = info.Samples > 1 ? 1 : Math.Max(1, info.Levels);
        _resourceLayerCount = _viewLayerCount;
        _resourceLevelCount = _viewLevelCount;
        _shadow = Array.Empty<byte>();

        if (info.Target == Target.TextureBuffer)
        {
            Resource = 0;
            ShaderResourceView = 0;
            RenderTargetView = 0;
            DepthStencilView = 0;
            UnorderedAccessView = 0;
            _stagingResource = 0;
            _resolveResource = 0;
            return;
        }

        if (info.Target is Target.Texture1D or Target.Texture1DArray)
        {
            D3D11Format nativeFormat1D = D3D11FormatConversion.ToDxgi(info.Format);
            D3D11FormatSupport formatSupport1D = _device.CheckFormatSupport(nativeFormat1D);
            D3D11Texture1DDesc desc1D = new()
            {
                Width = (uint)Math.Max(1, info.Width),
                MipLevels = (uint)Math.Max(1, info.Levels),
                ArraySize = (uint)Math.Max(1, info.GetLayers()),
                Format = nativeFormat1D,
                Usage = D3D11Usage.Default,
                BindFlags = GetBindFlags(info, formatSupport1D),
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
            };
            Resource = _device.CreateTexture1D(in desc1D, 0, 0);
            _stagingResource = 0;
            _resolveResource = 0;

            if (!IsDepthStencil)
            {
                if ((desc1D.BindFlags & D3D11BindFlags.ShaderResource) != 0)
                {
                    ShaderResourceView = _device.CreateShaderResourceView(Resource);
                }
                if ((desc1D.BindFlags & D3D11BindFlags.RenderTarget) != 0)
                {
                    RenderTargetView = _device.CreateRenderTargetView(Resource);
                }
                if ((desc1D.BindFlags & D3D11BindFlags.UnorderedAccess) != 0)
                {
                    UnorderedAccessView = _device.CreateUnorderedAccessView(Resource);
                }
            }
            else if ((desc1D.BindFlags & D3D11BindFlags.DepthStencil) != 0)
            {
                DepthStencilView = _device.CreateDepthStencilView(Resource);
            }
            return;
        }

        if (info.Target == Target.Texture3D)
        {
            D3D11Format nativeFormat3D = D3D11FormatConversion.ToDxgi(info.Format);
            D3D11FormatSupport formatSupport3D = _device.CheckFormatSupport(nativeFormat3D);
            D3D11Texture3DDesc desc3D = new()
            {
                Width = (uint)Math.Max(1, info.Width),
                Height = (uint)Math.Max(1, info.Height),
                Depth = (uint)Math.Max(1, info.Depth),
                MipLevels = (uint)Math.Max(1, info.Levels),
                Format = nativeFormat3D,
                Usage = D3D11Usage.Default,
                BindFlags = GetBindFlags(info, formatSupport3D),
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
            };
            Resource = _device.CreateTexture3D(in desc3D, 0, 0, 0);
            _stagingResource = 0;
            _resolveResource = 0;

            if (!IsDepthStencil)
            {
                if ((desc3D.BindFlags & D3D11BindFlags.ShaderResource) != 0)
                {
                    ShaderResourceView = _device.CreateShaderResourceView(Resource);
                }
                if ((desc3D.BindFlags & D3D11BindFlags.RenderTarget) != 0)
                {
                    RenderTargetView = _device.CreateRenderTargetView(Resource);
                }
                if ((desc3D.BindFlags & D3D11BindFlags.UnorderedAccess) != 0)
                {
                    UnorderedAccessView = _device.CreateUnorderedAccessView(Resource);
                }
            }
            else
            {
                DepthStencilView = _device.CreateDepthStencilView(Resource);
            }
            return;
        }

        D3D11Format nativeFormat = D3D11FormatConversion.ToDxgi(info.Format);
        D3D11FormatSupport formatSupport = _device.CheckFormatSupport(nativeFormat);
        D3D11BindFlags bindFlags = GetBindFlags(info, formatSupport);
        bool typeless = IsDepthStencil && (bindFlags & D3D11BindFlags.ShaderResource) != 0;
        
        D3D11Texture2DDesc desc = new()
        {
            Width = (uint)GetPhysicalWidth(info),
            Height = (uint)GetPhysicalHeight(info),
            MipLevels = info.Samples > 1 ? 1u : (uint)Math.Max(1, info.Levels),
            ArraySize = (uint)Math.Max(1, info.GetLayers()),
            Format = typeless ? D3D11FormatConversion.ToTypeless(info.Format) : nativeFormat,
            SampleCount = (uint)Math.Max(1, info.Samples),
            SampleQuality = 0,
            Usage = D3D11Usage.Default,
            BindFlags = bindFlags,
            CpuAccessFlags = D3D11CpuAccessFlags.None,
            MiscFlags = info.Target is Target.Cubemap or Target.CubemapArray
                ? D3D11ResourceMiscFlags.TextureCube
                : D3D11ResourceMiscFlags.None,
        };

        try
        {
            Resource = _device.CreateTexture2D(in desc, 0, 0);
        }
        catch (D3D11Exception exception) when ((uint)exception.HResultCode == 0x8007000Eu)
        {
            // Xbox Dev Mode has a small working-set budget. A large texture
            // created with SRV+RTV+UAV can be rejected even though the same
            // texture is valid with the views it actually needs. Retry in a
            // deterministic order, dropping UAV first (the most expensive
            // optional binding), then RTV. This keeps large read-only game
            // textures usable instead of terminating the GPU thread.
            D3D11BindFlags originalFlags = desc.BindFlags;
            D3D11BindFlags[] fallbacks =
            {
                originalFlags & ~D3D11BindFlags.UnorderedAccess,
                originalFlags & ~(D3D11BindFlags.UnorderedAccess | D3D11BindFlags.RenderTarget),
                originalFlags & D3D11BindFlags.ShaderResource,
            };

            foreach (D3D11BindFlags fallbackFlags in fallbacks)
            {
                if (fallbackFlags == originalFlags)
                {
                    continue;
                }

                desc.BindFlags = fallbackFlags;
                // Update typeless logic based on new flags
                bool newTypeless = IsDepthStencil && (fallbackFlags & D3D11BindFlags.ShaderResource) != 0;
                desc.Format = newTypeless ? D3D11FormatConversion.ToTypeless(info.Format) : nativeFormat;

                try
                {
                    Resource = _device.CreateTexture2D(in desc, 0, 0);
                    bindFlags = fallbackFlags;
                    typeless = newTypeless;
                    D3D11DebugTelemetry.Append(
                        $"texture-bind-fallback: size={desc.Width}x{desc.Height}, format={desc.Format}, " +
                        $"from={originalFlags}, to={fallbackFlags}, hresult=0x{exception.HResultCode:X8}\n");
                    break;
                }
                catch (D3D11Exception retryException) when ((uint)retryException.HResultCode == 0x8007000Eu)
                {
                    Resource = 0;
                }
            }

            if (Resource == 0)
            {
                desc.BindFlags = originalFlags;
                throw;
            }
        }

        _resolveResource = 0;

        if (!IsDepthStencil)
        {
            if ((bindFlags & D3D11BindFlags.ShaderResource) != 0)
            {
                ShaderResourceView = _device.CreateShaderResourceView(Resource);
            }
            if ((bindFlags & D3D11BindFlags.RenderTarget) != 0)
            {
                RenderTargetView = _device.CreateRenderTargetView(Resource);
            }
            if ((bindFlags & D3D11BindFlags.UnorderedAccess) != 0)
            {
                UnorderedAccessView = _device.CreateUnorderedAccessView(Resource);
            }
        }
        else
        {
            if ((bindFlags & D3D11BindFlags.DepthStencil) != 0)
            {
                if (typeless)
                {
                    D3D11DepthStencilViewDesc dsv = new()
                    {
                        Format = nativeFormat,
                        ViewDimension = info.Samples > 1 
                            ? (info.GetLayers() > 1 ? D3D11DsvDimension.Texture2DMultisampleArray : D3D11DsvDimension.Texture2DMultisample)
                            : (info.GetLayers() > 1 ? D3D11DsvDimension.Texture2DArray : D3D11DsvDimension.Texture2D)
                    };
                    if (info.GetLayers() > 1)
                    {
                        dsv.View = new D3D11DsvUnion { Texture2DArray = new D3D11Texture2DArrayDsv { ArraySize = (uint)info.GetLayers() } };
                    }
                    DepthStencilView = _device.CreateDepthStencilView(Resource, in dsv);
                }
                else
                {
                    DepthStencilView = _device.CreateDepthStencilView(Resource);
                }
            }

            if ((bindFlags & D3D11BindFlags.ShaderResource) != 0 && typeless)
            {
                D3D11ShaderResourceViewDesc srv = new()
                {
                    Format = D3D11FormatConversion.ToDepthSrv(info.Format),
                    ViewDimension = info.Samples > 1 
                        ? (info.GetLayers() > 1 ? D3D11SrvDimension.Texture2DMultisampleArray : D3D11SrvDimension.Texture2DMultisample)
                        : (info.GetLayers() > 1 ? D3D11SrvDimension.Texture2DArray : D3D11SrvDimension.Texture2D)
                };

                if (info.GetLayers() > 1)
                {
                    srv.View = new D3D11SrvUnion { Texture2DArray = new D3D11Texture2DArraySrv { MipLevels = desc.MipLevels, ArraySize = (uint)info.GetLayers() } };
                }
                else
                {
                    srv.View = new D3D11SrvUnion { Texture2D = new D3D11Texture2DSrv { MipLevels = desc.MipLevels } };
                }

                ShaderResourceView = _device.CreateShaderResourceView(Resource, in srv);
            }
        }

    }

    private D3D11Texture(
        D3D11Device device,
        D3D11DeviceContext context,
        TextureCreateInfo info,
        nint resource,
        Func<BufferHandle, D3D11BufferResource> bufferResolver,
        nint shaderResourceView,
        nint renderTargetView,
        nint depthStencilView,
        nint unorderedAccessView,
        D3D11Texture storageOwner,
        int viewFirstLayer = 0,
        int viewFirstLevel = 0,
        int viewLayerCount = 0,
        int viewLevelCount = 0,
        int resourceLayerCount = 0,
        int resourceLevelCount = 0)
    {
        _device = device;
        _context = context;
        _bufferResolver = bufferResolver;
        _storageOwner = storageOwner ?? this;
        _gpuBlit = storageOwner?._gpuBlit;
        _info = info;
        _ownsResource = false;
        _ownsViews = true;
        _viewFirstLayer = Math.Max(0, viewFirstLayer);
        _viewFirstLevel = Math.Max(0, viewFirstLevel);
        int defaultLayerCount = info.Target == Target.Texture3D ? info.Depth : info.GetLayers();
        _viewLayerCount = Math.Max(1, viewLayerCount == 0 ? defaultLayerCount : viewLayerCount);
        _viewLevelCount = Math.Max(1, viewLevelCount == 0 ? info.Levels : viewLevelCount);
        _resourceLayerCount = Math.Max(_viewFirstLayer + _viewLayerCount, resourceLayerCount == 0 ? defaultLayerCount : resourceLayerCount);
        _resourceLevelCount = Math.Max(_viewFirstLevel + _viewLevelCount, resourceLevelCount == 0 ? info.Levels : resourceLevelCount);
        _shadow = Array.Empty<byte>();
        _stagingResource = 0;
        Resource = resource;
        ShaderResourceView = shaderResourceView;
        RenderTargetView = renderTargetView;
        DepthStencilView = depthStencilView;
        UnorderedAccessView = unorderedAccessView;
        _resolveResource = 0;
    }

    public void CopyTo(ITexture destination, int firstLayer, int firstLevel)
    {
        CopyTo(destination, firstLayer, firstLayer, firstLevel, firstLevel);
    }

    public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
    {
        if (destination is not D3D11Texture other)
        {
            throw new ArgumentException("Destination must be a D3D11 texture.", nameof(destination));
        }

        int physicalSrcLevel = ResolveViewIndex(srcLevel, _viewFirstLevel, _viewLevelCount, _resourceLevelCount, nameof(srcLevel));
        int physicalDstLevel = ResolveViewIndex(dstLevel, other._viewFirstLevel, other._viewLevelCount, other._resourceLevelCount, nameof(dstLevel));
        int physicalSrcLayer = ResolveViewIndex(srcLayer, _viewFirstLayer, _viewLayerCount, _resourceLayerCount, nameof(srcLayer));
        int physicalDstLayer = ResolveViewIndex(dstLayer, other._viewFirstLayer, other._viewLayerCount, other._resourceLayerCount, nameof(dstLayer));
        bool source3D = _info.Target == Target.Texture3D;
        bool destination3D = other._info.Target == Target.Texture3D;

        if (source3D != destination3D)
        {
            throw new NotSupportedException("D3D11 copies between 3D textures and array textures require a shader copy.");
        }

        int srcWidth = Math.Max(1, _info.Width >> srcLevel);
        int srcHeight = Math.Max(1, _info.Height >> srcLevel);
        int dstWidth = Math.Max(1, other._info.Width >> dstLevel);
        int dstHeight = Math.Max(1, other._info.Height >> dstLevel);
        D3D11Box box = new()
        {
            Left = 0,
            Top = 0,
            Front = 0,
            Right = (uint)Math.Min(srcWidth, dstWidth),
            Bottom = (uint)Math.Min(srcHeight, dstHeight),
            Back = 1,
        };
        uint srcSubresource;
        uint dstSubresource;
        uint destinationZ = 0;
        if (source3D)
        {
            // D3D11 Texture3D has one subresource per mip; depth slices are
            // selected by the source box and DstZ, not by ArraySlice.
            int sourceMipDepth = Math.Max(1, _resourceLayerCount >> physicalSrcLevel);
            int destinationMipDepth = Math.Max(1, other._resourceLayerCount >> physicalDstLevel);
            if ((uint)physicalSrcLayer >= (uint)sourceMipDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(srcLayer), $"Texture3D source slice {physicalSrcLayer} exceeds mip {physicalSrcLevel} depth {sourceMipDepth}.");
            }
            if ((uint)physicalDstLayer >= (uint)destinationMipDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(dstLayer), $"Texture3D destination slice {physicalDstLayer} exceeds mip {physicalDstLevel} depth {destinationMipDepth}.");
            }

            srcSubresource = (uint)physicalSrcLevel;
            dstSubresource = (uint)physicalDstLevel;
            box.Front = (uint)physicalSrcLayer;
            box.Back = (uint)(physicalSrcLayer + 1);
            destinationZ = (uint)physicalDstLayer;
        }
        else
        {
            srcSubresource = GetD3DSubresource(physicalSrcLayer, physicalSrcLevel, _resourceLevelCount);
            dstSubresource = GetD3DSubresource(physicalDstLayer, physicalDstLevel, other._resourceLevelCount);
        }
        _context.CopySubresourceRegion(other.Resource, dstSubresource, 0, 0, destinationZ, Resource, srcSubresource, in box);
        other.NotifySubresourceWrite(physicalDstLayer, physicalDstLevel);
    }

    public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter)
    {
        int sourceWidth = srcRegion.X2 - srcRegion.X1;
        int sourceHeight = srcRegion.Y2 - srcRegion.Y1;
        int destinationWidth = dstRegion.X2 - dstRegion.X1;
        int destinationHeight = dstRegion.Y2 - dstRegion.Y1;

        if (destination is not D3D11Texture other)
        {
            throw new ArgumentException("Destination must be a D3D11 texture.", nameof(destination));
        }

        if (sourceWidth != destinationWidth || sourceHeight != destinationHeight ||
            sourceWidth < 0 || sourceHeight < 0 || _info.Format != other._info.Format)
        {
            if (_gpuBlit?.Invoke(this, other, srcRegion, dstRegion, linearFilter) == true) return;
            D3D11DebugTelemetry.CountCpuScaledCopyAttempt();
            D3D11DebugTelemetry.Append($"scaled-copy-cpu-fallback: src={PresentationDescription}; dst={other.PresentationDescription}; filter={linearFilter}\n");
            CopyScaledRegionCpu(other, srcRegion, dstRegion);
            return;
        }

        if (_info.IsCompressed || other._info.IsCompressed)
        {
            if (!_info.IsCompressed || !other._info.IsCompressed ||
                _info.BlockWidth != other._info.BlockWidth ||
                _info.BlockHeight != other._info.BlockHeight ||
                _info.BytesPerPixel != other._info.BytesPerPixel)
            {
                throw new NotSupportedException("Compressed copies require matching block formats.");
            }

            int blockWidth = Math.Max(1, _info.BlockWidth);
            int blockHeight = Math.Max(1, _info.BlockHeight);
            int sourceLevelWidth = Math.Max(1, _info.Width);
            int sourceLevelHeight = Math.Max(1, _info.Height);
            if (srcRegion.X1 % blockWidth != 0 || srcRegion.Y1 % blockHeight != 0 ||
                dstRegion.X1 % blockWidth != 0 || dstRegion.Y1 % blockHeight != 0 ||
                ((srcRegion.X2 % blockWidth != 0) && srcRegion.X2 != sourceLevelWidth) ||
                ((srcRegion.Y2 % blockHeight != 0) && srcRegion.Y2 != sourceLevelHeight) ||
                dstRegion.X2 % blockWidth != 0 || dstRegion.Y2 % blockHeight != 0)
            {
                throw new ArgumentException("Compressed copies must be aligned to complete blocks.");
            }
        }

        D3D11Box box = new()
        {
            Left = (uint)Math.Max(0, srcRegion.X1),
            Top = (uint)Math.Max(0, srcRegion.Y1),
            Front = 0,
            Right = (uint)Math.Max(0, srcRegion.X2),
            Bottom = (uint)Math.Max(0, srcRegion.Y2),
            Back = 1,
        };
        _context.CopySubresourceRegion(
            other.Resource,
            GetD3DSubresource(other._viewFirstLayer, other._viewFirstLevel, other._resourceLevelCount),
            (uint)Math.Max(0, dstRegion.X1),
            (uint)Math.Max(0, dstRegion.Y1),
            0,
            Resource,
            GetD3DSubresource(_viewFirstLayer, _viewFirstLevel, _resourceLevelCount),
            in box);
        other.NotifySubresourceWrite(other._viewFirstLayer, other._viewFirstLevel);
    }

    private void CopyScaledRegionCpu(D3D11Texture destination, Extents2D srcRegion, Extents2D dstRegion)
    {
        // Correctness fallback for GAL copies which require scaling before the
        // draw-texture blit pipeline is available. This deliberately handles
        // only ordinary, single-sample color surfaces. Keeping this bounded
        // avoids turning an unsupported state into a process-ending exception
        // during first-frame composition on Xbox.
        if (_info.IsCompressed || destination._info.IsCompressed ||
            IsDepthStencil || destination.IsDepthStencil ||
            _info.Samples != 1 || destination._info.Samples != 1 ||
            _info.Format != destination._info.Format ||
            _info.BytesPerPixel <= 0 || _info.BytesPerPixel != destination._info.BytesPerPixel)
        {
            throw new NotSupportedException(
                "Scaled D3D11 copies require matching, uncompressed, single-sample color formats.");
        }

        int sourceWidth = srcRegion.X2 - srcRegion.X1;
        int sourceHeight = srcRegion.Y2 - srcRegion.Y1;
        int destinationWidth = dstRegion.X2 - dstRegion.X1;
        int destinationHeight = dstRegion.Y2 - dstRegion.Y1;
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0 ||
            srcRegion.X1 < 0 || srcRegion.Y1 < 0 || srcRegion.X2 > _info.Width || srcRegion.Y2 > _info.Height ||
            dstRegion.X1 < 0 || dstRegion.Y1 < 0 || dstRegion.X2 > destination._info.Width || dstRegion.Y2 > destination._info.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(srcRegion), "Scaled copy regions must be positive and remain inside both textures.");
        }

        int bytesPerPixel = _info.BytesPerPixel;
        int sourcePitch = _info.GetMipStride(0);
        int uploadRowBytes = checked(destinationWidth * bytesPerPixel);
        int uploadPitch = BitUtils.AlignUp(uploadRowBytes, 4);
        MemoryOwner<byte> upload = MemoryOwner<byte>.Rent(checked(uploadPitch * destinationHeight));

        try
        {
            using PinnedSpan<byte> sourceData = GetData(0, 0);
            ReadOnlySpan<byte> source = sourceData.Get();
            Span<byte> output = upload.Span;

            // Nearest-neighbour works for normalized, integer and floating
            // formats because complete texels are copied without interpreting
            // their channel representation.
            for (int y = 0; y < destinationHeight; y++)
            {
                int sourceY = srcRegion.Y1 + (int)((long)y * sourceHeight / destinationHeight);
                for (int x = 0; x < destinationWidth; x++)
                {
                    int sourceX = srcRegion.X1 + (int)((long)x * sourceWidth / destinationWidth);
                    int sourceOffset = checked(sourceY * sourcePitch + sourceX * bytesPerPixel);
                    int destinationOffset = checked(y * uploadPitch + x * bytesPerPixel);
                    source.Slice(sourceOffset, bytesPerPixel).CopyTo(output.Slice(destinationOffset, bytesPerPixel));
                }
            }

            destination.SetData(
                upload,
                0,
                0,
                new Rectangle<int>(dstRegion.X1, dstRegion.Y1, destinationWidth, destinationHeight));
            upload = null;
        }
        finally
        {
            upload?.Dispose();
        }
    }

    public void CopyTo(BufferRange range, int layer, int level, int stride)
    {
        if (_bufferResolver is null)
        {
            throw new InvalidOperationException("The texture has no buffer resolver for texture-to-buffer copies.");
        }

        using PinnedSpan<byte> data = GetData(layer, level);
        ReadOnlySpan<byte> source = data.Get();
        int copySize = Math.Min(range.Size, source.Length);
        if (stride > 0)
        {
            copySize = Math.Min(copySize, checked(stride * Math.Max(1, _info.Height >> level)));
        }
        _bufferResolver(range.Handle).SetData(range.Offset, source[..copySize]);
    }

    public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
    {
        D3D11DebugTelemetry.TraceTextureConfiguration("view", info);

        if (_info.Target is Target.Texture1D or Target.Texture1DArray || info.Target is Target.Texture1D or Target.Texture1DArray)
        {
            return Create1DView(info, firstLayer, firstLevel);
        }

        if (_info.Target == Target.Texture3D || info.Target == Target.Texture3D)
        {
            return Create3DView(info, firstLayer, firstLevel);
        }

        int sourceLayers = Math.Max(1, _info.GetLayers());
        int viewLayers = Math.Max(1, info.GetLayers());
        bool isMultisample = _info.Samples > 1;
        // D3D11 multisample resources are created with exactly one mip level,
        // even when the guest view metadata still carries the Vulkan mip count.
        // Feeding that guest count into an RTV/SRV descriptor is a common
        // source of E_INVALIDARG on the Xbox runtime.
        int sourceLevels = isMultisample ? 1 : Math.Max(1, _info.Levels);
        int viewLevels = isMultisample ? 1 : Math.Max(1, info.Levels);

        if (firstLayer < 0 || firstLayer + viewLayers > sourceLayers || firstLevel < 0 || firstLevel + viewLevels > sourceLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLayer), "The D3D11 view range exceeds the parent texture.");
        }

        D3D11Format format = D3D11FormatConversion.ToDxgi(info.Format);
        bool isArray = sourceLayers > 1;
        // The parent resource may have been created with TEXTURECUBE, but a GAL
        // view can legally reinterpret only one face (or an arbitrary face
        // range) as Texture2D/Texture2DArray. Select the SRV dimension from the
        // requested view target, as Vulkan does, rather than forcing every view
        // of a cubemap resource to contain complete groups of six faces.
        bool isCube = info.Target is Target.Cubemap or Target.CubemapArray;
        bool isCubeArray = info.Target == Target.CubemapArray;
        nint shaderResourceView = 0;
        nint renderTargetView = 0;
        nint depthStencilView = 0;
        nint unorderedAccessView = 0;

        if (!IsDepthStencil)
        {
            D3D11ShaderResourceViewDesc srv = new()
            {
                Format = format,
                ViewDimension = isMultisample
                    ? (isArray ? D3D11SrvDimension.Texture2DMultisampleArray : D3D11SrvDimension.Texture2DMultisample)
                    : isCube
                        ? (isCubeArray ? D3D11SrvDimension.TextureCubeArray : D3D11SrvDimension.TextureCube)
                        : (isArray ? D3D11SrvDimension.Texture2DArray : D3D11SrvDimension.Texture2D),
            };

            if (isMultisample)
            {
                srv.View = new D3D11SrvUnion
                {
                    Texture2DArray = new D3D11Texture2DArraySrv
                    {
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                };
            }
            else if (isCube)
            {
                if (firstLayer % 6 != 0 || viewLayers % 6 != 0)
                {
                    throw new ArgumentException("Cubemap views must start and end on complete six-face cubes.");
                }
                srv.View = isCubeArray
                    ? new D3D11SrvUnion
                    {
                        TextureCubeArray = new D3D11TextureCubeArraySrv
                        {
                            MostDetailedMip = (uint)firstLevel,
                            MipLevels = (uint)viewLevels,
                            First2DArrayFace = (uint)firstLayer,
                            NumCubes = (uint)Math.Max(1, viewLayers / 6),
                        },
                    }
                    : new D3D11SrvUnion
                    {
                        TextureCube = new D3D11TextureCubeSrv
                        {
                            MostDetailedMip = (uint)firstLevel,
                            MipLevels = (uint)viewLevels,
                        },
                    };
            }
            else if (isArray)
            {
                srv.View = new D3D11SrvUnion
                {
                    Texture2DArray = new D3D11Texture2DArraySrv
                    {
                        MostDetailedMip = (uint)firstLevel,
                        MipLevels = (uint)viewLevels,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                };
            }
            else
            {
                srv.View = new D3D11SrvUnion
                {
                    Texture2D = new D3D11Texture2DSrv
                    {
                        MostDetailedMip = (uint)firstLevel,
                        MipLevels = (uint)viewLevels,
                    },
                };
            }

            if (ShaderResourceView != 0)
            {
                shaderResourceView = CreateShaderResourceViewForView(in srv, info, firstLayer, firstLevel);
            }

            D3D11RenderTargetViewDesc rtv = new()
            {
                Format = format,
                ViewDimension = isMultisample
                    ? (isArray ? D3D11RtvDimension.Texture2DMultisampleArray : D3D11RtvDimension.Texture2DMultisample)
                    : (isArray ? D3D11RtvDimension.Texture2DArray : D3D11RtvDimension.Texture2D),
            };

            if (isArray)
            {
                rtv.View = new D3D11RtvUnion
                {
                    Texture2DArray = new D3D11Texture2DArrayRtv
                    {
                        MipSlice = (uint)firstLevel,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                };
            }
            else
            {
                rtv.View = new D3D11RtvUnion
                {
                    Texture2D = new D3D11Texture2DRtv { MipSlice = (uint)firstLevel },
                };
            }
            if (RenderTargetView != 0)
            {
                renderTargetView = CreateRenderTargetViewForView(in rtv, info, firstLayer, firstLevel, viewLayers, viewLevels);
            }

            if (!isMultisample && UnorderedAccessView != 0)
            {
                D3D11UnorderedAccessViewDesc uav = new()
                {
                    Format = format,
                    ViewDimension = isArray ? D3D11UavDimension.Texture2DArray : D3D11UavDimension.Texture2D,
                };
                if (isArray)
                {
                    uav.View = new D3D11UavUnion
                    {
                        Texture2DArray = new D3D11Texture2DArrayUav
                        {
                            MipSlice = (uint)firstLevel,
                            FirstArraySlice = (uint)firstLayer,
                            ArraySize = (uint)viewLayers,
                        },
                    };
                }
                else
                {
                    uav.View = new D3D11UavUnion
                    {
                        Texture2D = new D3D11Texture2DUav { MipSlice = (uint)firstLevel },
                    };
                }
                unorderedAccessView = CreateUnorderedAccessViewForView(in uav, info, firstLayer, firstLevel, viewLayers, viewLevels);
            }
        }
        else
        {
            D3D11DepthStencilViewDesc dsv = new()
            {
                Format = format,
                ViewDimension = isMultisample
                    ? (isArray ? D3D11DsvDimension.Texture2DMultisampleArray : D3D11DsvDimension.Texture2DMultisample)
                    : (isArray ? D3D11DsvDimension.Texture2DArray : D3D11DsvDimension.Texture2D),
            };
            if (isArray)
            {
                dsv.View = new D3D11DsvUnion
                {
                    Texture2DArray = new D3D11Texture2DArrayDsv
                    {
                        MipSlice = (uint)firstLevel,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                };
            }
            else
            {
                dsv.View = new D3D11DsvUnion
                {
                    Texture2D = new D3D11Texture2DDsv { MipSlice = (uint)firstLevel },
                };
            }
            depthStencilView = CreateDepthStencilViewForView(in dsv, info, firstLayer, firstLevel, viewLayers, viewLevels);
        }

        return new D3D11Texture(_device, _context, info, Resource, _bufferResolver, shaderResourceView, renderTargetView, depthStencilView, unorderedAccessView, _storageOwner, firstLayer, firstLevel, viewLayers, viewLevels, _resourceLayerCount, _resourceLevelCount);
    }

    private nint CreateShaderResourceViewForView(
        in D3D11ShaderResourceViewDesc desc,
        TextureCreateInfo viewInfo,
        int firstLayer,
        int firstLevel)
    {
        try
        {
            return _device.CreateShaderResourceView(Resource, in desc);
        }
        catch (D3D11Exception exception) when ((uint)exception.HResultCode == 0x80070057)
        {
            // A few Switch texture views have no exact D3D11 equivalent (for
            // example a reinterpretation of an array/cube or a typed view
            // that the Xbox D3D11 runtime rejects). Do not terminate the GPU
            // thread for one incompatible view: retain the resource's legal
            // default SRV as a conservative fallback. The shader may sample
            // a broader range than requested, but the session remains alive
            // and the diagnostic identifies the offending view precisely.
            D3D11DebugTelemetry.Append(
                $"srv-view-fallback: hresult=0x{exception.HResultCode:X8}, " +
                $"resource=0x{Resource.ToInt64():X}, resourceDimension={GetResourceDimension()}, " +
                $"resourceFormat={D3D11FormatConversion.ToDxgi(_info.Format)}, resourceSize={_info.Width}x{_info.Height}x{_info.Depth}, " +
                $"resourceLayers={_info.GetLayers()}, resourceLevels={_info.Levels}, resourceSamples={_info.Samples}, " +
                $"viewFormat={desc.Format}, viewDimension={desc.ViewDimension}, viewTarget={viewInfo.Target}, viewGalFormat={viewInfo.Format}, " +
                $"viewLayers={viewInfo.GetLayers()}, viewLevels={viewInfo.Levels}, firstLayer={firstLayer}, firstLevel={firstLevel}\n");

            return D3D11Com.AddRef(ShaderResourceView);
        }
    }

    private nint CreateRenderTargetViewForView(
        in D3D11RenderTargetViewDesc desc,
        TextureCreateInfo viewInfo,
        int firstLayer,
        int firstLevel,
        int viewLayers,
        int viewLevels)
    {
        try
        {
            return _device.CreateRenderTargetView(Resource, in desc);
        }
        catch (D3D11Exception exception) when ((uint)exception.HResultCode == 0x80070057)
        {
            D3D11DebugTelemetry.Append(
                $"rtv-view-fallback: hresult=0x{exception.HResultCode:X8}, resource=0x{Resource.ToInt64():X}, " +
                $"resourceDimension={GetResourceDimension()}, resourceFormat={D3D11FormatConversion.ToDxgi(_info.Format)}, " +
                $"resourceSize={_info.Width}x{_info.Height}x{_info.Depth}, resourceLayers={_info.GetLayers()}, " +
                $"resourceLevels={_info.Levels}, resourceSamples={_info.Samples}, viewFormat={desc.Format}, " +
                $"viewDimension={desc.ViewDimension}, viewTarget={viewInfo.Target}, viewGalFormat={viewInfo.Format}, " +
                $"viewLayers={viewLayers}, viewLevels={viewLevels}, firstLayer={firstLayer}, firstLevel={firstLevel}\n");

            // A parent RTV is a legal descriptor for the same resource and is
            // preferable to aborting the GPU FIFO. This is deliberately a
            // generic fallback: it is used only after the runtime rejects the
            // requested typed/subresource view with E_INVALIDARG.
            return D3D11Com.AddRef(RenderTargetView);
        }
    }

    private nint CreateUnorderedAccessViewForView(
        in D3D11UnorderedAccessViewDesc desc,
        TextureCreateInfo viewInfo,
        int firstLayer,
        int firstLevel,
        int viewLayers,
        int viewLevels)
    {
        try
        {
            return _device.CreateUnorderedAccessView(Resource, in desc);
        }
        catch (D3D11Exception exception) when ((uint)exception.HResultCode == 0x80070057)
        {
            D3D11DebugTelemetry.Append(
                $"uav-view-fallback: hresult=0x{exception.HResultCode:X8}, resource=0x{Resource.ToInt64():X}, " +
                $"resourceDimension={GetResourceDimension()}, resourceFormat={D3D11FormatConversion.ToDxgi(_info.Format)}, " +
                $"resourceSize={_info.Width}x{_info.Height}x{_info.Depth}, resourceLayers={_info.GetLayers()}, " +
                $"resourceLevels={_info.Levels}, resourceSamples={_info.Samples}, viewFormat={desc.Format}, " +
                $"viewDimension={desc.ViewDimension}, viewTarget={viewInfo.Target}, viewGalFormat={viewInfo.Format}, " +
                $"viewLayers={viewLayers}, viewLevels={viewLevels}, firstLayer={firstLayer}, firstLevel={firstLevel}\n");
            return D3D11Com.AddRef(UnorderedAccessView);
        }
    }

    private nint CreateDepthStencilViewForView(
        in D3D11DepthStencilViewDesc desc,
        TextureCreateInfo viewInfo,
        int firstLayer,
        int firstLevel,
        int viewLayers,
        int viewLevels)
    {
        try
        {
            return _device.CreateDepthStencilView(Resource, in desc);
        }
        catch (D3D11Exception exception) when ((uint)exception.HResultCode == 0x80070057)
        {
            D3D11DebugTelemetry.Append(
                $"dsv-view-fallback: hresult=0x{exception.HResultCode:X8}, resource=0x{Resource.ToInt64():X}, " +
                $"resourceDimension={GetResourceDimension()}, resourceFormat={D3D11FormatConversion.ToDxgi(_info.Format)}, " +
                $"resourceSize={_info.Width}x{_info.Height}x{_info.Depth}, resourceLayers={_info.GetLayers()}, " +
                $"resourceLevels={_info.Levels}, resourceSamples={_info.Samples}, viewFormat={desc.Format}, " +
                $"viewDimension={desc.ViewDimension}, viewTarget={viewInfo.Target}, viewGalFormat={viewInfo.Format}, " +
                $"viewLayers={viewLayers}, viewLevels={viewLevels}, firstLayer={firstLayer}, firstLevel={firstLevel}\n");
            return D3D11Com.AddRef(DepthStencilView);
        }
    }

    private string GetResourceDimension() => _info.Target switch
    {
        Target.Texture1D or Target.Texture1DArray => "TEXTURE1D",
        Target.Texture3D => "TEXTURE3D",
        Target.Cubemap or Target.CubemapArray => "TEXTURE2D_CUBE",
        _ => "TEXTURE2D",
    };

    public unsafe PinnedSpan<byte> GetData()
    {
        if (!_ownsResource)
        {
            return ReadbackView();
        }

        EnsureReadbackAvailable();
        ReadbackAll();
        return Pin(_shadow);
    }

    public unsafe PinnedSpan<byte> GetData(int layer, int level)
    {
        if (!_ownsResource)
        {
            int physicalLayer = ResolveViewIndex(layer, _viewFirstLayer, _viewLayerCount, _resourceLayerCount, nameof(layer));
            int physicalLevel = ResolveViewIndex(level, _viewFirstLevel, _viewLevelCount, _resourceLevelCount, nameof(level));
            return _storageOwner.GetData(physicalLayer, physicalLevel);
        }

        EnsureReadbackAvailable();
        int offset = GetSubresourceOffset(layer, level);
        ReadbackSubresource(layer, level, offset);
        int size = GetSubresourceSize(level);
        GCHandle pin = GCHandle.Alloc(_shadow, GCHandleType.Pinned);
        byte* pointer = (byte*)pin.AddrOfPinnedObject() + offset;
        return new PinnedSpan<byte>(pointer, size, pin.Free);
    }

    public void SetData(MemoryOwner<byte> data)
    {
        D3D11DebugTelemetry.TraceTextureUpload("full", _info, data.Length);
        try
        {
            int levels = Math.Max(1, _info.Levels);
            int layers = Math.Max(1, _info.GetLayers());
            for (int level = 0; level < levels; level++)
            {
                int mipSize = GetSubresourceSize(level);
                int layerCount = _info.Target == Target.Texture3D ? 1 : layers;
                for (int layer = 0; layer < layerCount; layer++)
                {
                    int offset = GetSubresourceOffset(layer, level);
                    // Match the established Vulkan upload behavior: a caller
                    // may provide only the complete leading mip levels. Never
                    // over-read a truncated final subresource.
                    if (offset > data.Length - mipSize)
                    {
                        return;
                    }
                    ApplyRgba8Swizzle(
                        data.Span.Slice(offset, mipSize),
                        Math.Max(1, _info.Width >> level),
                        Math.Max(1, _info.Height >> level),
                        (int)_info.GetMipStride(level),
                        _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : 1);
                    _context.UpdateSubresource(
                        Resource,
                        data.Span.Slice(offset, mipSize),
                        GetUploadSubresource(layer, level),
                        (uint)_info.GetMipStride(level),
                        _info.Target == Target.Texture3D ? (uint)_info.GetMipSize2D(level) : 0);
                }
            }
        }
        finally
        {
            data.Dispose();
        }
        NotifyWrite();
    }

    public void SetData(MemoryOwner<byte> data, int layer, int level)
    {
        D3D11DebugTelemetry.TraceTextureUpload($"mip layer={layer} level={level}", _info, data.Length);
        try
        {
            int mipSize = GetSubresourceSize(level);
            if (data.Length < mipSize)
            {
                throw new ArgumentException("The mip upload does not contain enough data.", nameof(data));
            }
            ApplyRgba8Swizzle(
                data.Span[..mipSize],
                Math.Max(1, _info.Width >> level),
                Math.Max(1, _info.Height >> level),
                (int)_info.GetMipStride(level),
                _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : 1);
            _context.UpdateSubresource(
                Resource,
                data.Span[..mipSize],
                GetUploadSubresource(layer, level),
                (uint)_info.GetMipStride(level),
                _info.Target == Target.Texture3D ? (uint)_info.GetMipSize2D(level) : 0);
        }
        finally
        {
            data.Dispose();
        }
        NotifySubresourceWrite(_viewFirstLayer + layer, _viewFirstLevel + level);
    }

    public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
    {
        D3D11DebugTelemetry.TraceTextureUpload($"region layer={layer} level={level}", _info, data.Length);
        try
        {
            int levelWidth = Math.Max(1, _info.Width >> level);
            int levelHeight = Math.Max(1, _info.Height >> level);
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                region.X + region.Width > levelWidth || region.Y + region.Height > levelHeight)
            {
                throw new ArgumentOutOfRangeException(nameof(region));
            }

            int blockWidth = _info.IsCompressed ? Math.Max(1, _info.BlockWidth) : 1;
            int blockHeight = _info.IsCompressed ? Math.Max(1, _info.BlockHeight) : 1;
            if (_info.IsCompressed &&
                (region.X % blockWidth != 0 || region.Y % blockHeight != 0 ||
                 ((region.X + region.Width) % blockWidth != 0 && region.X + region.Width != levelWidth) ||
                 ((region.Y + region.Height) % blockHeight != 0 && region.Y + region.Height != levelHeight)))
            {
                throw new ArgumentException("Compressed texture updates must be aligned to complete blocks.", nameof(region));
            }

            int uploadWidth = _info.IsCompressed
                ? (region.Width + blockWidth - 1) / blockWidth
                : region.Width;
            int uploadHeight = _info.IsCompressed
                ? (region.Height + blockHeight - 1) / blockHeight
                : region.Height;
            int rowBytes = checked(uploadWidth * _info.BytesPerPixel);
            int rowPitch = BitUtils.AlignUp(rowBytes, 4);
            int expectedSize = checked(rowPitch * uploadHeight);
            if (data.Length < expectedSize)
            {
                throw new ArgumentException("The partial texture upload does not contain enough row data.", nameof(data));
            }
            ApplyRgba8Swizzle(data.Span[..expectedSize], region.Width, region.Height, rowPitch, 1);

            D3D11Box box = new()
            {
                Left = (uint)region.X,
                Top = (uint)region.Y,
                Front = 0,
                Right = (uint)(region.X + region.Width),
                Bottom = (uint)(region.Y + region.Height),
                Back = 1,
            };
            _context.UpdateSubresource(
                Resource,
                data.Span[..expectedSize],
                in box,
                GetUploadSubresource(layer, level),
                (uint)rowPitch);
        }
        finally
        {
            data.Dispose();
        }
        NotifySubresourceWrite(_viewFirstLayer + layer, _viewFirstLevel + level);
    }

    private uint GetUploadSubresource(int layer, int level)
    {
        if ((uint)layer >= (uint)_viewLayerCount || (uint)level >= (uint)_viewLevelCount)
            throw new ArgumentOutOfRangeException(nameof(layer), "Upload is outside the texture view.");
        return GetD3DSubresource(_info.Target == Target.Texture3D ? 0 : _viewFirstLayer + layer,
            _viewFirstLevel + level, _resourceLevelCount);
    }

    /// <summary>
    /// Applies the guest texture view swizzle to an RGBA8 upload.
    ///
    /// D3D11 has no texture-view component mapping equivalent to Vulkan's
    /// VkComponentMapping. The compatibility layer can therefore expand a
    /// packed Switch texture to RGBA8, but without this final step a descriptor
    /// such as Blue/Green/Red/One still arrives with red and blue exchanged (or
    /// an undefined alpha). Keep this conversion local to the D3D11 resource so
    /// Vulkan's native image-view swizzle remains untouched.
    /// </summary>
    private void ApplyRgba8Swizzle(Span<byte> data, int width, int height, int rowPitch, int depth)
    {
        if (_info.IsCompressed || _info.BlockWidth != 1 || _info.BlockHeight != 1 ||
            _info.BytesPerPixel != 4 || _info.Format is not (Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb) ||
            (_info.SwizzleR == SwizzleComponent.Red &&
             _info.SwizzleG == SwizzleComponent.Green &&
             _info.SwizzleB == SwizzleComponent.Blue &&
             _info.SwizzleA == SwizzleComponent.Alpha))
        {
            return;
        }

        D3D11DebugTelemetry.TraceTextureSwizzle(_info);
        // Preserve component mapping exactly, including Zero/Zero/Zero/Red.
        // Video shaders sample these Y/U/V planes through .w; replacing alpha
        // with one destroys the plane values before the YUV conversion.
        int slicePitch = checked(rowPitch * height);
        for (int z = 0; z < depth; z++)
        {
            int sliceOffset = z * slicePitch;
            if (sliceOffset >= data.Length)
            {
                break;
            }

            Span<byte> slice = data[sliceOffset..Math.Min(data.Length, sliceOffset + slicePitch)];
            int rowBytes = Math.Min(width * 4, rowPitch);
            for (int y = 0; y < height && y * rowPitch < slice.Length; y++)
            {
                Span<byte> row = slice.Slice(y * rowPitch, Math.Min(rowBytes, slice.Length - y * rowPitch));
                for (int x = 0; x + 3 < row.Length; x += 4)
                {
                    byte red = row[x];
                    byte green = row[x + 1];
                    byte blue = row[x + 2];
                    byte alpha = row[x + 3];
                    row[x] = ReadSwizzleComponent(_info.SwizzleR, red, green, blue, alpha);
                    row[x + 1] = ReadSwizzleComponent(_info.SwizzleG, red, green, blue, alpha);
                    row[x + 2] = ReadSwizzleComponent(_info.SwizzleB, red, green, blue, alpha);
                    row[x + 3] = ReadSwizzleComponent(_info.SwizzleA, red, green, blue, alpha);
                }
            }
        }
    }

    private static byte ReadSwizzleComponent(SwizzleComponent component, byte red, byte green, byte blue, byte alpha)
    {
        return component switch
        {
            SwizzleComponent.Zero => 0,
            SwizzleComponent.One => 255,
            SwizzleComponent.Red => red,
            SwizzleComponent.Green => green,
            SwizzleComponent.Blue => blue,
            SwizzleComponent.Alpha => alpha,
            _ => 0,
        };
    }

    public void SetStorage(BufferRange buffer)
    {
        if (_info.Target != Target.TextureBuffer || _bufferResolver is null)
        {
            throw new InvalidOperationException("SetStorage is only valid for a D3D11 texture-buffer resource.");
        }

        D3D11BufferResource resource = _bufferResolver(buffer.Handle);
        int elementSize = Math.Max(1, _info.BytesPerPixel);
        if (buffer.Offset < 0 || buffer.Size <= 0 || buffer.Offset > resource.Size - buffer.Size ||
            buffer.Offset % elementSize != 0 || buffer.Size % elementSize != 0)
        {
            throw new ArgumentException("Texture-buffer range must be valid and aligned to complete format elements.", nameof(buffer));
        }

        D3D11Com.Release(UnorderedAccessView);
        D3D11Com.Release(ShaderResourceView);

        Resource = resource.Resource;
        D3D11Format format = D3D11FormatConversion.ToDxgi(_info.Format);
        uint firstElement = (uint)(buffer.Offset / elementSize);
        uint elementCount = (uint)(buffer.Size / elementSize);

        D3D11ShaderResourceViewDesc srv = new()
        {
            Format = format,
            ViewDimension = D3D11SrvDimension.Buffer,
            View = new D3D11SrvUnion
            {
                BufferExtended = new D3D11BufferExtendedSrv
                {
                    FirstElement = firstElement,
                    NumElements = elementCount,
                    Flags = 0,
                },
            },
        };
        ShaderResourceView = _device.CreateShaderResourceView(Resource, in srv);

        D3D11FormatSupport support = _device.CheckFormatSupport(format);
        if ((support & D3D11FormatSupport.TypedUnorderedAccessView) != 0)
        {
            D3D11UnorderedAccessViewDesc uav = new()
            {
                Format = format,
                ViewDimension = D3D11UavDimension.Buffer,
                View = new D3D11UavUnion
                {
                    Buffer = new D3D11BufferUav
                    {
                        FirstElement = firstElement,
                        NumElements = elementCount,
                        Flags = 0,
                    },
                },
            };
            UnorderedAccessView = _device.CreateUnorderedAccessView(Resource, in uav);
        }
        else
        {
            UnorderedAccessView = 0;
        }
    }

    public void Release()
    {
        if (_feedbackSnapshot != null)
        {
            if (--_feedbackSnapshot.References == 0)
            {
                _storageOwner._feedbackSnapshots.Remove((_viewFirstLevel, _viewFirstLayer, _info.Format));
                _feedbackSnapshot.Texture.Release();
            }
            _feedbackSnapshot = null;
        }
        if (_ownsViews)
        {
            D3D11Com.Release(UnorderedAccessView);
            D3D11Com.Release(ShaderResourceView);
            D3D11Com.Release(RenderTargetView);
            D3D11Com.Release(DepthStencilView);
        }
        D3D11Com.Release(_stagingResource);
        D3D11Com.Release(_resolveResource);

        if (_ownsResource)
        {
            D3D11Com.Release(Resource);
        }
    }

    internal nint TryGetFeedbackCopyView(bool refresh)
    {
        // Single 2D subresource views may select any physical mip/array layer.
        // Multi-subresource, multisample and depth snapshots need separate paths.
        if (IsDepthStencil || Resource == 0 || _info.Target != Target.Texture2D ||
            _info.Samples > 1 || _info.Levels != 1 || _info.GetLayers() != 1 ||
            _viewLayerCount != 1 || _viewLevelCount != 1)
        {
            return 0;
        }

        try
        {
            bool created = false;
            if (_feedbackSnapshot is null)
            {
                var key = (_viewFirstLevel, _viewFirstLayer, _info.Format);
                if (!_storageOwner._feedbackSnapshots.TryGetValue(key, out _feedbackSnapshot))
                {
                TextureCreateInfo copyInfo = new(
                    _info.Width,
                    _info.Height,
                    1,
                    1,
                    1,
                    _info.BlockWidth,
                    _info.BlockHeight,
                    _info.BytesPerPixel,
                    _info.Format,
                    _info.DepthStencilMode,
                    Target.Texture2D,
                    SwizzleComponent.Red,
                    SwizzleComponent.Green,
                    SwizzleComponent.Blue,
                    SwizzleComponent.Alpha);
                    _feedbackSnapshot = new FeedbackSnapshot
                    {
                        Texture = new D3D11Texture(_device, _context, copyInfo, _bufferResolver),
                    };
                    _storageOwner._feedbackSnapshots.Add(key, _feedbackSnapshot);
                    created = true;
                }
                _feedbackSnapshot.References++;
            }

            // Only re-copy when the source has been written since the last copy.
            // This avoids the catastrophic per-frame CopyResource cost when the
            // texture is bound as both SRV and RTV but the RT has not been touched.
            if (_feedbackSnapshot.Generation < 0 || (refresh && WriteGeneration != _feedbackSnapshot.Generation))
            {
                int width  = Math.Max(1, _info.Width);
                int height = Math.Max(1, _info.Height);
                D3D11Box box = new()
                {
                    Left   = 0,
                    Top    = 0,
                    Front  = 0,
                    Right  = (uint)width,
                    Bottom = (uint)height,
                    Back   = 1,
                };
                long started = Stopwatch.GetTimestamp();
                _context.CopySubresourceRegion(_feedbackSnapshot.Texture.Resource, 0, 0, 0, 0, Resource,
                    GetD3DSubresource(_viewFirstLayer, _viewFirstLevel, _resourceLevelCount), in box);
                D3D11DebugTelemetry.CountFeedbackSubmission(Stopwatch.GetElapsedTime(started).TotalMilliseconds, created);
                _feedbackSnapshot.Generation = WriteGeneration;
                long bytesCopied = (long)((width + Math.Max(1, _info.BlockWidth) - 1) / Math.Max(1, _info.BlockWidth)) *
                    ((height + Math.Max(1, _info.BlockHeight) - 1) / Math.Max(1, _info.BlockHeight)) * Math.Max(1, _info.BytesPerPixel);
                bool isFullCopy = width == _info.Width && height == _info.Height;
                D3D11DebugTelemetry.CountHazardFeedbackCopy(bytesCopied, isFullCopy);
                D3D11DebugTelemetry.TraceFeedbackCopy(
                    Resource,
                    _feedbackSnapshot.Texture.Resource,
                    _info.Width,
                    _info.Height,
                    WriteGeneration,
                    bytesCopied,
                    isFullCopy, _viewFirstLevel, _viewFirstLayer, created ? "created" : "source-write");
            }

            else D3D11DebugTelemetry.CountHazardFeedbackReuse();
            return _feedbackSnapshot.Texture.ShaderResourceView;
        }
        catch (Exception exception)
        {
            D3D11DebugTelemetry.Append(
                $"hazard-feedback-copy-failed: source=0x{Resource.ToInt64():X}, " +
                $"reason={exception.GetType().Name}:{exception.Message}\n");
            return 0;
        }
    }

    private D3D11Texture2DDesc CreateStagingDesc(TextureCreateInfo info)
    {
        return new D3D11Texture2DDesc
        {
            Width = (uint)GetPhysicalWidth(info),
            Height = (uint)GetPhysicalHeight(info),
            MipLevels = info.Samples > 1 ? 1u : (uint)Math.Max(1, info.Levels),
            ArraySize = (uint)Math.Max(1, info.GetLayers()),
            Format = D3D11FormatConversion.ToDxgi(info.Format),
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11Usage.Staging,
            BindFlags = 0,
            CpuAccessFlags = D3D11CpuAccessFlags.Read,
            MiscFlags = D3D11ResourceMiscFlags.None,
        };
    }

    private static int GetPhysicalWidth(TextureCreateInfo info)
    {
        int width = Math.Max(1, info.Width);
        return info.IsCompressed
            ? BitUtils.AlignUp(width, Math.Max(1, info.BlockWidth))
            : width;
    }

    private static int GetPhysicalHeight(TextureCreateInfo info)
    {
        int height = Math.Max(1, info.Height);
        return info.IsCompressed
            ? BitUtils.AlignUp(height, Math.Max(1, info.BlockHeight))
            : height;
    }

    private unsafe PinnedSpan<byte> ReadbackView()
    {
        int requiredSize = GetDataSize(_info);
        if (_shadow.Length != requiredSize)
        {
            _shadow = new byte[requiredSize];
        }

        int destinationOffset = 0;
        int levels = Math.Max(1, _viewLevelCount);
        int layers = _info.Target == Target.Texture3D ? 1 : Math.Max(1, _viewLayerCount);
        for (int level = 0; level < levels; level++)
        {
            int subresourceSize = GetSubresourceSize(level);
            for (int layer = 0; layer < layers; layer++)
            {
                using PinnedSpan<byte> sourceData = _storageOwner.GetData(_viewFirstLayer + layer, _viewFirstLevel + level);
                ReadOnlySpan<byte> source = sourceData.Get();
                int copySize = Math.Min(subresourceSize, source.Length);
                source[..copySize].CopyTo(_shadow.AsSpan(destinationOffset, copySize));
                if (copySize < subresourceSize)
                {
                    _shadow.AsSpan(destinationOffset + copySize, subresourceSize - copySize).Clear();
                }
                destinationOffset += subresourceSize;
            }
        }

        D3D11DebugTelemetry.Append(
            $"subresource-view-readback: firstLayer={_viewFirstLayer}, layers={_viewLayerCount}, " +
            $"firstLevel={_viewFirstLevel}, levels={_viewLevelCount}, bytes={destinationOffset}\n");
        return Pin(_shadow);
    }

    private void EnsureReadbackAvailable()
    {
        if (!_ownsResource)
        {
            throw new NotSupportedException("Readback is only available on an owning D3D11 texture, not on a subresource view.");
        }

        if (_info.Samples > 1 && IsDepthStencil)
        {
            throw new NotSupportedException("MSAA depth/stencil readback requires a shader resolve path.");
        }

        if (_shadow.Length == 0)
        {
            _shadow = new byte[GetDataSize(_info)];
        }

        if (_info.Samples > 1 && _resolveResource == 0)
        {
            D3D11Texture2DDesc resolveDesc = new()
            {
                Width = (uint)GetPhysicalWidth(_info),
                Height = (uint)GetPhysicalHeight(_info),
                MipLevels = 1,
                ArraySize = (uint)Math.Max(1, _info.GetLayers()),
                Format = D3D11FormatConversion.ToDxgi(_info.Format),
                SampleCount = 1,
                SampleQuality = 0,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.None,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
            };
            _resolveResource = _device.CreateTexture2D(in resolveDesc, 0, 0);
        }

        if (_stagingResource == 0)
        {
            if (_info.Target is Target.Texture1D or Target.Texture1DArray)
            {
                D3D11Texture1DDesc stagingDesc = new()
                {
                    Width = (uint)Math.Max(1, _info.Width),
                    MipLevels = (uint)Math.Max(1, _info.Levels),
                    ArraySize = (uint)Math.Max(1, _info.GetLayers()),
                    Format = D3D11FormatConversion.ToDxgi(_info.Format),
                    Usage = D3D11Usage.Staging,
                    BindFlags = D3D11BindFlags.None,
                    CpuAccessFlags = D3D11CpuAccessFlags.Read,
                    MiscFlags = D3D11ResourceMiscFlags.None,
                };
                _stagingResource = _device.CreateTexture1D(in stagingDesc, 0, 0);
            }
            else if (_info.Target == Target.Texture3D)
            {
                D3D11Texture3DDesc stagingDesc = new()
                {
                    Width = (uint)Math.Max(1, _info.Width),
                    Height = (uint)Math.Max(1, _info.Height),
                    Depth = (uint)Math.Max(1, _info.Depth),
                    MipLevels = (uint)Math.Max(1, _info.Levels),
                    Format = D3D11FormatConversion.ToDxgi(_info.Format),
                    Usage = D3D11Usage.Staging,
                    BindFlags = D3D11BindFlags.None,
                    CpuAccessFlags = D3D11CpuAccessFlags.Read,
                    MiscFlags = D3D11ResourceMiscFlags.None,
                };
                _stagingResource = _device.CreateTexture3D(in stagingDesc, 0, 0, 0);
            }
            else
            {
                D3D11Texture2DDesc stagingDesc = CreateStagingDesc(_info);
                _stagingResource = _device.CreateTexture2D(in stagingDesc, 0, 0);
            }
        }
    }

    private void ReadbackAll()
    {
        nint source = PrepareReadbackSource();
        _context.CopyResource(_stagingResource, source);
        int layers = Math.Max(1, _info.GetLayers());
        int levels = _info.Samples > 1 ? 1 : Math.Max(1, _info.Levels);
        for (int level = 0; level < levels; level++)
        {
            for (int layer = 0; layer < layers; layer++)
            {
                ReadbackSubresource(layer, level, GetSubresourceOffset(layer, level), alreadyCopied: true);
            }
        }
    }

    private unsafe void ReadbackSubresource(int layer, int level, int destinationOffset, bool alreadyCopied = false)
    {
        if (!alreadyCopied)
        {
            _context.CopyResource(_stagingResource, PrepareReadbackSource());
        }

        uint subresource = GetD3DSubresource(_info, layer, level);
        D3D11MappedSubresource mapped = _context.Map(_stagingResource, D3D11MapType.Read, subresource: subresource);
        int rows = (Math.Max(1, _info.Height >> level) + Math.Max(1, _info.BlockHeight) - 1) / Math.Max(1, _info.BlockHeight);
        int destinationStride = _info.GetMipStride(level);
        int copyStride = Math.Min(destinationStride, (int)mapped.RowPitch);
        int depth = _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : 1;
        int sliceStride = destinationStride * rows;
        int mappedDepthPitch = mapped.DepthPitch == 0 ? sliceStride : (int)mapped.DepthPitch;

        for (int slice = 0; slice < depth; slice++)
        {
            for (int row = 0; row < rows; row++)
            {
                Marshal.Copy(
                    mapped.Data + slice * mappedDepthPitch + row * (int)mapped.RowPitch,
                    _shadow,
                    destinationOffset + slice * sliceStride + row * destinationStride,
                    copyStride);
            }
        }

        _context.Unmap(_stagingResource, subresource);
    }

    private nint PrepareReadbackSource()
    {
        if (_info.Samples <= 1)
        {
            return Resource;
        }

        if (IsDepthStencil || _resolveResource == 0)
        {
            throw new NotSupportedException("MSAA depth/stencil readback requires a depth resolve shader or host-specific path.");
        }

        int layers = Math.Max(1, _info.GetLayers());
        D3D11Format format = D3D11FormatConversion.ToDxgi(_info.Format);
        for (int layer = 0; layer < layers; layer++)
        {
            _context.ResolveSubresource(_resolveResource, (uint)layer, Resource, (uint)layer, format);
        }

        return _resolveResource;
    }

    private D3D11Texture Create1DView(TextureCreateInfo info, int firstLayer, int firstLevel)
    {
        int sourceLayers = Math.Max(1, _info.GetLayers());
        int viewLayers = Math.Max(1, info.GetLayers());
        int sourceLevels = Math.Max(1, _info.Levels);
        int viewLevels = Math.Max(1, info.Levels);
        bool isArray = sourceLayers > 1;
        if (firstLayer < 0 || firstLayer + viewLayers > sourceLayers || firstLevel < 0 || firstLevel + viewLevels > sourceLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLayer));
        }

        D3D11Format format = D3D11FormatConversion.ToDxgi(info.Format);
        nint shaderResourceView = 0;
        nint renderTargetView = 0;
        nint unorderedAccessView = 0;
        nint depthStencilView = 0;

        if (!IsDepthStencil)
        {
            D3D11ShaderResourceViewDesc srv = new()
            {
                Format = format,
                ViewDimension = isArray ? D3D11SrvDimension.Texture1DArray : D3D11SrvDimension.Texture1D,
                View = new D3D11SrvUnion
                {
                    Texture1D = isArray
                        ? new D3D11Texture1DSrv()
                        : new D3D11Texture1DSrv
                        {
                            MostDetailedMip = (uint)firstLevel,
                            MipLevels = (uint)viewLevels,
                        },
                },
            };
            if (isArray)
            {
                srv.View = new D3D11SrvUnion
                {
                    Texture1DArray = new D3D11Texture1DArraySrv
                    {
                        MostDetailedMip = (uint)firstLevel,
                        MipLevels = (uint)viewLevels,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                };
            }
            shaderResourceView = CreateShaderResourceViewForView(in srv, info, firstLayer, firstLevel);

            D3D11RenderTargetViewDesc rtv = new()
            {
                Format = format,
                ViewDimension = isArray ? D3D11RtvDimension.Texture1DArray : D3D11RtvDimension.Texture1D,
            };
            rtv.View = isArray
                ? new D3D11RtvUnion
                {
                    Texture1DArray = new D3D11Texture1DArrayRtv
                    {
                        MipSlice = (uint)firstLevel,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                }
                : new D3D11RtvUnion
                {
                    Texture1D = new D3D11Texture1DRtv { MipSlice = (uint)firstLevel },
                };
            renderTargetView = CreateRenderTargetViewForView(in rtv, info, firstLayer, firstLevel, viewLayers, viewLevels);

            D3D11UnorderedAccessViewDesc uav = new()
            {
                Format = format,
                ViewDimension = isArray ? D3D11UavDimension.Texture1DArray : D3D11UavDimension.Texture1D,
            };
            uav.View = isArray
                ? new D3D11UavUnion
                {
                    Texture1DArray = new D3D11Texture1DArrayUav
                    {
                        MipSlice = (uint)firstLevel,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                }
                : new D3D11UavUnion
                {
                    Texture1D = new D3D11Texture1DUav { MipSlice = (uint)firstLevel },
                };
            unorderedAccessView = CreateUnorderedAccessViewForView(in uav, info, firstLayer, firstLevel, viewLayers, viewLevels);
        }
        else
        {
            D3D11DepthStencilViewDesc dsv = new()
            {
                Format = format,
                ViewDimension = isArray ? D3D11DsvDimension.Texture1DArray : D3D11DsvDimension.Texture1D,
            };
            dsv.View = isArray
                ? new D3D11DsvUnion
                {
                    Texture1DArray = new D3D11Texture1DArrayDsv
                    {
                        MipSlice = (uint)firstLevel,
                        FirstArraySlice = (uint)firstLayer,
                        ArraySize = (uint)viewLayers,
                    },
                }
                : new D3D11DsvUnion
                {
                    Texture1D = new D3D11Texture1DDsv { MipSlice = (uint)firstLevel },
                };
            depthStencilView = CreateDepthStencilViewForView(in dsv, info, firstLayer, firstLevel, viewLayers, viewLevels);
        }

        return new D3D11Texture(
            _device,
            _context,
            info,
            Resource,
            _bufferResolver,
            shaderResourceView,
            renderTargetView,
            depthStencilView,
            unorderedAccessView,
            _storageOwner,
            firstLayer,
            firstLevel,
            viewLayers,
            viewLevels,
            _resourceLayerCount,
            _resourceLevelCount);
    }

    private D3D11Texture Create3DView(TextureCreateInfo info, int firstLayer, int firstLevel)
    {
        int sourceLevels = Math.Max(1, _info.Levels);
        int viewLevels = Math.Max(1, info.Levels);
        int sourceDepth = Math.Max(1, _info.Depth >> firstLevel);
        int viewDepth = Math.Max(1, info.Depth);
        if (firstLayer < 0 || firstLayer + viewDepth > sourceDepth || firstLevel < 0 || firstLevel + viewLevels > sourceLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLayer));
        }

        D3D11Format format = D3D11FormatConversion.ToDxgi(info.Format);
        nint shaderResourceView = 0;
        nint renderTargetView = 0;
        nint unorderedAccessView = 0;
        nint depthStencilView = 0;

        if (!IsDepthStencil)
        {
            D3D11ShaderResourceViewDesc srv = new()
            {
                Format = format,
                ViewDimension = D3D11SrvDimension.Texture3D,
                View = new D3D11SrvUnion
                {
                    Texture3D = new D3D11Texture3DSrv
                    {
                        MostDetailedMip = (uint)firstLevel,
                        MipLevels = (uint)viewLevels,
                    },
                },
            };
            shaderResourceView = CreateShaderResourceViewForView(in srv, info, firstLayer, firstLevel);

            D3D11RenderTargetViewDesc rtv = new()
            {
                Format = format,
                ViewDimension = D3D11RtvDimension.Texture3D,
                View = new D3D11RtvUnion
                {
                    Texture3D = new D3D11Texture3DRtv
                    {
                        MipSlice = (uint)firstLevel,
                        FirstWSlice = (uint)firstLayer,
                        WSize = (uint)viewDepth,
                    },
                },
            };
            renderTargetView = CreateRenderTargetViewForView(in rtv, info, firstLayer, firstLevel, 1, viewLevels);

            D3D11UnorderedAccessViewDesc uav = new()
            {
                Format = format,
                ViewDimension = D3D11UavDimension.Texture3D,
                View = new D3D11UavUnion
                {
                    Texture3D = new D3D11Texture3DUav
                    {
                        MipSlice = (uint)firstLevel,
                        FirstWSlice = (uint)firstLayer,
                        WSize = (uint)viewDepth,
                    },
                },
            };
            unorderedAccessView = CreateUnorderedAccessViewForView(in uav, info, firstLayer, firstLevel, 1, viewLevels);
        }
        else
        {
            throw new NotSupportedException("D3D11 depth Texture3D views are not supported by the GAL contract.");
        }

        return new D3D11Texture(
            _device,
            _context,
            info,
            Resource,
            _bufferResolver,
            shaderResourceView,
            renderTargetView,
            depthStencilView,
            unorderedAccessView,
            _storageOwner,
            firstLayer,
            firstLevel,
            viewDepth,
            viewLevels,
            _resourceLayerCount,
            _resourceLevelCount);
    }

    private static D3D11BindFlags GetBindFlags(TextureCreateInfo info, D3D11FormatSupport support)
    {
        if (info.Format.HasDepth)
        {
            if ((support & D3D11FormatSupport.DepthStencil) == 0)
            {
                throw new NotSupportedException($"D3D11 depth-stencil binding is not supported for {info.Format}.");
            }

            return D3D11BindFlags.DepthStencil;
        }

        D3D11BindFlags flags = D3D11BindFlags.None;
        if ((support & (D3D11FormatSupport.ShaderLoad | D3D11FormatSupport.ShaderSample)) != 0)
        {
            flags |= D3D11BindFlags.ShaderResource;
        }
        if ((support & D3D11FormatSupport.RenderTarget) != 0)
        {
            flags |= D3D11BindFlags.RenderTarget;
        }
        if (info.Samples <= 1 && (support & D3D11FormatSupport.TypedUnorderedAccessView) != 0)
        {
            flags |= D3D11BindFlags.UnorderedAccess;
        }

        return flags;
    }

    private int GetSubresourceOffset(int layer, int level)
    {
        if (layer < 0 || layer >= Math.Max(1, _info.GetLayers()))
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }

        if (level < 0 || level >= Math.Max(1, _info.Levels))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        int offset = 0;
        for (int currentLevel = 0; currentLevel < level; currentLevel++)
        {
            // TextureCreateInfo.GetMipSize already includes all array/cubemap
            // layers (or the full 3D depth) for this mip.
            offset += _info.GetMipSize(currentLevel);
        }

        return checked(offset + layer * GetSubresourceSize(level));
    }

    private static int GetDataSize(TextureCreateInfo info)
    {
        long size = 0;
        for (int level = 0; level < Math.Max(1, info.Levels); level++)
        {
            size += info.GetMipSize(level);
        }

        return checked((int)Math.Min(Math.Max(1, size), int.MaxValue));
    }

    private int GetSubresourceSize(int level)
    {
        return _info.Target == Target.Texture3D
            ? _info.GetMipSize(level)
            : _info.GetMipSize2D(level);
    }

    private static uint GetD3DSubresource(TextureCreateInfo info, int layer, int level)
    {
        int mipLevels = info.Samples > 1 ? 1 : Math.Max(1, info.Levels);
        return checked((uint)(level + layer * mipLevels));
    }

    private static uint GetD3DSubresource(int layer, int level, int mipLevels)
    {
        return checked((uint)(level + layer * Math.Max(1, mipLevels)));
    }

    private static int ResolveViewIndex(int requested, int first, int count, int resourceCount, string parameterName)
    {
        // Texture-group copy dependencies can carry an index relative to the
        // parent storage, while the GAL ITexture contract normally supplies a
        // view-local index. Accept both and always return the physical parent
        // resource index used by D3D11CalcSubresource.
        if (first != 0 && requested >= first && requested < first + count)
        {
            return requested;
        }

        if (requested >= 0 && requested < count)
        {
            return first + requested;
        }

        // TextureGroupHandle addresses the shared storage directly when it
        // creates a copy dependency. In that path the index can legitimately
        // be outside this particular view while remaining inside the parent
        // D3D11 resource.
        if (requested >= 0 && requested < resourceCount)
        {
            return requested;
        }

        throw new ArgumentOutOfRangeException(parameterName, requested, $"Index is outside view range first={first}, count={count}, resourceCount={resourceCount}.");
    }

    private static unsafe PinnedSpan<byte> Pin(byte[] data)
    {
        if (data.Length == 0)
        {
            return new PinnedSpan<byte>(null, 0);
        }

        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        return new PinnedSpan<byte>((void*)pin.AddrOfPinnedObject(), data.Length, pin.Free);
    }
}

internal sealed class D3D11CounterEvent : ICounterEvent
{
    internal sealed class Accumulator { public ulong Value; }
    private readonly D3D11Device _device;
    private readonly D3D11DeviceContext _context;
    private EventHandler<ulong> _resultHandler;
    private double _divisor = 1;
    private readonly D3D11QueryType _queryType;
    private readonly Accumulator _accumulator;
    private readonly Action<D3D11CounterEvent> _flush;
    private nint _query;
    private bool _ended;
    private bool _reported;
    private long _firstPollAt;
    private ulong _result;

    public D3D11CounterEvent(
        D3D11Device device,
        D3D11DeviceContext context,
        CounterType type,
        Accumulator accumulator,
        Action<D3D11CounterEvent> flush)
    {
        _device = device;
        _context = context;
        _accumulator = accumulator;
        _flush = flush;
        _queryType = type == CounterType.SamplesPassed
            ? D3D11QueryType.Occlusion
            : D3D11QueryType.SoStatistics;
        _query = device.CreateQuery(_queryType);
        _context.Begin(_query);
    }

    public bool Invalid { get; set; }
    internal bool Reported => _reported;

    public bool ReserveForHostAccess()
    {
        // This backend does not expose native predication/query ownership.
        return false;
    }

    public void Flush()
    {
        if (!_reported) _flush(this);
    }

    internal void Complete(EventHandler<ulong> resultHandler, float divisor)
    {
        _resultHandler = resultHandler;
        _divisor = float.IsFinite(divisor) && divisor > 0 ? divisor : 1;
        _context.End(_query);
        _ended = true;
    }

    // One GetData call only. S_FALSE means pending, never a synthetic zero.
    internal bool TryPoll()
    {
        if (_reported || _query == 0) return true;
        if (!_ended) return false;
        // A shader compile between End and the first poll must not consume the
        // GPU-wait timeout before the command buffer has even been submitted.
        if (_firstPollAt == 0) _firstPollAt = Stopwatch.GetTimestamp();
        Span<byte> data = stackalloc byte[_queryType == D3D11QueryType.Occlusion ? sizeof(ulong) : 16];
        if (_context.GetData(_query, data) > 0)
        {
            int reason = _device.GetDeviceRemovedReason();
            if (reason < 0) throw new D3D11Exception("GPU counter/device removed", reason);
            if (Stopwatch.GetElapsedTime(_firstPollAt) > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"D3D11 counter {_queryType} did not complete in 30 seconds.");
            return false;
        }

        ulong value = _queryType == D3D11QueryType.Occlusion
            ? MemoryMarshal.Read<ulong>(data)
            : MemoryMarshal.Read<D3D11SoStatistics>(data).NumPrimitivesWritten;
        // Match GAL's cumulative counter semantics, including scaled samples.
        _accumulator.Value += (ulong)Math.Ceiling(value / _divisor);
        _result = _accumulator.Value;
        _reported = true;
        D3D11Com.Release(_query);
        _query = 0;
        return true;
    }

    // Invoke after removing from the pending queue: callbacks may reenter the
    // renderer without dequeuing the next event or reporting this one twice.
    internal void ReportResult()
    {
        EventHandler<ulong> handler = _resultHandler;
        _resultHandler = null;
        if (!Invalid) handler?.Invoke(this, _result);
    }

    public void Dispose()
    {
        // The pending queue still owns the query. Even a discarded report must
        // contribute to later cumulative results in the same reset epoch.
        Invalid = true;
    }

    internal void Release()
    {
        if (_query != 0 && !_ended) _context.End(_query);
        D3D11Com.Release(_query);
        _query = 0;
        _reported = true;
        _resultHandler = null;
    }
}
