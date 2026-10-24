using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Graphics.D3D11;

/// <summary>
/// GAL pipeline boundary for the native D3D11 backend.
///
/// The method surface is intentionally complete so the project can be wired
/// into Ryujinx without silently falling back to OpenGL or Vulkan. Native D3D11
/// paths cover the common resource, draw, copy, state and binding operations;
/// unsupported behavior is limited to features that need shader emulation or a
/// host-specific D3D11.1/Xbox extension.
/// </summary>
internal sealed class D3D11Pipeline : IPipeline, IDisposable
{
    private readonly D3D11Renderer _renderer;
    private D3D11Texture[] _colorTargets = Array.Empty<D3D11Texture>();
    private D3D11Texture _depthStencil;
    private D3D11Program _program;
    private readonly Dictionary<D3D11Program, int> _geometryTraceCounts = new();
    private readonly Dictionary<D3D11Program, int> _indexedGeometryTraceCounts = new();
    private nint _blendState;
    private nint _depthStencilState;
    private nint _rasterizerState;
    private nint _inputLayout;
    private nint _defaultVertexBuffer;
    private nint _zeroVertexBuffer;
    private readonly BlendDescriptor[] _blendStates = new BlendDescriptor[8];
    private readonly bool[] _blendStateSet = new bool[8];
    private readonly uint[] _colorMasks = new uint[8];
    private DepthTestDescriptor _depthTest;
    private StencilTestDescriptor _stencilTest;
    private bool _depthTestSet;
    private bool _stencilTestSet;
    private bool _depthClamp;
    private bool _cullEnabled;
    private Face _cullFace = Face.Back;
    private FrontFace _frontFace = FrontFace.CounterClockwise;
    private PolygonMode _frontPolygonMode = PolygonMode.Fill;
    private PolygonMode _backPolygonMode = PolygonMode.Fill;
    private PolygonModeMask _depthBiasEnables;
    private float _depthBiasFactor;
    private float _depthBiasUnits;
    private float _depthBiasClamp;
    private bool _alphaToCoverage;
    private bool _lineSmooth;
    private bool _hasScissors;
    private float _lineWidth = 1f;
    private int _patchVertices = 1;
    private bool _primitiveRestart;
    private int _primitiveRestartIndex;
    private bool _rasterizerDiscard;
    private PrimitiveTopology _primitiveTopology = PrimitiveTopology.Triangles;
    private nint _blitVertexShader;
    private nint _blitPixelShader;
    private nint _blitConstantBuffer;
    private nint _blitRasterizerState;
    private D3D11Sampler _copyPointSampler;
    private D3D11Sampler _copyLinearSampler;
    private nint _clearPixelShader;
    private nint _clearConstantBuffer;
    private readonly nint[] _clearBlendStates = new nint[16];
    private nint _diagnosticPixelShader;
    private readonly string _diagnosticPixelShaderMode =
        Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_DIAGNOSTIC_PS")?.Trim().ToLowerInvariant();
    private readonly bool _forceDiagnosticPixelShader =
        string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_FORCE_MAGENTA_PS"), "1", StringComparison.Ordinal);
    private readonly bool _forceVertexColorPixelShader =
        string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_FORCE_VERTEX_COLOR_PS"), "1", StringComparison.Ordinal);
    private readonly bool _singleViewportCompatibility =
        AppContext.TryGetSwitch("Ryujinx.Uwp.D3D11.SingleViewportCompatibility", out bool singleViewport) && singleViewport;
    private VertexAttribDescriptor[] _vertexAttribs = Array.Empty<VertexAttribDescriptor>();
    private VertexBufferDescriptor[] _vertexBuffers = Array.Empty<VertexBufferDescriptor>();
    private readonly Dictionary<int, D3D11Half3VertexBuffer> _half3Buffers = new();
    private readonly Dictionary<int, int> _half3Slots = new();
    private readonly Dictionary<(ShaderStage Stage, int Slot), D3D11Texture> _imageWrites = new();
    private readonly Dictionary<(ShaderStage Stage, int Slot), D3D11BufferResource> _storageWrites = new();
    public float RenderScale { get; set; } = 1f;

    private D3D11BufferResource[] _transformFeedbackBuffers = Array.Empty<D3D11BufferResource>();
    private long _drawCount;
    private long _indexedDrawCount;
    private long _dispatchCount;
    private long _colorClearCount;
    private int _validColorTargetCount;
    private int _viewportCount;
    private ulong _viewportSetCount;
    private D3D11Viewport[] _nativeViewports = Array.Empty<D3D11Viewport>();
    private float _firstViewportWidth;
    private float _firstViewportHeight;
    private int _scissorCount;
    private int _firstScissorWidth;
    private int _firstScissorHeight;
    private readonly nint[][] _desiredShaderResources = CreateShaderResourceState();
    private readonly D3D11Texture[][] _desiredShaderResourceTextures = CreateShaderResourceTextureState();
    private readonly int[] _desiredShaderResourceHighWater = CreateShaderResourceHighWaterState();
    private readonly Dictionary<(ShaderStage Stage, int GuestBinding, int HostSlot), int> _constantBufferTraceCounts = new();
    private readonly Dictionary<int, BufferRange> _uniformBufferAssignments = new();
    private readonly Dictionary<(ShaderStage Stage, int Slot), nint> _boundUniformBuffers = new();
    private BufferRange _indexBuffer;
    private IndexType _indexType;
    private D3D11BufferResource _indexBufferResource;
    private nint _expandedUByteIndexBuffer;
    private int _expandedUByteIndexCapacity;
    private long _expandedUByteSourceGeneration = -1;
    private int _expandedUByteSourceOffset = -1;
    private int _expandedUByteSourceSize = -1;
    private bool _expandedUBytePrimitiveRestart;
    private int _expandedUBytePrimitiveRestartIndex;
    private long _uByteIndexExpansionCount;
    private nint _quadIndexBuffer;
    private int _quadIndexBufferCapacity;
    private int _quadIndexBufferPatternCount;
    private nint _convertedQuadIndexBuffer;
    private int _convertedQuadIndexBufferCapacity;
    private long _quadConversionCount;
    private nint _fanIndexBuffer;
    private int _fanIndexBufferCapacity;
    private int _fanIndexBufferPatternCount;
    private nint _convertedFanIndexBuffer;
    private int _convertedFanIndexBufferCapacity;
    private long _fanConversionCount;
    private long _hazardFeedbackBindCount;
    private long _hazardSanitizedCount;
    private int _missingShaderWarningMask;
    private string _lastDepthAttachmentMismatch;
    private long _partialColorClearCount;
    private string _lastGraphicsOperation = "none";
    private nint _lastDrawTargetResource;
    private D3D11Texture _lastDrawTarget;
    private readonly Dictionary<(int Width, int Height), D3D11Texture> _lastDrawTargetsBySize = new();
    private nint _lastClearTargetResource;
    private uint _lastClearMask;
    private ColorF _lastClearColor;
    private readonly Dictionary<nint, long> _renderTargetWriteCounts = new();

    public D3D11Pipeline(D3D11Renderer renderer)
    {
        _renderer = renderer;

        for (int index = 0; index < _colorMasks.Length; index++)
        {
            _colorMasks[index] = 0xf;
        }
    }

    /// <summary>
    /// Notify the hazard-feedback write-generation system that all active render targets
    /// and the depth/stencil buffer were written to. Must be called after every draw call
    /// and after every render-target clear, so the generation-based copy cache in
    /// <see cref="D3D11Texture.TryGetFeedbackCopyView"/> can skip redundant copies.
    /// </summary>
    private void NotifyRenderTargetWrites()
    {
        for (int index = 0; index < _colorTargets.Length; index++)
        {
            D3D11Texture target = _colorTargets[index];
            // ColorMask=0 proves this attachment cannot be written by the
            // draw. Keep depth/UAV tracking conservative and independent.
            if (target is not null && _colorMasks[index] != 0)
            {
                target.NotifyRenderTargetWrite();

                nint resource = target.Resource;
                if (resource != 0)
                {
                    _renderTargetWriteCounts.TryGetValue(resource, out long count);
                    _renderTargetWriteCounts[resource] = count + 1;
                }
            }
        }
        _depthStencil?.NotifyRenderTargetWrite();
        NotifyUavWrites(ShaderStage.Fragment);
    }

    private void NotifyUavWrites(ShaderStage stage)
    {
        foreach (var (key, texture) in _imageWrites)
            if (key.Stage == stage) texture.NotifyRenderTargetWrite(); // UAV addresses one mip
        foreach (var (key, buffer) in _storageWrites)
            if (key.Stage == stage) buffer.MarkGpuModified();
    }

    internal long GetRenderTargetWriteCount(nint resource)
    {
        return resource != 0 && _renderTargetWriteCounts.TryGetValue(resource, out long count)
            ? count
            : 0;
    }

    internal string GetPresentationTelemetry()
    {
        int vertexSrvCount = CountDesiredShaderResources(ShaderStage.Vertex);
        int fragmentSrvCount = CountDesiredShaderResources(ShaderStage.Fragment);
        int geometrySrvCount = CountDesiredShaderResources(ShaderStage.Geometry);

        return
        $"draws={_drawCount}, indexedDraws={_indexedDrawCount}, dispatches={_dispatchCount}, colorClears={_colorClearCount}, " +
        $"gpuScaledCopies={D3D11DebugTelemetry.GpuScaledCopies}, cpuScaledCopyAttempts={D3D11DebugTelemetry.CpuScaledCopyAttempts}, feedbackReuses={D3D11DebugTelemetry.HazardFeedbackReuses}, " +
        $"feedbackCreated={D3D11DebugTelemetry.FeedbackCreated}, feedbackInvalidated={D3D11DebugTelemetry.FeedbackInvalidated}, feedbackSubmissionMs={D3D11DebugTelemetry.FeedbackSubmissionMs:F2}, " +
        $"program={(_program is null ? "none" : "bound")}, colorTargets={_validColorTargetCount}/{_colorTargets.Length}, " +
        $"depthTarget={(_depthStencil is null ? 0 : 1)}, viewports={_viewportCount}, " +
        $"firstViewport={_firstViewportWidth:0.##}x{_firstViewportHeight:0.##}, scissors={_scissorCount}, " +
        $"firstScissor={_firstScissorWidth}x{_firstScissorHeight}, scissorEnabled={_hasScissors}, " +
        $"cull={_cullEnabled}/{_cullFace}, frontFace={_frontFace}, topology={_primitiveTopology}, " +
        $"vertexBuffers={_vertexBuffers.Length}, vertexAttribs={_vertexAttribs.Length}, rasterizerDiscard={_rasterizerDiscard}, " +
        $"indexType={_indexType}, u8Expansions={_uByteIndexExpansionCount}, " +
        $"srvDesired=vertex:{vertexSrvCount},fragment:{fragmentSrvCount},geometry:{geometrySrvCount}, " +
        $"srvHighWater=vertex:{_desiredShaderResourceHighWater[(int)ShaderStage.Vertex]},fragment:{_desiredShaderResourceHighWater[(int)ShaderStage.Fragment]},geometry:{_desiredShaderResourceHighWater[(int)ShaderStage.Geometry]}, " +
        $"lastOp={_lastGraphicsOperation}, lastDrawTarget=0x{_lastDrawTargetResource.ToInt64():X}, " +
        $"lastClearTarget=0x{_lastClearTargetResource.ToInt64():X}, lastClearMask=0x{_lastClearMask:X}, " +
        $"lastClearColor=[{_lastClearColor.Red:0.###},{_lastClearColor.Green:0.###},{_lastClearColor.Blue:0.###},{_lastClearColor.Alpha:0.###}]";
    }

    private nint GetFirstColorTargetResource()
    {
        return GetFirstColorTarget()?.Resource ?? 0;
    }

    private D3D11Texture GetFirstColorTarget()
    {
        foreach (D3D11Texture target in _colorTargets)
        {
            if (target is not null)
            {
                return target;
            }
        }

        return null;
    }

    internal D3D11Texture GetLastDrawTarget() => _lastDrawTarget;

    internal D3D11Texture GetLastDrawTarget(int width, int height)
    {
        return _lastDrawTargetsBySize.TryGetValue((width, height), out D3D11Texture target) &&
               target is not null && target.Resource != 0
            ? target
            : null;
    }

    private void TrackLastDrawTarget()
    {
        _lastDrawTarget = GetFirstColorTarget();
        _lastDrawTargetResource = _lastDrawTarget?.Resource ?? 0;
        if (_lastDrawTarget is not null && _lastDrawTarget.Resource != 0)
        {
            _lastDrawTargetsBySize[(_lastDrawTarget.Width, _lastDrawTarget.Height)] = _lastDrawTarget;
        }
    }

    private int CountDesiredShaderResources(ShaderStage stage)
    {
        int count = 0;
        nint[] resources = _desiredShaderResources[(int)stage];
        int highWater = _desiredShaderResourceHighWater[(int)stage];
        for (int index = 0; index <= highWater && index < resources.Length; index++)
        {
            if (resources[index] != 0)
            {
                count++;
            }
        }

        return count;
    }

    public void Barrier()
    {
        // D3D11 performs resource hazard tracking on the immediate context.
        // Flushing at every GAL barrier forces an unnecessary driver
        // submission and is particularly expensive on Xbox.
        if (!D3D11PerformanceSettings.DeferImplicitBarriers)
        {
            _renderer.NativeContext.Flush();
        }
    }
    public void BeginTransformFeedback(PrimitiveTopology topology)
    {
        _renderer.NativeContext.IASetPrimitiveTopology(
            D3D11FormatConversion.ToPrimitiveTopology(topology, _patchVertices));
    }
    public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
    {
        D3D11BufferResource buffer = _renderer.GetBuffer(destination);
        if (offset < 0 || size < 0 || offset > buffer.Size - size)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (offset == 0 && size == buffer.Size)
        {
            _renderer.NativeContext.ClearUnorderedAccessViewUint(buffer.UnorderedAccessView, value);
        }
        else
        {
            buffer.Clear(offset, size, value);
        }
    }
    public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
    {
        if ((uint)index >= (uint)_colorTargets.Length || _colorTargets[index] is null)
        {
            return;
        }

        componentMask &= 0xf;
        if (componentMask == 0)
        {
            return;
        }

        _colorClearCount++;
        _lastGraphicsOperation = "clear-color";
        _lastClearTargetResource = _colorTargets[index].Resource;
        _lastClearMask = componentMask;
        _lastClearColor = color;
        if (componentMask == 0xf)
        {
            _renderer.NativeContext.ClearRenderTargetView(
                _colorTargets[index].RenderTargetView,
                color.Red,
                color.Green,
                color.Blue,
                color.Alpha);
        }
        else
        {
            ClearRenderTargetColorMasked(index, componentMask, color);
        }
        _colorTargets[index].NotifyRenderTargetWrite();
    }

    private void ClearRenderTargetColorMasked(int index, uint componentMask, ColorF color)
    {
        EnsureClearResources();

        D3D11Texture destination = _colorTargets[index];
        float width = Math.Max(1f, destination.Width);
        float height = Math.Max(1f, destination.Height);

        Span<float> vertexConstants = stackalloc float[12]
        {
            0f, 0f, width, height,
            0f, 0f, width, height,
            width, height, width, height,
        };
        Span<float> clearColor = stackalloc float[4]
        {
            color.Red, color.Green, color.Blue, color.Alpha,
        };

        _renderer.NativeContext.UpdateSubresource(
            _blitConstantBuffer,
            MemoryMarshal.AsBytes(vertexConstants),
            rowPitch: (uint)MemoryMarshal.AsBytes(vertexConstants).Length);
        _renderer.NativeContext.UpdateSubresource(
            _clearConstantBuffer,
            MemoryMarshal.AsBytes(clearColor),
            rowPitch: (uint)MemoryMarshal.AsBytes(clearColor).Length);

        ShaderStage[] graphicsStages =
        {
            ShaderStage.Vertex,
            ShaderStage.Fragment,
            ShaderStage.Geometry,
            ShaderStage.TessellationControl,
            ShaderStage.TessellationEvaluation,
        };

        foreach (ShaderStage stage in graphicsStages)
        {
            _renderer.NativeContext.SetShader(stage, 0);
        }

        _renderer.NativeContext.OMSetRenderTargets(new[] { destination.RenderTargetView }, 0);
        _renderer.NativeContext.RSSetViewports(new[]
        {
            new D3D11Viewport
            {
                TopLeftX = 0f,
                TopLeftY = 0f,
                Width = width,
                Height = height,
                MinDepth = 0f,
                MaxDepth = 1f,
            }
        });
        _renderer.NativeContext.SetShader(ShaderStage.Vertex, _blitVertexShader);
        _renderer.NativeContext.SetShader(ShaderStage.Fragment, _clearPixelShader);
        _renderer.NativeContext.SetConstantBuffers(ShaderStage.Vertex, 13, new[] { _blitConstantBuffer });
        _renderer.NativeContext.SetConstantBuffers(ShaderStage.Fragment, 12, new[] { _clearConstantBuffer });
        _renderer.NativeContext.IASetInputLayout(0);
        _renderer.NativeContext.IASetPrimitiveTopology(D3D11PrimitiveTopology.TriangleList);
        _renderer.NativeContext.OMSetBlendState(_clearBlendStates[(int)componentMask], 0f, 0f, 0f, 0f, uint.MaxValue);
        _renderer.NativeContext.OMSetDepthStencilState(0, 0);
        _renderer.NativeContext.RSSetState(_blitRasterizerState);
        _renderer.NativeContext.Draw(6, 0);

        _renderer.NativeContext.OMSetRenderTargets(GetColorTargetViews(), GetCompatibleDepthStencilView());
        if (_nativeViewports.Length != 0)
        {
            _renderer.NativeContext.RSSetViewports(_nativeViewports);
        }

        foreach (ShaderStage stage in graphicsStages)
        {
            _renderer.NativeContext.SetShader(stage, _program?.GetShader(stage) ?? 0);
        }

        _renderer.NativeContext.IASetInputLayout(_inputLayout);
        _renderer.NativeContext.IASetPrimitiveTopology(
            D3D11FormatConversion.ToPrimitiveTopology(_primitiveTopology, _patchVertices));
        _renderer.NativeContext.OMSetBlendState(
            _blendState,
            _blendStates[0].BlendConstant.Red,
            _blendStates[0].BlendConstant.Green,
            _blendStates[0].BlendConstant.Blue,
            _blendStates[0].BlendConstant.Alpha,
            uint.MaxValue);
        _renderer.NativeContext.OMSetDepthStencilState(
            _depthStencilState,
            _stencilTestSet ? (uint)_stencilTest.FrontFuncRef : 0);
        _renderer.NativeContext.RSSetState(_rasterizerState);
        RebindUniformBuffers();
        RebindGraphicsShaderResources();

        _partialColorClearCount++;
        if (_partialColorClearCount <= 10 || _partialColorClearCount % 1000 == 0)
        {
            D3D11DebugTelemetry.Append(
                $"masked-color-clear: target={index}, mask=0x{componentMask:X}, " +
                $"color=[{color.Red:0.###},{color.Green:0.###},{color.Blue:0.###},{color.Alpha:0.###}], " +
                $"size={width}x{height}, sample={_partialColorClearCount}\n");
        }
    }
    public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
    {
        if (_depthStencil is null)
        {
            return;
        }

        uint clearFlags = 0;
        if (depthMask)
        {
            clearFlags |= 0x1;
        }

        if (stencilMask != 0)
        {
            clearFlags |= 0x2;
        }

        if (clearFlags != 0)
        {
            _renderer.NativeContext.ClearDepthStencilView(
                _depthStencil.DepthStencilView,
                clearFlags,
                depthValue,
                (byte)stencilValue);
            _depthStencil.NotifyRenderTargetWrite();
        }
    }
    public void CommandBufferBarrier()
    {
        _renderer.NativeContext.Flush();
    }
    public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
    {
        D3D11BufferResource src = _renderer.GetBuffer(source);
        D3D11BufferResource dst = _renderer.GetBuffer(destination);
        D3D11Box box = new()
        {
            Left = (uint)srcOffset,
            Top = 0,
            Front = 0,
            Right = (uint)(srcOffset + size),
            Bottom = 1,
            Back = 1,
        };
        _renderer.NativeContext.CopySubresourceRegion(dst.Resource, 0, (uint)dstOffset, 0, 0, src.Resource, 0, in box);
        dst.NotifyBufferCopy(src, srcOffset, dstOffset, size);
    }
    public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
    {
        if (!EnsureProgram(ShaderStage.Compute))
        {
            return;
        }
        RebindShaderResources(ShaderStage.Compute);
        _dispatchCount++;
        _renderer.NativeContext.Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        if (groupsX > 0 && groupsY > 0 && groupsZ > 0) NotifyUavWrites(ShaderStage.Compute);
    }
    public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
    {
        if (!EnsureProgram(ShaderStage.Vertex))
        {
            return;
        }
        RebindGraphicsShaderResources();
        BindDiagnosticPixelShader();

        _drawCount++;
        TraceDrawGeometry(vertexCount, firstVertex);
        _lastGraphicsOperation = "draw";
        TrackLastDrawTarget();
        if (_primitiveTopology == PrimitiveTopology.Quads)
        {
            DrawQuads(vertexCount, instanceCount, firstVertex, firstInstance);
        }
        else if (_primitiveTopology == PrimitiveTopology.TriangleFan)
        {
            DrawTriangleFan(vertexCount, instanceCount, firstVertex, firstInstance);
        }
        else
        {
            _renderer.NativeContext.DrawInstanced((uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
        }
        NotifyRenderTargetWrites();
    }
    public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
    {
        if (!EnsureProgram(ShaderStage.Vertex))
        {
            return;
        }
        RebindGraphicsShaderResources();
        BindDiagnosticPixelShader();

        _indexedDrawCount++;
        TraceIndexedDrawGeometry(indexCount, firstIndex, firstVertex);
        _lastGraphicsOperation = "draw-indexed";
        TrackLastDrawTarget();
        if (_primitiveTopology == PrimitiveTopology.Quads)
        {
            DrawIndexedQuads(indexCount, instanceCount, firstIndex, firstVertex, firstInstance);
        }
        else if (_primitiveTopology == PrimitiveTopology.TriangleFan)
        {
            DrawIndexedTriangleFan(indexCount, instanceCount, firstIndex, firstVertex, firstInstance);
        }
        else
        {
            EnsureUByteIndexBufferCurrent();
            _renderer.NativeContext.DrawIndexedInstanced((uint)indexCount, (uint)instanceCount, (uint)firstIndex, firstVertex, (uint)firstInstance);
        }
        NotifyRenderTargetWrites();
    }
    public void DrawIndexedIndirect(BufferRange indirectBuffer)
    {
        if (!EnsureProgram(ShaderStage.Vertex))
        {
            return;
        }
        EnsureUByteIndexBufferCurrent();
        RebindGraphicsShaderResources();
        BindDiagnosticPixelShader();
        D3D11BufferResource buffer = _renderer.GetBuffer(indirectBuffer.Handle);
        _renderer.NativeContext.DrawIndexedIndirect(buffer.Resource, (uint)indirectBuffer.Offset);
        NotifyRenderTargetWrites();
    }
    public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
    {
        uint count = ReadUInt32(parameterBuffer);
        int drawCount = Math.Min((int)count, maxDrawCount);
        for (int index = 0; index < drawCount; index++)
        {
            DrawIndexedIndirect(new BufferRange(indirectBuffer.Handle, indirectBuffer.Offset + index * stride, indirectBuffer.Size - index * stride));
        }
    }
    public void DrawIndirect(BufferRange indirectBuffer)
    {
        if (!EnsureProgram(ShaderStage.Vertex))
        {
            return;
        }
        RebindGraphicsShaderResources();
        BindDiagnosticPixelShader();
        D3D11BufferResource buffer = _renderer.GetBuffer(indirectBuffer.Handle);
        _renderer.NativeContext.DrawIndirect(buffer.Resource, (uint)indirectBuffer.Offset);
        NotifyRenderTargetWrites();
    }
    public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
    {
        uint count = ReadUInt32(parameterBuffer);
        int drawCount = Math.Min((int)count, maxDrawCount);
        for (int index = 0; index < drawCount; index++)
        {
            DrawIndirect(new BufferRange(indirectBuffer.Handle, indirectBuffer.Offset + index * stride, indirectBuffer.Size - index * stride));
        }
    }
    public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
    {
        if (texture is not D3D11Texture source || sampler is not D3D11Sampler d3dSampler ||
            _colorTargets.Length == 0 || _colorTargets[0] is null)
        {
            string detail = $"DrawTexture invalid state: texture={texture?.GetType().Name ?? "null"}, sampler={sampler?.GetType().Name ?? "null"}, targets={_colorTargets.Length}, target0={(_colorTargets.Length > 0 ? _colorTargets[0]?.PresentationDescription : "absent")}; pressure={Ryujinx.Common.Memory.RuntimeMemoryBudget.Pressure}";
            D3D11DebugTelemetry.Append(detail + "\n");
            throw new ArgumentException(detail);
        }

        BlitTexture(source, _colorTargets[0], d3dSampler, srcRegion, dstRegion);
    }

    internal bool TryCopyTextureGpu(D3D11Texture source, D3D11Texture destination, Extents2D src, Extents2D dst, bool linear)
    {
        // A float sampling shader converts these color formats in hardware.
        // Integer, depth/MSAA and compressed destinations require other shaders.
        static bool ColorFormat(Format format) => format is Format.R8Unorm or Format.R8G8Unorm or
            Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb or
            Format.R16Float or Format.R16G16Float or Format.R16G16B16A16Float or
            Format.R32Float or Format.R32G32Float or Format.R32G32B32A32Float or
            Format.R8Snorm or Format.R8G8Snorm or Format.R8G8B8A8Snorm or
            Format.R16Unorm or Format.R16Snorm or Format.R16G16Unorm or Format.R16G16Snorm or
            Format.R16G16B16A16Unorm or Format.R16G16B16A16Snorm or
            Format.R10G10B10A2Unorm or Format.R11G11B10Float;
        if (source.Info.Target != Target.Texture2D || destination.Info.Target != Target.Texture2D ||
            source.Info.Samples != 1 || destination.Info.Samples != 1 ||
            (!ColorFormat(source.Info.Format) && !source.Info.IsCompressed) || !ColorFormat(destination.Info.Format) ||
            source.ShaderResourceView == 0 || destination.RenderTargetView == 0 || _renderer.Host.ShaderCompiler == null)
            return false;
        if (src.X1 == src.X2 || src.Y1 == src.Y2 || dst.X1 == dst.X2 || dst.Y1 == dst.Y2) return true;
        D3D11Sampler sampler = linear
            ? _copyLinearSampler ??= new D3D11Sampler(_renderer.NativeDevice, SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear))
            : _copyPointSampler ??= new D3D11Sampler(_renderer.NativeDevice, SamplerCreateInfo.Create(MinFilter.Nearest, MagFilter.Nearest));
        BlitTexture(source, destination, sampler, new(src.X1, src.Y1, src.X2, src.Y2), new(dst.X1, dst.Y1, dst.X2, dst.Y2), trackDraw: false);
        D3D11DebugTelemetry.CountGpuScaledCopy();
        return true;
    }

    private void BlitTexture(D3D11Texture source, D3D11Texture destination, D3D11Sampler d3dSampler, Extents2DF srcRegion, Extents2DF dstRegion, bool trackDraw = true)
    {

        EnsureBlitResources();
        if (source.Resource == 0 || source.ShaderResourceView == 0 ||
            destination.Resource == 0 || destination.RenderTargetView == 0)
        {
            throw new InvalidOperationException("D3D11 DrawTexture requires valid source and destination views.");
        }

        nint savedVsBuffer = _renderer.NativeContext.GetBlitVertexConstantBuffer();
        nint savedPsBuffer = _renderer.NativeContext.GetBlitPixelConstantBuffer();
        nint savedSampler = _renderer.NativeContext.GetBlitPixelSampler();
        try
        {

        // DrawTexture is a self-contained GAL operation. Do not assume that
        // the destination RTV or a compatible viewport survived the preceding
        // command: hazard transitions can legitimately leave OM unbound, and
        // then this blit silently draws nowhere. That produced a black official
        // presentation surface while the presenter exposed a partially drawn
        // intermediate target (missing text and triangular sprite fragments).
        _renderer.NativeContext.OMSetRenderTargets(ReadOnlySpan<nint>.Empty, 0);
        UnbindRenderTargetShaderResources();
        nint sourceView = HasSubresourceOverlap(destination, source)
            ? source.TryGetFeedbackCopyView(refresh: true)
            : source.ShaderResourceView;
        if (sourceView == 0)
            throw new NotSupportedException($"D3D11 blit cannot snapshot overlapping view: {source.PresentationDescription}, mip={source.ViewFirstLevel}, layer={source.ViewFirstLayer}");
        _renderer.NativeContext.OMSetRenderTargets(destination.RenderTargetView, 0);
        D3D11Viewport blitViewport = new()
        {
            TopLeftX = 0,
            TopLeftY = 0,
            Width = Math.Max(1, destination.Width),
            Height = Math.Max(1, destination.Height),
            MinDepth = 0,
            MaxDepth = 1,
        };
        _renderer.NativeContext.RSSetViewports(new[] { blitViewport });
        float sourceWidth = Math.Max(1f, source.Width);
        float sourceHeight = Math.Max(1f, source.Height);
        float destinationWidth = Math.Max(1f, destination.Width);
        float destinationHeight = Math.Max(1f, destination.Height);
        float sourceRegionWidth = srcRegion.X2 - srcRegion.X1;
        float sourceRegionHeight = srcRegion.Y2 - srcRegion.Y1;
        float destinationRegionWidth = dstRegion.X2 - dstRegion.X1;
        float destinationRegionHeight = dstRegion.Y2 - dstRegion.Y1;

        Span<float> constants = stackalloc float[12]
        {
            srcRegion.X1,
            srcRegion.Y1,
            sourceRegionWidth,
            sourceRegionHeight,
            dstRegion.X1,
            dstRegion.Y1,
            destinationRegionWidth,
            destinationRegionHeight,
            sourceWidth,
            sourceHeight,
            destinationWidth,
            destinationHeight,
        };
        _renderer.NativeContext.UpdateSubresource(
            _blitConstantBuffer,
            MemoryMarshal.AsBytes(constants),
            rowPitch: (uint)MemoryMarshal.AsBytes(constants).Length);

        ShaderStage[] graphicsStages =
        {
            ShaderStage.Vertex,
            ShaderStage.Fragment,
            ShaderStage.Geometry,
            ShaderStage.TessellationControl,
            ShaderStage.TessellationEvaluation,
        };
        foreach (ShaderStage stage in graphicsStages)
        {
            _renderer.NativeContext.SetShader(stage, 0);
        }

        _renderer.NativeContext.SetShader(ShaderStage.Vertex, _blitVertexShader);
        _renderer.NativeContext.SetShader(ShaderStage.Fragment, _blitPixelShader);
        _renderer.NativeContext.SetConstantBuffers(ShaderStage.Vertex, 13, new[] { _blitConstantBuffer });
        _renderer.NativeContext.SetConstantBuffers(ShaderStage.Fragment, 13, new[] { _blitConstantBuffer });
        _renderer.NativeContext.SetShaderResources(ShaderStage.Fragment, 127, new[] { sourceView });
        _renderer.NativeContext.SetSamplers(ShaderStage.Fragment, 15, new[] { d3dSampler.NativeSampler });
        _renderer.NativeContext.IASetInputLayout(0);
        _renderer.NativeContext.IASetPrimitiveTopology(D3D11PrimitiveTopology.TriangleList);
        _renderer.NativeContext.OMSetBlendState(0, 0, 0, 0, 0, uint.MaxValue);
        _renderer.NativeContext.OMSetDepthStencilState(0, 0);
        _renderer.NativeContext.RSSetState(_blitRasterizerState);
        _renderer.NativeContext.Draw(6, 0);
        _renderer.NativeContext.SetShaderResources(ShaderStage.Fragment, 127, new nint[1]);
        destination.NotifyRenderTargetWrite();
        if (trackDraw)
        {
        // Internal copies are not guest draws and must not replace the
        // presentation fallback's last drawn attachment with a temporary.
        _lastGraphicsOperation = "draw-texture";
        _lastDrawTarget = destination;
        _lastDrawTargetResource = destination.Resource;
        _lastDrawTargetsBySize[(destination.Width, destination.Height)] = destination;
        }
        }
        finally
        {
        try
        {
        _renderer.NativeContext.SetShaderResources(ShaderStage.Fragment, 127, new nint[1]);
        foreach (ShaderStage stage in new[] { ShaderStage.Vertex, ShaderStage.Fragment, ShaderStage.Geometry, ShaderStage.TessellationControl, ShaderStage.TessellationEvaluation })
        {
            _renderer.NativeContext.SetShader(stage, _program?.GetShader(stage) ?? 0);
        }
        _renderer.NativeContext.IASetInputLayout(_inputLayout);
        _renderer.NativeContext.IASetPrimitiveTopology(
            D3D11FormatConversion.ToPrimitiveTopology(_primitiveTopology, _patchVertices));
        _renderer.NativeContext.OMSetBlendState(
            _blendState,
            _blendStates[0].BlendConstant.Red,
            _blendStates[0].BlendConstant.Green,
            _blendStates[0].BlendConstant.Blue,
            _blendStates[0].BlendConstant.Alpha,
            uint.MaxValue);
        _renderer.NativeContext.OMSetDepthStencilState(
            _depthStencilState,
            _stencilTestSet ? (uint)_stencilTest.FrontFuncRef : 0);
        _renderer.NativeContext.RSSetState(_rasterizerState);
        if (_nativeViewports.Length != 0)
        {
            _renderer.NativeContext.RSSetViewports(_nativeViewports);
        }
        _renderer.NativeContext.OMSetRenderTargets(GetColorTargetViews(), GetCompatibleDepthStencilView());
        RebindGraphicsShaderResources();
        _renderer.NativeContext.SetConstantBuffers(ShaderStage.Vertex, 13, new[] { savedVsBuffer });
        _renderer.NativeContext.SetConstantBuffers(ShaderStage.Fragment, 13, new[] { savedPsBuffer });
        _renderer.NativeContext.SetSamplers(ShaderStage.Fragment, 15, new[] { savedSampler });
        }
        finally
        {
        D3D11Com.Release(savedVsBuffer);
        D3D11Com.Release(savedPsBuffer);
        D3D11Com.Release(savedSampler);
        }
        }
    }
    public void EndTransformFeedback()
    {
        _renderer.NativeContext.SOSetTargets(ReadOnlySpan<nint>.Empty, ReadOnlySpan<uint>.Empty);
        foreach (D3D11BufferResource buffer in _transformFeedbackBuffers)
        {
            buffer.CommitStreamOutput();
        }
    }
    public void SetAlphaTest(bool enable, float reference, CompareOp op)
    {
        // D3D11 has no fixed-function alpha test. The shader translator must
        // materialize this state as a discard when the feature is required.
    }
    public void SetBlendState(AdvancedBlendDescriptor blend)
    {
        // Approximate the common advanced modes with the closest fixed blend
        // operation. The shader translator remains responsible for exact modes.
        BlendDescriptor fallback = blend.Op switch
        {
            AdvancedBlendOp.Zero => new BlendDescriptor(true, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.Zero, BlendFactor.Zero, BlendOp.Add, BlendFactor.Zero, BlendFactor.Zero),
            AdvancedBlendOp.Dst => new BlendDescriptor(true, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One),
            AdvancedBlendOp.Plus or AdvancedBlendOp.PlusClamped or AdvancedBlendOp.LinearDodge => new BlendDescriptor(true, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.One, BlendFactor.One, BlendOp.Add, BlendFactor.One, BlendFactor.One),
            AdvancedBlendOp.Multiply => new BlendDescriptor(true, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.DstColor, BlendFactor.Zero, BlendOp.Add, BlendFactor.DstAlpha, BlendFactor.Zero),
            AdvancedBlendOp.Screen => new BlendDescriptor(true, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.One, BlendFactor.OneMinusSrcColor, BlendOp.Add, BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
            _ => new BlendDescriptor(true, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
        };
        SetBlendState(0, fallback);
    }
    public void SetBlendState(int index, BlendDescriptor blend)
    {
        if ((uint)index >= 8)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _blendStates[index] = blend;
        _blendStateSet[index] = true;
        RecreateBlendState();
    }
    public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
    {
        _depthBiasEnables = enables;
        _depthBiasFactor = factor;
        _depthBiasUnits = units;
        _depthBiasClamp = clamp;
        RecreateRasterizerState();
    }
    public void SetDepthClamp(bool clamp)
    {
        _depthClamp = clamp;
        RecreateRasterizerState();
    }
    public void SetDepthMode(DepthMode mode) { }
    public void SetDepthTest(DepthTestDescriptor depthTest)
    {
        _depthTest = depthTest;
        _depthTestSet = true;
        RecreateDepthStencilState();
    }
    public void SetFaceCulling(bool enable, Face face)
    {
        _cullEnabled = enable;
        _cullFace = face;
        RecreateRasterizerState();
    }
    public void SetFrontFace(FrontFace frontFace)
    {
        _frontFace = frontFace;
        RecreateRasterizerState();
    }
    public void SetIndexBuffer(BufferRange buffer, IndexType type)
    {
        _indexBuffer = buffer;
        _indexType = type;
        _indexBufferResource = _renderer.GetOptionalBuffer(buffer.Handle);

        if (type == IndexType.UByte)
        {
            EnsureUByteIndexBufferCurrent(forceBind: true);
            return;
        }

        D3D11Format format = type switch
        {
            IndexType.UShort => D3D11Format.R16Uint,
            IndexType.UInt => D3D11Format.R32Uint,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        _renderer.NativeContext.IASetIndexBuffer(_indexBufferResource?.Resource ?? 0, format, (uint)Math.Max(0, buffer.Offset));
    }
    public void SetImage(ShaderStage stage, int binding, ITexture texture)
    {
        D3D11Texture d3dTexture = texture as D3D11Texture;
        if (texture != null && (d3dTexture == null || d3dTexture.UnorderedAccessView == 0))
        {
            throw new ArgumentException("Image must belong to the D3D11 renderer and expose a UAV.", nameof(texture));
        }

        int nativeBinding = GetMappedBinding(stage, 3, binding, static mapped => mapped.Uav);
        _storageWrites.Remove((stage, nativeBinding));
        if (d3dTexture == null) _imageWrites.Remove((stage, nativeBinding));
        else _imageWrites[(stage, nativeBinding)] = d3dTexture;
        Span<nint> view = stackalloc nint[1] { d3dTexture?.UnorderedAccessView ?? 0 };
        if (stage == ShaderStage.Compute)
        {
            _renderer.NativeContext.SetUnorderedAccessViews((uint)nativeBinding, view);
        }
        else if (stage == ShaderStage.Fragment)
        {
            _renderer.NativeContext.OMSetRenderTargetsAndUnorderedAccessViews(
                GetColorTargetViews(),
                GetCompatibleDepthStencilView(),
                (uint)nativeBinding,
                view);
        }
        else
        {
            throw new NotSupportedException("D3D11 images are supported only in fragment and compute stages.");
        }
    }

    public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
    {
        if (array is not D3D11ImageArray imageArray)
        {
            throw new ArgumentException("Image array must belong to the D3D11 renderer.", nameof(array));
        }

        for (int index = 0; index < imageArray.Images.Length; index++)
        {
            SetImage(stage, binding + index, imageArray.Images[index]);
        }
    }
    public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
    {
        SetImageArray(stage, setIndex, array);
    }
    public void SetLineParameters(float width, bool smooth)
    {
        _lineWidth = Math.Max(1f, width);
        _lineSmooth = smooth;
        RecreateRasterizerState();
    }
    public void SetLogicOpState(bool enable, LogicalOp op)
    {
        // D3D11 does not expose logical blend operations. Keep the state as a
        // non-fatal compatibility path; shader-side emulation is required for
        // exact integer framebuffer behavior.
    }
    public void SetMultisampleState(MultisampleDescriptor multisample)
    {
        _alphaToCoverage = multisample.AlphaToCoverageEnable;
        RecreateBlendState();
    }
    public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
    {
        _patchVertices = Math.Clamp(vertices, 1, 32);
        if (_renderer.NativeContext is not null)
        {
            // The default tessellation levels are supplied through the shader
            // path; D3D11's IA state only needs the control-point count.
        }
    }
    public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
    {
        // Point size and sprite coordinates are shader-defined in D3D11.
    }
    public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
    {
        if (frontMode == PolygonMode.Point || backMode == PolygonMode.Point)
        {
            throw new NotSupportedException("D3D11 rasterizer has no point polygon mode.");
        }

        _frontPolygonMode = frontMode;
        _backPolygonMode = backMode;
        RecreateRasterizerState();
    }
    public void SetPrimitiveRestart(bool enable, int index)
    {
        _primitiveRestart = enable;
        _primitiveRestartIndex = index;
        // D3D11 uses the all-bits-one index for strip restart. The index
        // format selected by SetIndexBuffer determines that value.
    }
    public void SetPrimitiveTopology(PrimitiveTopology topology)
    {
        _primitiveTopology = topology;
        _renderer.NativeContext.IASetPrimitiveTopology(
            D3D11FormatConversion.ToPrimitiveTopology(topology, _patchVertices));
    }
    public void SetProgram(IProgram program)
    {
        _program = program as D3D11Program
            ?? throw new ArgumentException("Program must be created by the D3D11 renderer.", nameof(program));

        if (_program.CheckProgramLink(blocking: true) != ProgramLinkStatus.Success)
        {
            D3D11DebugTelemetry.Append(
                $"program-partial-bind: linkStatus=Failure, anyShader={_program.HasAnyNativeShader()}, " +
                $"VS={_program.HasNativeShader(ShaderStage.Vertex)}, PS={_program.HasNativeShader(ShaderStage.Fragment)}, " +
                $"GS={_program.HasNativeShader(ShaderStage.Geometry)}, HS={_program.HasNativeShader(ShaderStage.TessellationControl)}, " +
                $"DS={_program.HasNativeShader(ShaderStage.TessellationEvaluation)}, CS={_program.HasNativeShader(ShaderStage.Compute)}\n");
        }

        foreach (ShaderStage stage in Enum.GetValues<ShaderStage>())
        {
            if (stage != ShaderStage.Count)
            {
                _renderer.NativeContext.SetShader(stage, _program.GetShader(stage));
            }
        }

        // Resource registers are compacted independently for every shader
        // program. GAL is allowed to submit a uniform-buffer assignment once
        // and then switch programs without resubmitting it. Rebind the saved
        // guest assignments through the new layout, otherwise b1/b2 can keep
        // buffers from the previous shader and turn otherwise valid draws
        // completely black (Cuphead is a reliable reproducer).
        RebindUniformBuffers();

        // Vertex attributes and buffers may be submitted before the program
        // is selected. D3D11 input layouts are validated against the active
        // vertex-shader signature, so leaving the layout from the previous
        // program can silently feed zero/incorrect vertex data and produce a
        // black framebuffer even though Draw calls are being issued.
        RecreateInputLayout();
    }
    public void SetRasterizerDiscard(bool discard)
    {
        _rasterizerDiscard = discard;
        // D3D11 has no rasterizer-discard bit. A shader-side discard or a
        // stream-output-only pass is required for exact semantics.
    }
    public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
    {
        componentMask[..Math.Min(componentMask.Length, _colorMasks.Length)].CopyTo(_colorMasks);
        RecreateBlendState();
    }
    public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
    {
        _colorTargets = new D3D11Texture[colors.Length];
        nint[] nativeViews = new nint[colors.Length];

        for (int index = 0; index < colors.Length; index++)
        {
            ITexture color = colors[index];
            if (color is null)
            {
                // GAL preserves empty MRT slots in the span. D3D11 represents
                // those slots with a null RTV rather than a texture object.
                nativeViews[index] = 0;
                continue;
            }

            _colorTargets[index] = color as D3D11Texture
                ?? throw new ArgumentException("All non-null D3D11 color targets must be D3D11 textures.", nameof(colors));
            nativeViews[index] = _colorTargets[index].RenderTargetView;
        }

        _depthStencil = depthStencil is null
            ? null
            : depthStencil as D3D11Texture
                ?? throw new ArgumentException("The non-null depth/stencil target must be a D3D11 texture.", nameof(depthStencil));
        _validColorTargetCount = 0;
        for (int index = 0; index < _colorTargets.Length; index++)
        {
            if (_colorTargets[index] is not null)
            {
                _validColorTargetCount++;
            }
        }

        // D3D11 rejects a resource that is simultaneously bound as an output
        // and as a shader input.  Vulkan permits this state to be described by
        // the guest command stream, so make the transition explicit here and
        // let RebindShaderResources restore only non-conflicting views before
        // the next draw.  Without this, the debug layer forcibly nulls the
        // pixel shader input and titles such as Cuphead can render a black
        // framebuffer without reporting a failed Present().
        //
        // Unbind the previous OM targets before taking a feedback snapshot.
        // CopySubresourceRegion is itself invalid/undefined while its source is
        // still bound for output.  Captain Toad repeatedly reuses a 1280x720
        // color target as fragment input; copying it before this unbind left
        // the initial white clear in the feedback texture indefinitely.
        _renderer.NativeContext.OMSetRenderTargets(ReadOnlySpan<nint>.Empty, 0);
        UnbindRenderTargetShaderResources();
        _renderer.NativeContext.OMSetRenderTargets(nativeViews, GetCompatibleDepthStencilView());
    }
    public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
    {
        // D3D11 exposes at most 16 simultaneous scissors. Guest register
        // snapshots can contain trailing or malformed entries; never forward
        // an unbounded array to the native runtime.
        int regionCount = Math.Min(regions.Length, 16);
        D3D11Rect[] nativeRects = new D3D11Rect[regionCount];

        for (int index = 0; index < regionCount; index++)
        {
            Rectangle<int> region = regions[index];
            float x = region.X;
            float y = region.Y;
            float width = region.Width;
            float height = region.Height;

            int left = ClampToInt(x);
            int top = ClampToInt(y);
            int right = ClampToInt(x + width);
            int bottom = ClampToInt(y + height);
            if (right < left) (left, right) = (right, left);
            if (bottom < top) (top, bottom) = (bottom, top);

            // GAL already applies render scale. Keep the scissor independent
            // of the current attachment: a later target switch can reuse it
            // without resubmitting state. D3D11 clips to the target at raster time.

            nativeRects[index] = new D3D11Rect
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom,
            };

        }

        _renderer.NativeContext.RSSetScissorRects(nativeRects);
        _scissorCount = regionCount;
        _firstScissorWidth = regionCount == 0 ? 0 : regions[0].Width;
        _firstScissorHeight = regionCount == 0 ? 0 : regions[0].Height;
        bool hasScissors = regionCount != 0;
        if (_hasScissors != hasScissors)
        {
            _hasScissors = hasScissors;
            RecreateRasterizerState();
        }
    }

    private static int ClampToInt(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        return value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;
    }
    public void SetStencilTest(StencilTestDescriptor stencilTest)
    {
        _stencilTest = stencilTest;
        _stencilTestSet = true;
        RecreateDepthStencilState();
    }
    public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
    {
        ShaderStage[] stages =
        {
            ShaderStage.Compute,
            ShaderStage.Vertex,
            ShaderStage.Fragment,
            ShaderStage.Geometry,
            ShaderStage.TessellationControl,
            ShaderStage.TessellationEvaluation,
        };

        nint[] srv = new nint[1];
        nint[] uav = new nint[1];

        foreach (BufferAssignment assignment in buffers)
        {
            foreach (ShaderStage stage in stages)
            {
                if (!TryGetMappedBinding(stage, 1, assignment.Binding, out D3D11ResourceBinding mapped))
                {
                    continue;
                }

                D3D11BufferResource resource = _renderer.GetOptionalBuffer(assignment.Range.Handle);

                if (mapped.Srv >= 0)
                {
                    srv[0] = resource?.ShaderResourceView ?? 0;
                    D3D11DebugTelemetry.TraceResourceBind(
                        "storage-buffer-srv",
                        stage.ToString(),
                        mapped.Srv,
                        resource?.Resource ?? 0,
                        srv[0],
                        0,
                        $"assignmentBinding={assignment.Binding}, rangeOffset={assignment.Range.Offset}, rangeSize={assignment.Range.Size}");
                    RememberShaderResource(stage, mapped.Srv, srv[0]);
                    _renderer.NativeContext.SetShaderResources(stage, (uint)mapped.Srv, srv);
                }
                if (mapped.Uav >= 0)
                {
                    if (stage == ShaderStage.Compute || stage == ShaderStage.Fragment)
                    {
                        _imageWrites.Remove((stage, mapped.Uav));
                        if (resource is null) _storageWrites.Remove((stage, mapped.Uav));
                        else _storageWrites[(stage, mapped.Uav)] = resource;
                    }
                    uav[0] = resource?.UnorderedAccessView ?? 0;
                    D3D11DebugTelemetry.TraceResourceBind(
                        "storage-buffer-uav",
                        stage.ToString(),
                        mapped.Uav,
                        resource?.Resource ?? 0,
                        0,
                        uav[0],
                        $"assignmentBinding={assignment.Binding}, rangeOffset={assignment.Range.Offset}, rangeSize={assignment.Range.Size}");
                    if (stage == ShaderStage.Compute)
                    {
                        _renderer.NativeContext.SetUnorderedAccessViews((uint)mapped.Uav, uav);
                    }
                    else if (stage == ShaderStage.Fragment)
                    {
                        _renderer.NativeContext.OMSetRenderTargetsAndUnorderedAccessViews(
                            GetColorTargetViews(), GetCompatibleDepthStencilView(), (uint)mapped.Uav, uav);
                    }
                }
            }
        }
    }

    private void RebindUniformBuffers()
    {
        if (_program is null || _uniformBufferAssignments.Count == 0)
        {
            return;
        }

        ShaderStage[] stages =
        {
            ShaderStage.Vertex,
            ShaderStage.Fragment,
            ShaderStage.Geometry,
            ShaderStage.TessellationControl,
            ShaderStage.TessellationEvaluation,
            ShaderStage.Compute,
        };
        nint[] constantBuffer = new nint[1];
        foreach ((int guestBinding, BufferRange range) in _uniformBufferAssignments)
        {
            D3D11BufferResource resource = _renderer.GetOptionalBuffer(range.Handle);
            foreach (ShaderStage stage in stages)
            {
                if (!TryGetMappedBinding(stage, 0, guestBinding, out D3D11ResourceBinding mapped) || mapped.Cbv < 0)
                {
                    continue;
                }

                constantBuffer[0] = resource is null
                    ? 0
                    : resource.GetConstantBuffer(range.Offset, range.Size);
                TraceConstantBuffer(stage, guestBinding, mapped.Cbv, range, resource);
                _renderer.NativeContext.SetConstantBuffers(stage, (uint)mapped.Cbv, constantBuffer);
                _boundUniformBuffers[(stage, mapped.Cbv)] = constantBuffer[0];
            }
        }
    }

    public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
    {
        int srvBinding = GetMappedBinding(stage, 2, binding, static mapped => mapped.Srv);
        int samplerBinding = GetMappedBinding(stage, 2, binding, static mapped => mapped.Sampler);
        D3D11Texture d3dTexture = texture as D3D11Texture;
        D3D11Sampler d3dSampler = sampler as D3D11Sampler;

        // GAL uses null to explicitly unbind stale resources. Treat a null or
        // foreign object as an unbind at this backend boundary instead of
        // terminating the entire CoreHost session.
        if (d3dTexture is null || d3dSampler is null)
        {
            D3D11DebugTelemetry.TraceResourceBind(
                "texture-bind-rejected",
                stage.ToString(),
                binding,
                0,
                0,
                0,
                $"textureType={texture?.GetType().Name ?? "null"}, samplerType={sampler?.GetType().Name ?? "null"}");
            Span<nint> empty = stackalloc nint[1];
            RememberShaderResource(stage, srvBinding, null);
            _renderer.NativeContext.SetShaderResources(stage, (uint)srvBinding, empty);
            _renderer.NativeContext.SetSamplers(stage, (uint)samplerBinding, empty);
            return;
        }

        Span<nint> textureView = stackalloc nint[1] { d3dTexture.ShaderResourceView };
        Span<nint> samplerView = stackalloc nint[1] { d3dSampler.NativeSampler };
        RememberShaderResource(stage, srvBinding, d3dTexture);
        bool renderTargetConflict = IsActiveRenderTarget(d3dTexture);
        // D3D11 cannot sample a subresource while that same subresource is
        // bound for output.  Detaching the render target here used to make the
        // bind legal, but it also discarded every following guest draw (most
        // visibly: Cuphead kept running at 60 FPS with a completely black
        // picture).  Snapshot the current contents on the GPU and sample that
        // immutable view while leaving the guest render target intact.
        if (renderTargetConflict)
        {
            // The render-target transition already refreshed the feedback
            // snapshot. Binding the same texture again during the pass must
            // reuse that immutable view: refreshing here after every draw
            // caused Mario Wonder to issue several full GPU copies per frame
            // while entering a stage.
            nint feedbackView = SnapshotActiveRenderTarget(d3dTexture, refresh: false);
            if (feedbackView != 0)
            {
                textureView[0] = feedbackView;
            }
            long feedbackBindCount = ++_hazardFeedbackBindCount;
            if (ShouldTraceFrequentEvent(feedbackBindCount))
            {
                D3D11DebugTelemetry.Append(
                    $"hazard-feedback-bound: count={feedbackBindCount}, stage={stage}, slot={srvBinding}, " +
                    $"copy={(feedbackView != 0 ? "ok" : "unsupported-view")}\n");
            }
        }
        D3D11DebugTelemetry.TraceTextureBind(
            stage.ToString(),
            srvBinding,
            d3dTexture.Info,
            d3dTexture.Resource,
            textureView[0],
            samplerView[0],
            renderTargetConflict);
        _renderer.NativeContext.SetShaderResources(stage, (uint)srvBinding, textureView);
        _renderer.NativeContext.SetSamplers(stage, (uint)samplerBinding, samplerView);
    }

    public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
    {
        if (array is not D3D11TextureArray textureArray)
        {
            throw new ArgumentException("Texture array must belong to the D3D11 renderer.", nameof(array));
        }

        nint[] textures = new nint[textureArray.Textures.Length];
        nint[] samplers = new nint[textureArray.Samplers.Length];
        for (int index = 0; index < textures.Length; index++)
        {
            textures[index] = (textureArray.Textures[index] as D3D11Texture)?.ShaderResourceView ?? 0;
        }
        for (int index = 0; index < samplers.Length; index++)
        {
            samplers[index] = (textureArray.Samplers[index] as D3D11Sampler)?.NativeSampler ?? 0;
        }
        for (int index = 0; index < textures.Length; index++)
        {
            int mapped = GetMappedBinding(stage, 2, binding + index, static value => value.Srv);
            D3D11Texture texture = textureArray.Textures[index] as D3D11Texture;
            RememberShaderResource(stage, mapped, texture);
            if (IsActiveRenderTarget(texture))
            {
                nint feedbackView = SnapshotActiveRenderTarget(texture, refresh: false);
                if (feedbackView != 0)
                {
                    textures[index] = feedbackView;
                }
            }
            nint view = textures[index];
            D3D11DebugTelemetry.TraceTextureArrayBind(stage.ToString(), mapped, index, texture, view, samplers.Length > index ? samplers[index] : 0, false);
            _renderer.NativeContext.SetShaderResources(stage, (uint)mapped, new[] { view });
        }
        for (int index = 0; index < samplers.Length; index++)
        {
            int mapped = GetMappedBinding(stage, 2, binding + index, static value => value.Sampler);
            _renderer.NativeContext.SetSamplers(stage, (uint)mapped, samplers.AsSpan(index, 1));
        }
    }
    public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
    {
        SetTextureArray(stage, setIndex, array);
    }

    private nint SnapshotActiveRenderTarget(D3D11Texture texture, bool refresh)
    {
        // Copying from a resource that is still bound for output is itself a
        // D3D11 hazard. Temporarily clear OM, perform the GPU-side snapshot,
        // then restore the exact guest attachments. The managed attachment
        // state is deliberately never modified here.
        _renderer.NativeContext.OMSetRenderTargets(new nint[_colorTargets.Length], 0);
        nint view = texture?.TryGetFeedbackCopyView(refresh) ?? 0;
        _renderer.NativeContext.OMSetRenderTargets(GetColorTargetViews(), GetCompatibleDepthStencilView());
        return view;
    }

    private nint[] GetColorTargetViews()
    {
        nint[] views = new nint[_colorTargets.Length];
        for (int index = 0; index < views.Length; index++)
        {
            views[index] = _colorTargets[index]?.RenderTargetView ?? 0;
        }
        return views;
    }

    private nint GetCompatibleDepthStencilView()
    {
        if (_depthStencil is null || _depthStencil.DepthStencilView == 0)
        {
            return 0;
        }

        // D3D11 requires every active RTV and the DSV to have identical
        // dimensions and sample count. Vulkan permits a broader set of
        // framebuffer descriptions, and the guest can transiently leave a
        // 1280x720 depth attachment paired with a 1920x1080 color target while
        // rebuilding a framebuffer. Passing that pair to OMSetRenderTargets
        // produces the driver's warning 388 and leaves the game on a clear
        // color (Let’s Go was the first title where this became permanent).
        // Keep the color target valid and omit only the incompatible depth
        // attachment until the guest supplies a matching one.
        foreach (D3D11Texture colorTarget in _colorTargets)
        {
            if (colorTarget is null || colorTarget.RenderTargetView == 0)
            {
                continue;
            }

            if (colorTarget.Width != _depthStencil.Width ||
                colorTarget.Height != _depthStencil.Height ||
                colorTarget.Info.Samples != _depthStencil.Info.Samples)
            {
                string mismatch =
                    $"color={colorTarget.Width}x{colorTarget.Height}/samples{colorTarget.Info.Samples};" +
                    $"depth={_depthStencil.Width}x{_depthStencil.Height}/samples{_depthStencil.Info.Samples}";
                if (!string.Equals(_lastDepthAttachmentMismatch, mismatch, StringComparison.Ordinal))
                {
                    _lastDepthAttachmentMismatch = mismatch;
                    D3D11DebugTelemetry.Append(
                        $"depth-attachment-skipped: {mismatch}; reason=rtv-dsv-dimensions-or-samples-mismatch\n");
                }
                return 0;
            }
        }

        return _depthStencil.DepthStencilView;
    }

    public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
    {
        nint[] nativeBuffers = new nint[buffers.Length];
        uint[] offsets = new uint[buffers.Length];
        _transformFeedbackBuffers = new D3D11BufferResource[buffers.Length];
        for (int index = 0; index < buffers.Length; index++)
        {
            D3D11BufferResource resource = _renderer.GetOptionalBuffer(buffers[index].Handle);
            _transformFeedbackBuffers[index] = resource;
            nativeBuffers[index] = resource?.PrepareStreamOutput() ?? 0;
            offsets[index] = (uint)Math.Max(0, buffers[index].Offset);
        }
        _renderer.NativeContext.SOSetTargets(nativeBuffers, offsets);
    }
    public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
    {
        ShaderStage[] stages =
        {
            ShaderStage.Vertex,
            ShaderStage.Fragment,
            ShaderStage.Geometry,
            ShaderStage.TessellationControl,
            ShaderStage.TessellationEvaluation,
            ShaderStage.Compute,
        };

        nint[] constantBuffer = new nint[1];

        foreach (BufferAssignment assignment in buffers)
        {
            // Preserve the GAL uniform namespace, even when the current
            // program does not use this binding. Storage buffers belong to
            // descriptor set 1 and must never overwrite these set-0 entries.
            _uniformBufferAssignments[assignment.Binding] = assignment.Range;
            foreach (ShaderStage stage in stages)
            {
                if (TryGetMappedBinding(stage, 0, assignment.Binding, out D3D11ResourceBinding mapped) && mapped.Cbv >= 0)
                {
                    D3D11BufferResource resource = _renderer.GetOptionalBuffer(assignment.Range.Handle);
                    constantBuffer[0] = resource is null
                        ? 0
                        : resource.GetConstantBuffer(
                            assignment.Range.Offset,
                            assignment.Range.Size);
                    TraceConstantBuffer(stage, assignment.Binding, mapped.Cbv, assignment.Range, resource);
                    _renderer.NativeContext.SetConstantBuffers(stage, (uint)mapped.Cbv, constantBuffer);
                    _boundUniformBuffers[(stage, mapped.Cbv)] = constantBuffer[0];
                }
            }
        }
    }
    public void SetUserClipDistance(int index, bool enableClip)
    {
        if ((uint)index >= 8)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        // D3D11 clip planes are not exposed as a fixed-function state. The
        // shader translator must emit SV_ClipDistance when this is required.
    }
    public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
    {
        _vertexAttribs = vertexAttribs.ToArray();
        if (_vertexAttribs.Length == 0)
        {
            foreach (var buffer in _half3Buffers.Values) buffer.Dispose();
            _half3Buffers.Clear();
        }
        RecreateInputLayout();
    }
    public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
    {
        nint[] nativeBuffers = new nint[vertexBuffers.Length];
        uint[] strides = new uint[vertexBuffers.Length];
        uint[] offsets = new uint[vertexBuffers.Length];

        for (int index = 0; index < vertexBuffers.Length; index++)
        {
            VertexBufferDescriptor descriptor = vertexBuffers[index];
            if (descriptor.Divisor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(descriptor.Divisor));
            }

            D3D11BufferResource resource = _renderer.GetOptionalBuffer(descriptor.Buffer.Handle);
            nativeBuffers[index] = resource?.Resource ?? 0;
            strides[index] = resource is null ? 0u : (uint)Math.Max(0, descriptor.Stride);
            offsets[index] = resource is null ? 0u : (uint)Math.Max(0, descriptor.Buffer.Offset);
        }

        _vertexBuffers = vertexBuffers.ToArray();
        _renderer.NativeContext.IASetVertexBuffers(0, nativeBuffers, strides, offsets);
        RecreateInputLayout();
    }
    private void GetActiveRenderTargetDimensions(out float targetWidth, out float targetHeight)
    {
        D3D11Texture firstColor = null;
        if (_colorTargets != null)
        {
            for (int i = 0; i < _colorTargets.Length; i++)
            {
                if (_colorTargets[i] is not null)
                {
                    firstColor = _colorTargets[i];
                    break;
                }
            }
        }

        float backbufferWidth = _renderer.Host?.Width > 0 ? _renderer.Host.Width : 1280f;
        float backbufferHeight = _renderer.Host?.Height > 0 ? _renderer.Host.Height : 720f;

        targetWidth = firstColor != null ? firstColor.Info.Width 
            : (_depthStencil != null ? _depthStencil.Info.Width : backbufferWidth);
        targetHeight = firstColor != null ? firstColor.Info.Height 
            : (_depthStencil != null ? _depthStencil.Info.Height : backbufferHeight);

        if (targetWidth <= 0f) targetWidth = backbufferWidth;
        if (targetHeight <= 0f) targetHeight = backbufferHeight;
    }

    public void SetViewports(ReadOnlySpan<Viewport> viewports)
    {
        GetActiveRenderTargetDimensions(out float targetWidth, out float targetHeight);

        D3D11Viewport[] nativeViewports;
        int activeViewportCount = 0;
        for (int index = 0; index < viewports.Length; index++)
        {
            Rectangle<float> region = viewports[index].Region;
            if (float.IsFinite(region.X) && float.IsFinite(region.Y) &&
                float.IsFinite(region.Width) && float.IsFinite(region.Height) &&
                (MathF.Abs(region.Width) >= 0.0001f || MathF.Abs(region.Height) >= 0.0001f))
            {
                activeViewportCount = index + 1;
            }
        }

        if (_singleViewportCompatibility && activeViewportCount > 1)
        {
            activeViewportCount = 1;
        }

        // Maxwell exposes sixteen viewport registers and the GAL state span
        // consequently contains sixteen entries even when only viewport zero
        // is enabled. Do not turn trailing zero-initialized registers into
        // sixteen full-screen D3D11 viewports. Besides wasting driver work,
        // old Intel D3D11 drivers have been observed crashing in
        // RSSetViewports during Mario Wonder's level transition in that state.
        if (activeViewportCount == 0)
        {
            nativeViewports = new[]
            {
                new D3D11Viewport
                {
                    TopLeftX = 0f,
                    TopLeftY = 0f,
                    Width = targetWidth,
                    Height = targetHeight,
                    MinDepth = 0f,
                    MaxDepth = 1f,
                }
            };
        }
        else
        {
            nativeViewports = new D3D11Viewport[activeViewportCount];

            for (int index = 0; index < activeViewportCount; index++)
            {
                Viewport viewport = viewports[index];
                float x = float.IsFinite(viewport.Region.X) ? viewport.Region.X : 0f;
                float y = float.IsFinite(viewport.Region.Y) ? viewport.Region.Y : 0f;
                float width = float.IsFinite(viewport.Region.Width) ? viewport.Region.Width : targetWidth;
                float height = float.IsFinite(viewport.Region.Height) ? viewport.Region.Height : targetHeight;

                // A viewport larger than its attachment is valid. In particular,
                // transform-disabled draws normalize pixel positions using this
                // exact GAL extent in SupportBufferUpdater. Fitting it to the
                // attachment or host aspect shrinks/displaces those primitives.

                // GAL uses signed viewport extents to express axis flips. D3D11
                // rejects negative Width/Height, so preserve the covered rectangle
                // while normalizing it to a valid native viewport.
                if (width < 0f)
                {
                    x += width;
                    width = -width;
                }
                if (height < 0f)
                {
                    y += height;
                    height = -height;
                }

                // Normalize depth range components
                float minDepth = float.IsFinite(viewport.DepthNear)
                    ? Math.Clamp(viewport.DepthNear, 0f, 1f)
                    : 0f;
                float maxDepth = float.IsFinite(viewport.DepthFar)
                    ? Math.Clamp(viewport.DepthFar, 0f, 1f)
                    : 1f;
                if (maxDepth < minDepth)
                {
                    (minDepth, maxDepth) = (maxDepth, minDepth);
                }

                if (width <= 0f) width = targetWidth;
                if (height <= 0f) height = targetHeight;

                // D3D11 feature level 11 limits viewport dimensions to 16384
                // and top-left coordinates to [-32768, 32767]. Clamp guest
                // garbage here rather than forwarding it to the display
                // driver, where invalid floats can become an access violation.
                x = Math.Clamp(x, -32768f, 32767f);
                y = Math.Clamp(y, -32768f, 32767f);
                width = Math.Clamp(width, 0.0001f, 16384f);
                height = Math.Clamp(height, 0.0001f, 16384f);

                nativeViewports[index] = new D3D11Viewport
                {
                    TopLeftX = x,
                    TopLeftY = y,
                    Width = width,
                    Height = height,
                    MinDepth = minDepth,
                    MaxDepth = maxDepth,
                };
            }
        }

        _viewportSetCount++;

        // Viewport programming is a hot path.  Keep enough samples to diagnose
        // invalid guest state without formatting and writing one trace line per
        // draw.  The old unconditional condition produced hundreds of MB of
        // text in a few minutes and could make startup screens appear frozen.
        if (D3D11DebugTelemetry.Enabled &&
            (_viewportSetCount <= 16 || (_viewportSetCount & (_viewportSetCount - 1)) == 0 || _viewportSetCount % 4096 == 0))
        {
            D3D11DebugTelemetry.Append(
                $"RSSetViewports: sample={_viewportSetCount}, inputCount={viewports.Length}, appliedCount={nativeViewports.Length}, singleViewportCompatibility={_singleViewportCompatibility}, target={targetWidth}x{targetHeight}, " +
                $"v0=[x={nativeViewports[0].TopLeftX}, y={nativeViewports[0].TopLeftY}, w={nativeViewports[0].Width}, h={nativeViewports[0].Height}, minD={nativeViewports[0].MinDepth}, maxD={nativeViewports[0].MaxDepth}]\n");
        }

        _renderer.NativeContext.RSSetViewports(nativeViewports);
        _nativeViewports = nativeViewports;
        _viewportCount = nativeViewports.Length;
        _firstViewportWidth = nativeViewports.Length == 0 ? 0f : nativeViewports[0].Width;
        _firstViewportHeight = nativeViewports.Length == 0 ? 0f : nativeViewports[0].Height;
    }

    public void TextureBarrier()
    {
        if (!D3D11PerformanceSettings.DeferImplicitBarriers)
        {
            _renderer.NativeContext.Flush();
        }
    }
    public void TextureBarrierTiled()
    {
        if (!D3D11PerformanceSettings.DeferImplicitBarriers)
        {
            _renderer.NativeContext.Flush();
        }
    }

    public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
    {
        return false;
    }

    public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
    {
        return false;
    }

    public void EndHostConditionalRendering()
    {
    }

    public void Dispose()
    {
        foreach (var buffer in _half3Buffers.Values) buffer.Dispose();
        _half3Buffers.Clear();
        D3D11Com.Release(_inputLayout);
        D3D11Com.Release(_defaultVertexBuffer);
        D3D11Com.Release(_zeroVertexBuffer);
        D3D11Com.Release(_rasterizerState);
        D3D11Com.Release(_depthStencilState);
        D3D11Com.Release(_blendState);
        D3D11Com.Release(_blitVertexShader);
        D3D11Com.Release(_blitPixelShader);
        D3D11Com.Release(_blitConstantBuffer);
        D3D11Com.Release(_blitRasterizerState);
        _copyPointSampler?.Dispose();
        _copyLinearSampler?.Dispose();
        D3D11Com.Release(_clearPixelShader);
        D3D11Com.Release(_clearConstantBuffer);
        foreach (nint clearBlendState in _clearBlendStates)
        {
            D3D11Com.Release(clearBlendState);
        }
        D3D11Com.Release(_diagnosticPixelShader);
        D3D11Com.Release(_expandedUByteIndexBuffer);
        D3D11Com.Release(_quadIndexBuffer);
        D3D11Com.Release(_convertedQuadIndexBuffer);
        D3D11Com.Release(_fanIndexBuffer);
        D3D11Com.Release(_convertedFanIndexBuffer);
        _inputLayout = 0;
        _defaultVertexBuffer = 0;
        _zeroVertexBuffer = 0;
        _rasterizerState = 0;
        _depthStencilState = 0;
        _blendState = 0;
        _clearPixelShader = 0;
        _clearConstantBuffer = 0;
        _diagnosticPixelShader = 0;
        _expandedUByteIndexBuffer = 0;
        _expandedUByteIndexCapacity = 0;
        _quadIndexBuffer = 0;
        _quadIndexBufferCapacity = 0;
        _quadIndexBufferPatternCount = 0;
        _convertedQuadIndexBuffer = 0;
        _convertedQuadIndexBufferCapacity = 0;
        _fanIndexBuffer = 0;
        _fanIndexBufferCapacity = 0;
        _fanIndexBufferPatternCount = 0;
        _convertedFanIndexBuffer = 0;
        _convertedFanIndexBufferCapacity = 0;
    }

    private void DrawQuads(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
    {
        int primitiveCount = Math.Max(0, vertexCount / 4);
        int convertedCount = checked(primitiveCount * 6);
        if (convertedCount == 0 || instanceCount <= 0)
        {
            return;
        }

        if (_quadIndexBuffer == 0 || _quadIndexBufferPatternCount < convertedCount)
        {
            uint[] indices = new uint[convertedCount];
            int output = 0;
            for (uint primitive = 0; primitive < (uint)primitiveCount; primitive++)
            {
                uint vertex = primitive * 4u;
                indices[output++] = vertex;
                indices[output++] = vertex + 1u;
                indices[output++] = vertex + 2u;
                indices[output++] = vertex;
                indices[output++] = vertex + 2u;
                indices[output++] = vertex + 3u;
            }

            EnsureTemporaryIndexBuffer(
                ref _quadIndexBuffer,
                ref _quadIndexBufferCapacity,
                MemoryMarshal.AsBytes(indices.AsSpan()));
            _quadIndexBufferPatternCount = convertedCount;
        }

        _renderer.NativeContext.IASetPrimitiveTopology(D3D11PrimitiveTopology.TriangleList);
        _renderer.NativeContext.IASetIndexBuffer(_quadIndexBuffer, D3D11Format.R32Uint, 0);
        _renderer.NativeContext.DrawIndexedInstanced(
            (uint)convertedCount,
            (uint)instanceCount,
            0,
            firstVertex,
            (uint)firstInstance);

        RestoreGuestIndexBuffer();
        RestoreGuestPrimitiveTopology();
        TraceQuadConversion(indexed: false, vertexCount, convertedCount);
    }

    private void DrawIndexedQuads(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
    {
        int primitiveCount = Math.Max(0, indexCount / 4);
        int convertedCount = checked(primitiveCount * 6);
        if (convertedCount == 0 || instanceCount <= 0 || _indexBufferResource is null)
        {
            return;
        }

        int indexSize = _indexType switch
        {
            IndexType.UByte => sizeof(byte),
            IndexType.UShort => sizeof(ushort),
            IndexType.UInt => sizeof(uint),
            _ => throw new ArgumentOutOfRangeException(nameof(_indexType)),
        };
        int sourceSize = checked(primitiveCount * 4 * indexSize);
        int sourceOffset = checked(_indexBuffer.Offset + firstIndex * indexSize);
        byte[] source = new byte[sourceSize];
        _indexBufferResource.CopyData(sourceOffset, source);

        uint restartValue = _indexType switch
        {
            IndexType.UByte => byte.MaxValue,
            IndexType.UShort => ushort.MaxValue,
            _ => uint.MaxValue,
        };
        uint[] indices = new uint[convertedCount];
        int output = 0;
        for (int primitive = 0; primitive < primitiveCount; primitive++)
        {
            int sourceIndex = primitive * 4;
            uint i0 = ReadGuestIndex(source, sourceIndex, indexSize);
            uint i1 = ReadGuestIndex(source, sourceIndex + 1, indexSize);
            uint i2 = ReadGuestIndex(source, sourceIndex + 2, indexSize);
            uint i3 = ReadGuestIndex(source, sourceIndex + 3, indexSize);

            if (_primitiveRestart)
            {
                i0 = i0 == restartValue ? uint.MaxValue : i0;
                i1 = i1 == restartValue ? uint.MaxValue : i1;
                i2 = i2 == restartValue ? uint.MaxValue : i2;
                i3 = i3 == restartValue ? uint.MaxValue : i3;
            }

            indices[output++] = i0;
            indices[output++] = i1;
            indices[output++] = i2;
            indices[output++] = i0;
            indices[output++] = i2;
            indices[output++] = i3;
        }

        EnsureTemporaryIndexBuffer(
            ref _convertedQuadIndexBuffer,
            ref _convertedQuadIndexBufferCapacity,
            MemoryMarshal.AsBytes(indices.AsSpan()));

        _renderer.NativeContext.IASetPrimitiveTopology(D3D11PrimitiveTopology.TriangleList);
        _renderer.NativeContext.IASetIndexBuffer(_convertedQuadIndexBuffer, D3D11Format.R32Uint, 0);
        _renderer.NativeContext.DrawIndexedInstanced(
            (uint)convertedCount,
            (uint)instanceCount,
            0,
            firstVertex,
            (uint)firstInstance);

        RestoreGuestIndexBuffer();
        RestoreGuestPrimitiveTopology();
        TraceQuadConversion(indexed: true, indexCount, convertedCount);
    }

    private static uint ReadGuestIndex(ReadOnlySpan<byte> source, int index, int indexSize)
    {
        int offset = checked(index * indexSize);
        return indexSize switch
        {
            sizeof(byte) => source[offset],
            sizeof(ushort) => MemoryMarshal.Read<ushort>(source.Slice(offset, sizeof(ushort))),
            sizeof(uint) => MemoryMarshal.Read<uint>(source.Slice(offset, sizeof(uint))),
            _ => throw new ArgumentOutOfRangeException(nameof(indexSize)),
        };
    }

    private void DrawTriangleFan(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
    {
        int triangleCount = Math.Max(0, vertexCount - 2);
        int convertedCount = checked(triangleCount * 3);
        if (convertedCount == 0 || instanceCount <= 0)
        {
            return;
        }

        if (_fanIndexBuffer == 0 || _fanIndexBufferPatternCount < convertedCount)
        {
            uint[] indices = new uint[convertedCount];
            int output = 0;
            for (uint triangle = 0; triangle < (uint)triangleCount; triangle++)
            {
                indices[output++] = 0;
                indices[output++] = triangle + 1;
                indices[output++] = triangle + 2;
            }

            EnsureTemporaryIndexBuffer(ref _fanIndexBuffer, ref _fanIndexBufferCapacity, MemoryMarshal.AsBytes(indices.AsSpan()));
            _fanIndexBufferPatternCount = convertedCount;
        }

        _renderer.NativeContext.IASetPrimitiveTopology(D3D11PrimitiveTopology.TriangleList);
        _renderer.NativeContext.IASetIndexBuffer(_fanIndexBuffer, D3D11Format.R32Uint, 0);
        _renderer.NativeContext.DrawIndexedInstanced((uint)convertedCount, (uint)instanceCount, 0, firstVertex, (uint)firstInstance);
        RestoreGuestIndexBuffer();
        RestoreGuestPrimitiveTopology();
        TraceFanConversion(indexed: false, vertexCount, convertedCount);
    }

    private void DrawIndexedTriangleFan(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
    {
        if (indexCount < 3 || instanceCount <= 0 || _indexBufferResource is null)
        {
            return;
        }

        int indexSize = _indexType switch
        {
            IndexType.UByte => sizeof(byte),
            IndexType.UShort => sizeof(ushort),
            IndexType.UInt => sizeof(uint),
            _ => throw new ArgumentOutOfRangeException(nameof(_indexType)),
        };
        int sourceSize = checked(indexCount * indexSize);
        int sourceOffset = checked(_indexBuffer.Offset + firstIndex * indexSize);
        byte[] source = new byte[sourceSize];
        _indexBufferResource.CopyData(sourceOffset, source);
        uint restartValue = _indexType switch
        {
            IndexType.UByte => byte.MaxValue,
            IndexType.UShort => ushort.MaxValue,
            _ => uint.MaxValue,
        };

        // Worst case is one uninterrupted fan. Segments separated by a
        // primitive-restart marker only reduce the resulting index count.
        uint[] converted = new uint[checked((indexCount - 2) * 3)];
        int output = 0;
        int segmentStart = 0;
        for (int input = 0; input <= indexCount; input++)
        {
            bool end = input == indexCount;
            bool restart = !end && _primitiveRestart && ReadGuestIndex(source, input, indexSize) == restartValue;
            if (!end && !restart)
            {
                continue;
            }

            int segmentLength = input - segmentStart;
            if (segmentLength >= 3)
            {
                uint anchor = ReadGuestIndex(source, segmentStart, indexSize);
                for (int index = 1; index < segmentLength - 1; index++)
                {
                    converted[output++] = anchor;
                    converted[output++] = ReadGuestIndex(source, segmentStart + index, indexSize);
                    converted[output++] = ReadGuestIndex(source, segmentStart + index + 1, indexSize);
                }
            }

            segmentStart = input + 1;
        }

        if (output == 0)
        {
            return;
        }

        EnsureTemporaryIndexBuffer(
            ref _convertedFanIndexBuffer,
            ref _convertedFanIndexBufferCapacity,
            MemoryMarshal.AsBytes(converted.AsSpan(0, output)));
        _renderer.NativeContext.IASetPrimitiveTopology(D3D11PrimitiveTopology.TriangleList);
        _renderer.NativeContext.IASetIndexBuffer(_convertedFanIndexBuffer, D3D11Format.R32Uint, 0);
        _renderer.NativeContext.DrawIndexedInstanced((uint)output, (uint)instanceCount, 0, firstVertex, (uint)firstInstance);
        RestoreGuestIndexBuffer();
        RestoreGuestPrimitiveTopology();
        TraceFanConversion(indexed: true, indexCount, output);
    }

    private void EnsureTemporaryIndexBuffer(ref nint buffer, ref int capacity, ReadOnlySpan<byte> data)
    {
        int nativeSize = Math.Max(4, checked((data.Length + 3) & ~3));
        if (buffer == 0 || capacity < nativeSize)
        {
            D3D11Com.Release(buffer);
            D3D11BufferDesc descriptor = new()
            {
                ByteWidth = (uint)nativeSize,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.IndexBuffer,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
                StructureByteStride = 0,
            };
            buffer = _renderer.NativeDevice.CreateBuffer(in descriptor);
            capacity = nativeSize;
        }

        _renderer.NativeContext.UpdateSubresource(buffer, data);
    }

    private void RestoreGuestIndexBuffer()
    {
        if (_indexType == IndexType.UByte)
        {
            EnsureUByteIndexBufferCurrent(forceBind: true);
            return;
        }

        D3D11Format format = _indexType == IndexType.UInt
            ? D3D11Format.R32Uint
            : D3D11Format.R16Uint;
        _renderer.NativeContext.IASetIndexBuffer(
            _indexBufferResource?.Resource ?? 0,
            format,
            (uint)Math.Max(0, _indexBuffer.Offset));
    }

    private void RestoreGuestPrimitiveTopology()
    {
        _renderer.NativeContext.IASetPrimitiveTopology(
            D3D11FormatConversion.ToPrimitiveTopology(_primitiveTopology, _patchVertices));
    }

    private void TraceQuadConversion(bool indexed, int sourceCount, int convertedCount)
    {
        _quadConversionCount++;
        if (_quadConversionCount == 1 || _quadConversionCount == 10 ||
            _quadConversionCount == 100 || _quadConversionCount % 1000 == 0)
        {
            D3D11DebugTelemetry.Append(
                $"quad-topology-converted: indexed={indexed}, sourceCount={sourceCount}, " +
                $"triangleIndexCount={convertedCount}, sample={_quadConversionCount}\n");
        }
    }

    private void TraceFanConversion(bool indexed, int sourceCount, int convertedCount)
    {
        _fanConversionCount++;
        if (_fanConversionCount == 1 || _fanConversionCount == 10 ||
            _fanConversionCount == 100 || _fanConversionCount % 1000 == 0)
        {
            D3D11DebugTelemetry.Append(
                $"triangle-fan-converted: indexed={indexed}, sourceCount={sourceCount}, " +
                $"triangleIndexCount={convertedCount}, sample={_fanConversionCount}\n");
        }
    }

    private void EnsureBlitResources()
    {
        if (_blitVertexShader != 0)
        {
            return;
        }

        if (_renderer.Host.ShaderCompiler is null)
        {
            throw new InvalidOperationException("DrawTexture requires the host HLSL compiler callback.");
        }

        byte[] vertexBytecode = _renderer.Host.ShaderCompiler(ShaderStage.Vertex, BlitVertexShader);
        byte[] pixelBytecode = _renderer.Host.ShaderCompiler(ShaderStage.Fragment, BlitPixelShader);
        if (vertexBytecode is null || vertexBytecode.Length == 0 || pixelBytecode is null || pixelBytecode.Length == 0)
        {
            throw new InvalidOperationException("The host HLSL compiler returned empty blit shader bytecode.");
        }

        _blitVertexShader = _renderer.NativeDevice.CreateVertexShader(vertexBytecode);
        _blitPixelShader = _renderer.NativeDevice.CreatePixelShader(pixelBytecode);
        _blitConstantBuffer = _renderer.NativeDevice.CreateBuffer(
            new D3D11BufferDesc
            {
                ByteWidth = 48,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.ConstantBuffer,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
                StructureByteStride = 0,
            });
        _blitRasterizerState = _renderer.NativeDevice.CreateRasterizerState(
            new D3D11RasterizerDesc
            {
                FillMode = D3D11FillMode.Solid,
                CullMode = D3D11CullMode.None,
                FrontCounterClockwise = 0,
                DepthBias = 0,
                DepthBiasClamp = 0,
                SlopeScaledDepthBias = 0,
                DepthClipEnable = 1,
                ScissorEnable = 0,
                MultisampleEnable = 0,
                AntialiasedLineEnable = 0,
            });
    }

    private void EnsureClearResources()
    {
        EnsureBlitResources();
        if (_clearPixelShader != 0)
        {
            return;
        }

        byte[] pixelBytecode = _renderer.Host.ShaderCompiler(ShaderStage.Fragment, ClearPixelShader);
        if (pixelBytecode is null || pixelBytecode.Length == 0)
        {
            throw new InvalidOperationException("The host HLSL compiler returned empty clear shader bytecode.");
        }

        _clearPixelShader = _renderer.NativeDevice.CreatePixelShader(pixelBytecode);
        _clearConstantBuffer = _renderer.NativeDevice.CreateBuffer(
            new D3D11BufferDesc
            {
                ByteWidth = 16,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.ConstantBuffer,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.None,
                StructureByteStride = 0,
            });

        BlendDescriptor opaque = new(
            false,
            new ColorF(0f, 0f, 0f, 0f),
            BlendOp.Add,
            BlendFactor.One,
            BlendFactor.Zero,
            BlendOp.Add,
            BlendFactor.One,
            BlendFactor.Zero);
        for (uint mask = 1; mask < 0xf; mask++)
        {
            D3D11BlendDesc desc = D3D11StateConversion.CreateBlendDesc(new[] { opaque }, new[] { mask });
            _clearBlendStates[(int)mask] = _renderer.NativeDevice.CreateBlendState(in desc);
        }
    }

    private const string BlitVertexShader = @"
cbuffer BlitConstants : register(b13)
{
    float4 SrcRect;
    float4 DstRect;
    float2 SrcSize;
    float2 DstSize;
};
struct VSOutput
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD0;
};
VSOutput VSMain(uint vertexId : SV_VertexID)
{
    static const float2 Positions[6] = {
        float2(0, 0), float2(1, 0), float2(1, 1),
        float2(0, 0), float2(1, 1), float2(0, 1)
    };
    float2 p = Positions[vertexId];
    float2 pixel = DstRect.xy + p * DstRect.zw;
    VSOutput output;
    output.Position = float4(pixel.x / DstSize.x * 2.0 - 1.0,
                             1.0 - pixel.y / DstSize.y * 2.0,
                             0.0, 1.0);
    output.TexCoord = (SrcRect.xy + p * SrcRect.zw) / SrcSize;
    return output;
}
";

    private const string BlitPixelShader = @"
Texture2D BlitTexture : register(t127);
SamplerState BlitSampler : register(s15);
float4 PSMain(float4 position : SV_Position, float2 texCoord : TEXCOORD0) : SV_Target
{
    return BlitTexture.Sample(BlitSampler, texCoord);
}
";

    private const string ClearPixelShader = @"
cbuffer ClearConstants : register(b12)
{
    float4 ClearColor;
};
float4 PSMain() : SV_Target
{
    return ClearColor;
}
";

    private void RecreateBlendState()
    {
        BlendDescriptor[] states = new BlendDescriptor[8];
        for (int index = 0; index < states.Length; index++)
        {
            states[index] = _blendStateSet[index]
                ? _blendStates[index]
                : new BlendDescriptor(false, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendOp.Add, BlendFactor.One, BlendFactor.Zero);
        }

        D3D11BlendDesc desc = D3D11StateConversion.CreateBlendDesc(states, _colorMasks);
        desc.AlphaToCoverageEnable = _alphaToCoverage ? 1 : 0;

        nint state;
        try
        {
            state = _renderer.NativeDevice.CreateBlendState(in desc);
        }
        catch (D3D11Exception exception)
        {
            // Xbox's D3D11 validation is stricter than the desktop driver for
            // blend combinations that are legal in the GAL but have no fixed
            // function equivalent (notably dual-source/advanced combinations
            // emitted by larger titles). Keep the draw stream alive with a
            // valid opaque state instead of aborting the GPU thread. The
            // shader translator still carries the exact operation when it can.
            Console.WriteLine($"[D3D11] blend state rejected; using opaque fallback: {exception.Message}");
            D3D11BlendDesc fallback = CreateOpaqueBlendDesc();
            state = _renderer.NativeDevice.CreateBlendState(in fallback);
        }
        D3D11Com.Release(_blendState);
        _blendState = state;

        ColorF constant = states[0].BlendConstant;
        _renderer.NativeContext.OMSetBlendState(_blendState, constant.Red, constant.Green, constant.Blue, constant.Alpha, uint.MaxValue);
    }

    private D3D11BlendDesc CreateOpaqueBlendDesc()
    {
        D3D11RenderTargetBlendDesc target = new()
        {
            BlendEnable = 0,
            SrcBlend = D3D11Blend.One,
            DestBlend = D3D11Blend.Zero,
            BlendOp = D3D11BlendOp.Add,
            SrcBlendAlpha = D3D11Blend.One,
            DestAlpha = D3D11Blend.Zero,
            BlendOpAlpha = D3D11BlendOp.Add,
            ColorWriteMask = (byte)D3D11ColorWriteEnable.All,
        };

        return new D3D11BlendDesc
        {
            AlphaToCoverageEnable = 0,
            IndependentBlendEnable = 0,
            RenderTarget0 = target,
            RenderTarget1 = target,
            RenderTarget2 = target,
            RenderTarget3 = target,
            RenderTarget4 = target,
            RenderTarget5 = target,
            RenderTarget6 = target,
            RenderTarget7 = target,
        };
    }

    private void RecreateDepthStencilState()
    {
        D3D11DepthStencilDesc desc = new()
        {
            DepthEnable = _depthTestSet && _depthTest.TestEnable ? 1 : 0,
            DepthWriteMask = _depthTestSet && _depthTest.WriteEnable ? D3D11DepthWriteMask.All : D3D11DepthWriteMask.Zero,
            DepthFunc = _depthTestSet ? D3D11FormatConversion.ToComparisonFunc(_depthTest.Func) : D3D11ComparisonFunc.Always,
            StencilEnable = _stencilTestSet && _stencilTest.TestEnable ? 1 : 0,
            StencilReadMask = (byte)(_stencilTestSet ? _stencilTest.FrontFuncMask : 0xff),
            StencilWriteMask = (byte)(_stencilTestSet ? _stencilTest.FrontMask : 0xff),
            FrontFace = _stencilTestSet
                ? D3D11StateConversion.ToStencilOpDesc(_stencilTest.FrontFunc, _stencilTest.FrontSFail, _stencilTest.FrontDpFail, _stencilTest.FrontDpPass)
                : D3D11StateConversion.ToStencilOpDesc(CompareOp.Always, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep),
            BackFace = _stencilTestSet
                ? D3D11StateConversion.ToStencilOpDesc(_stencilTest.BackFunc, _stencilTest.BackSFail, _stencilTest.BackDpFail, _stencilTest.BackDpPass)
                : D3D11StateConversion.ToStencilOpDesc(CompareOp.Always, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep),
        };

        nint state = _renderer.NativeDevice.CreateDepthStencilState(in desc);
        D3D11Com.Release(_depthStencilState);
        _depthStencilState = state;
        uint stencilRef = _stencilTestSet ? (uint)_stencilTest.FrontFuncRef : 0;
        _renderer.NativeContext.OMSetDepthStencilState(_depthStencilState, stencilRef);
    }

    private void RecreateRasterizerState()
    {
        D3D11FillMode fillMode = _frontPolygonMode == PolygonMode.Fill && _backPolygonMode == PolygonMode.Fill
            ? D3D11FillMode.Solid
            : D3D11FillMode.Wireframe;
        D3D11CullMode cullMode = !_cullEnabled ? D3D11CullMode.None : _cullFace switch
        {
            Face.Front => D3D11CullMode.Front,
            Face.Back => D3D11CullMode.Back,
            Face.FrontAndBack => D3D11CullMode.Back,
            _ => D3D11CullMode.None,
        };

        D3D11RasterizerDesc desc = new()
        {
            FillMode = fillMode,
            CullMode = cullMode,
            // GAL front-face state is expressed in the Switch convention. As
            // with Ryujinx's Vulkan backend, flip it for the host's upper-left
            // render-target origin or every culled 3D primitive is reversed.
            FrontCounterClockwise = _frontFace == FrontFace.Clockwise ? 1 : 0,
            DepthBias = _depthBiasEnables == 0 ? 0 : (int)_depthBiasUnits,
            DepthBiasClamp = _depthBiasClamp,
            SlopeScaledDepthBias = _depthBiasFactor,
            DepthClipEnable = _depthClamp ? 0 : 1,
            ScissorEnable = _hasScissors ? 1 : 0,
            MultisampleEnable = _lineWidth > 1f ? 1 : 0,
            AntialiasedLineEnable = _lineSmooth ? 1 : 0,
        };

        nint state;
        try
        {
            state = _renderer.NativeDevice.CreateRasterizerState(in desc);
        }
        catch (D3D11Exception exception)
        {
            int removedReason = _renderer.NativeDevice.GetDeviceRemovedReason();
            throw new InvalidOperationException(
                $"D3D11 CreateRasterizerState failed. deviceRemovedReason=0x{removedReason:X8}, fill={desc.FillMode}, cull={desc.CullMode}, frontCCW={desc.FrontCounterClockwise}",
                exception);
        }
        D3D11Com.Release(_rasterizerState);
        _rasterizerState = state;
        _renderer.NativeContext.RSSetState(_rasterizerState);
    }

    private static HashSet<uint> ExtractUsedInputSemantics(byte[] vertexBytecode)
    {
        HashSet<uint> semantics = new();
        if (vertexBytecode == null || vertexBytecode.Length < 32)
        {
            return semantics;
        }

        try
        {
            if (vertexBytecode[0] != 'D' || vertexBytecode[1] != 'X' || vertexBytecode[2] != 'B' || vertexBytecode[3] != 'C')
            {
                return semantics;
            }

            int chunkCount = BitConverter.ToInt32(vertexBytecode, 0x1C);
            if (chunkCount <= 0 || chunkCount > 64)
            {
                return semantics;
            }

            for (int i = 0; i < chunkCount; i++)
            {
                int chunkOffsetPos = 0x20 + (i * 4);
                if (chunkOffsetPos + 4 > vertexBytecode.Length) break;
                int chunkOffset = BitConverter.ToInt32(vertexBytecode, chunkOffsetPos);
                if (chunkOffset + 8 > vertexBytecode.Length || chunkOffset < 0) continue;

                if (vertexBytecode[chunkOffset] == 'I' &&
                    vertexBytecode[chunkOffset + 1] == 'S' &&
                    vertexBytecode[chunkOffset + 2] == 'G' &&
                    vertexBytecode[chunkOffset + 3] == 'N')
                {
                    int elementCount = BitConverter.ToInt32(vertexBytecode, chunkOffset + 8);
                    int recordStart = chunkOffset + 16;
                    for (int elem = 0; elem < elementCount; elem++)
                    {
                        int entryOffset = recordStart + (elem * 24);
                        if (entryOffset + 24 > vertexBytecode.Length) break;
                        uint semanticIndex = BitConverter.ToUInt32(vertexBytecode, entryOffset + 4);
                        semantics.Add(semanticIndex);
                    }
                    break;
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return semantics;
    }

    private void RecreateInputLayout()
    {
        _half3Slots.Clear();
        if (_program is null || _program.CheckProgramLink(blocking: true) != ProgramLinkStatus.Success || _vertexAttribs.Length == 0)
        {
            return;
        }

        byte[] vertexBytecode = _program.GetShaderBytecode(ShaderStage.Vertex);
        if (vertexBytecode is null)
        {
            return;
        }

        HashSet<uint> usedSemantics = ExtractUsedInputSemantics(vertexBytecode);
        List<D3D11InputElementDesc> elementsList = new();
        List<nint> semanticNamesList = new();
        bool needsDefaultVertexBuffer = false;
        bool needsZeroVertexBuffer = false;
        HashSet<int> occupiedSlots = new() { 30, 31 };
        foreach (VertexAttribDescriptor attribute in _vertexAttribs)
            if (!attribute.IsZero) occupiedSlots.Add(attribute.BufferIndex);

        try
        {
            for (int index = 0; index < _vertexAttribs.Length; index++)
            {
                // If we successfully extracted the vertex shader's input signature, ignore attributes
                // that the shader does not actually consume.
                if (usedSemantics.Count > 0 && !usedSemantics.Contains((uint)index))
                {
                    continue;
                }

                VertexAttribDescriptor attribute = _vertexAttribs[index];
                bool missingBuffer = !attribute.IsZero &&
                    ((uint)attribute.BufferIndex >= (uint)_vertexBuffers.Length ||
                     _vertexBuffers[attribute.BufferIndex].Buffer.Handle == BufferHandle.Null);

                uint inputSlot;
                uint alignedByteOffset;
                VertexBufferDescriptor vertexBuffer = default;
                if (attribute.IsZero)
                {
                    inputSlot = 31;
                    alignedByteOffset = 0;
                    needsDefaultVertexBuffer = true;
                }
                else if (missingBuffer)
                {
                    inputSlot = 30;
                    alignedByteOffset = 0;
                    needsZeroVertexBuffer = true;
                }
                else
                {
                    vertexBuffer = _vertexBuffers[attribute.BufferIndex];
                    inputSlot = (uint)attribute.BufferIndex;
                    alignedByteOffset = (uint)Math.Max(0, attribute.Offset);
                    if (attribute.Format == Format.R16G16B16Float)
                    {
                        int slot = 29;
                        while (slot >= 0 && occupiedSlots.Contains(slot)) slot--;
                        if (slot < 0) throw new NotSupportedException("Half3 vertex conversion has no free IA slot.");
                        occupiedSlots.Add(slot);
                        _half3Slots[index] = slot;
                        inputSlot = (uint)slot;
                        alignedByteOffset = 0;
                        if (!_half3Buffers.ContainsKey(index))
                            _half3Buffers[index] = new(_renderer.NativeDevice, _renderer.NativeContext);
                    }
                }

                nint semName = Marshal.StringToCoTaskMemAnsi("TEXCOORD");
                semanticNamesList.Add(semName);
                elementsList.Add(new D3D11InputElementDesc
                {
                    SemanticName = semName,
                    SemanticIndex = (uint)index,
                    Format = D3D11FormatConversion.ToDxgi(attribute.Format == Format.R16G16B16Float
                        ? Format.R16G16B16A16Float : attribute.Format),
                    InputSlot = inputSlot,
                    AlignedByteOffset = alignedByteOffset,
                    InputSlotClass = inputSlot >= 30 || vertexBuffer.Divisor == 0
                        ? D3D11InputClassification.PerVertexData
                        : D3D11InputClassification.PerInstanceData,
                    InstanceDataStepRate = inputSlot >= 30 || vertexBuffer.Divisor == 0
                        ? 0u
                        : (uint)Math.Max(1, vertexBuffer.Divisor),
                });
            }

            if (elementsList.Count == 0)
            {
                return;
            }

            D3D11InputElementDesc[] elements = elementsList.ToArray();
            BindFallbackVertexBuffers(needsDefaultVertexBuffer, needsZeroVertexBuffer);
            nint layout;
            try
            {
                layout = _renderer.NativeDevice.CreateInputLayout(elements, vertexBytecode);
            }
            catch (D3D11Exception exception)
            {
                int removedReason = _renderer.NativeDevice.GetDeviceRemovedReason();
                throw new InvalidOperationException(
                    $"D3D11 input-layout validation failed. deviceRemovedReason=0x{removedReason:X8}. " + DescribeInputLayout(elements),
                    exception);
            }
            D3D11Com.Release(_inputLayout);
            _inputLayout = layout;
            _renderer.NativeContext.IASetInputLayout(_inputLayout);
        }
        finally
        {
            foreach (nint semanticName in semanticNamesList)
            {
                if (semanticName != 0)
                {
                    Marshal.FreeCoTaskMem(semanticName);
                }
            }
            foreach (int index in new List<int>(_half3Buffers.Keys))
                if (!_half3Slots.ContainsKey(index))
                {
                    _half3Buffers[index].Dispose();
                    _half3Buffers.Remove(index);
                }
        }
    }

    private void BindHalf3VertexBuffers()
    {
        Span<nint> buffers = stackalloc nint[1];
        Span<uint> strides = stackalloc uint[1];
        Span<uint> offsets = stackalloc uint[1];
        offsets[0] = 0;
        foreach (var (index, slot) in _half3Slots)
        {
            VertexAttribDescriptor attribute = _vertexAttribs[index];
            VertexBufferDescriptor descriptor = _vertexBuffers[attribute.BufferIndex];
            D3D11BufferResource source = _renderer.GetBuffer(descriptor.Buffer.Handle);
            int offset = checked(descriptor.Buffer.Offset + attribute.Offset);
            int size = Math.Max(0, descriptor.Buffer.Size - attribute.Offset);
            buffers[0] = _half3Buffers[index].GetBuffer(source, offset, size, descriptor.Stride);
            strides[0] = descriptor.Stride == 0 ? 0u : 8u;
            _renderer.NativeContext.IASetVertexBuffers((uint)slot, buffers, strides, offsets);
        }
    }

    private static string DescribeInputLayout(ReadOnlySpan<D3D11InputElementDesc> elements)
    {
        StringBuilder description = new();
        description.Append("Elements=").Append(elements.Length).Append(':');
        for (int index = 0; index < elements.Length; index++)
        {
            ref readonly D3D11InputElementDesc element = ref elements[index];
            description.Append(' ')
                .Append(index)
                .Append("[sem=").Append(element.SemanticIndex)
                .Append(",fmt=").Append(element.Format)
                .Append(",slot=").Append(element.InputSlot)
                .Append(",offset=").Append(element.AlignedByteOffset)
                .Append(",class=").Append(element.InputSlotClass)
                .Append(",step=").Append(element.InstanceDataStepRate)
                .Append(']');
        }
        return description.ToString();
    }

    private void BindFallbackVertexBuffers(bool bindDefault, bool bindZero)
    {
        if (!bindDefault && !bindZero)
        {
            return;
        }

        EnsureFallbackVertexBuffers();
        Span<nint> buffer = stackalloc nint[1];
        Span<uint> stride = stackalloc uint[1];
        Span<uint> offset = stackalloc uint[1];
        // A disabled/missing guest attribute is a constant zero vector. A
        // zero D3D11 stride repeats the same 16 bytes for every vertex and
        // instance, matching OpenGL's disabled attribute and Vulkan's empty
        // vertex binding. Advancing by 16 bytes made values after the small
        // fallback buffer read out-of-bounds, corrupting large meshes, UI
        // quads and font glyphs.
        stride[0] = 0;
        offset[0] = 0;

        if (bindZero)
        {
            buffer[0] = _zeroVertexBuffer;
            _renderer.NativeContext.IASetVertexBuffers(30, buffer, stride, offset);
        }
        if (bindDefault)
        {
            buffer[0] = _defaultVertexBuffer;
            _renderer.NativeContext.IASetVertexBuffers(31, buffer, stride, offset);
        }
    }

    private void EnsureFallbackVertexBuffers()
    {
        if (_defaultVertexBuffer != 0)
        {
            return;
        }

        D3D11BufferDesc descriptor = new()
        {
            ByteWidth = 256,
            Usage = D3D11Usage.Default,
            BindFlags = D3D11BindFlags.VertexBuffer,
            CpuAccessFlags = D3D11CpuAccessFlags.None,
            MiscFlags = D3D11ResourceMiscFlags.None,
            StructureByteStride = 0,
        };

        _defaultVertexBuffer = _renderer.NativeDevice.CreateBuffer(in descriptor);
        _zeroVertexBuffer = _renderer.NativeDevice.CreateBuffer(in descriptor);

        // IsZero means the complete generic attribute is zero. In particular
        // W must not be forced to one; doing so diverges from the other Ryujinx
        // backends and changes transforms/alpha in shaders that consume the
        // disabled location.
        byte[] defaultValue = new byte[256];
        byte[] zeroValue = new byte[256];
        _renderer.NativeContext.UpdateSubresource(_defaultVertexBuffer, defaultValue);
        _renderer.NativeContext.UpdateSubresource(_zeroVertexBuffer, zeroValue);
    }

    private static nint[][] CreateShaderResourceState()
    {
        nint[][] state = new nint[(int)ShaderStage.Count][];
        for (int stage = 0; stage < state.Length; stage++)
        {
            state[stage] = new nint[128];
        }
        return state;
    }

    private static D3D11Texture[][] CreateShaderResourceTextureState()
    {
        D3D11Texture[][] state = new D3D11Texture[(int)ShaderStage.Count][];
        for (int stage = 0; stage < state.Length; stage++)
        {
            state[stage] = new D3D11Texture[128];
        }
        return state;
    }

    private static int[] CreateShaderResourceHighWaterState()
    {
        int[] state = new int[(int)ShaderStage.Count];
        Array.Fill(state, -1);
        return state;
    }

    private void RememberShaderResource(ShaderStage stage, int slot, nint view)
    {
        if ((uint)stage >= (uint)ShaderStage.Count || (uint)slot >= 128u)
        {
            return;
        }

        _desiredShaderResources[(int)stage][slot] = view;
        _desiredShaderResourceTextures[(int)stage][slot] = null;
        _desiredShaderResourceHighWater[(int)stage] = Math.Max(
            _desiredShaderResourceHighWater[(int)stage],
            slot);
    }

    private void RememberShaderResource(ShaderStage stage, int slot, D3D11Texture texture)
    {
        RememberShaderResource(stage, slot, texture?.ShaderResourceView ?? 0);
        if ((uint)stage < (uint)ShaderStage.Count && (uint)slot < 128u)
        {
            _desiredShaderResourceTextures[(int)stage][slot] = texture;
        }
    }

    private void RebindGraphicsShaderResources()
    {
        RebindShaderResources(ShaderStage.Vertex);
        RebindShaderResources(ShaderStage.Fragment);
        RebindShaderResources(ShaderStage.Geometry);
        RebindShaderResources(ShaderStage.TessellationControl);
        RebindShaderResources(ShaderStage.TessellationEvaluation);
    }

    private void RebindShaderResources(ShaderStage stage)
    {
        // Covers direct/indexed/indirect draws and compute, including writes
        // to already-bound uniforms without a new SetUniformBuffers call.
        Span<nint> uniform = stackalloc nint[1];
        foreach ((int binding, BufferRange range) in _uniformBufferAssignments)
        {
            if (TryGetMappedBinding(stage, 0, binding, out D3D11ResourceBinding mapped) && mapped.Cbv >= 0)
            {
                uniform[0] = _renderer.GetOptionalBuffer(range.Handle)?.GetConstantBuffer(range.Offset, range.Size) ?? 0;
                // A cache eviction can replace a COM buffer still bound to the
                // pipeline. Rebind only on identity change, not on every draw.
                var key = (stage, mapped.Cbv);
                if (!_boundUniformBuffers.TryGetValue(key, out nint old) || old != uniform[0])
                {
                    _renderer.NativeContext.SetConstantBuffers(stage, (uint)mapped.Cbv, uniform);
                    _boundUniformBuffers[key] = uniform[0];
                }
            }
        }
        int highWater = _desiredShaderResourceHighWater[(int)stage];
        if (highWater >= 0)
        {
            D3D11Texture[] textures = _desiredShaderResourceTextures[(int)stage];
            bool hasConflict = false;
            for (int index = 0; index <= highWater; index++)
            {
                if (IsActiveRenderTarget(textures[index]))
                {
                    hasConflict = true;
                    break;
                }
            }

            if (!hasConflict)
            {
                _renderer.NativeContext.SetShaderResources(
                    stage,
                    0,
                    _desiredShaderResources[(int)stage].AsSpan(0, highWater + 1));
            }
            else
            {
                nint[] sanitized = _desiredShaderResources[(int)stage].AsSpan(0, highWater + 1).ToArray();
                for (int index = 0; index <= highWater; index++)
                {
                    if (IsActiveRenderTarget(textures[index]))
                    {
                        // Reuse the snapshot taken when this render pass bound
                        // the target. Refreshing it after every draw turns a
                        // legal feedback loop into a full texture copy per draw.
                        sanitized[index] = textures[index]?.TryGetFeedbackCopyView(refresh: false) ?? 0;
                    }
                }
                _renderer.NativeContext.SetShaderResources(stage, 0, sanitized);
            }
        }
    }

    private bool IsActiveRenderTarget(D3D11Texture texture)
    {
        if (texture is null)
        {
            return false;
        }

        for (int index = 0; index < _colorTargets.Length; index++)
        {
            if (HasSubresourceOverlap(_colorTargets[index], texture))
            {
                return true;
            }
        }

        return HasSubresourceOverlap(_depthStencil, texture);
    }

    private static bool HasSubresourceOverlap(D3D11Texture target, D3D11Texture input)
    {
        if (target is null || input is null || target.Resource == 0 || target.Resource != input.Resource)
        {
            return false;
        }

        int targetLastLayer = target.ViewFirstLayer + Math.Max(1, target.ViewLayerCount);
        int inputLastLayer = input.ViewFirstLayer + Math.Max(1, input.ViewLayerCount);
        int targetLastLevel = target.ViewFirstLevel + Math.Max(1, target.ViewLevelCount);
        int inputLastLevel = input.ViewFirstLevel + Math.Max(1, input.ViewLevelCount);

        bool layersOverlap = target.ViewFirstLayer < inputLastLayer && input.ViewFirstLayer < targetLastLayer;
        bool levelsOverlap = target.ViewFirstLevel < inputLastLevel && input.ViewFirstLevel < targetLastLevel;
        if (layersOverlap && levelsOverlap)
        {
            D3D11DebugTelemetry.TraceRealHazard(target, input);
        }

        return layersOverlap && levelsOverlap;
    }

    private void UnbindRenderTargetShaderResources()
    {
        ShaderStage[] stages =
        {
            ShaderStage.Vertex,
            ShaderStage.Fragment,
            ShaderStage.Geometry,
            ShaderStage.TessellationControl,
            ShaderStage.TessellationEvaluation,
            ShaderStage.Compute,
        };

        bool[] stageHasConflict = new bool[stages.Length];
        bool anyConflict = false;

        // Phase 1: find every stage that aliases an incoming output and clear
        // all native SRVs before writing any feedback texture. Clearing and
        // copying one stage at a time left the same feedback destination bound
        // in another stage; Intel's D3D11 driver eventually dereferenced that
        // illegal state while Mario Wonder entered a level (c0000005).
        for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            ShaderStage stage = stages[stageIndex];
            int highWater = _desiredShaderResourceHighWater[(int)stage];
            if (highWater < 0)
            {
                continue;
            }

            D3D11Texture[] textures = _desiredShaderResourceTextures[(int)stage];
            bool hasConflict = false;
            for (int index = 0; index <= highWater; index++)
            {
                if (IsActiveRenderTarget(textures[index]))
                {
                    hasConflict = true;
                    break;
                }
            }

            if (hasConflict)
            {
                stageHasConflict[stageIndex] = true;
                anyConflict = true;
            }
        }

        if (!anyConflict)
        {
            return;
        }

        for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            ShaderStage stage = stages[stageIndex];
            int highWater = _desiredShaderResourceHighWater[(int)stage];
            if (highWater >= 0)
            {
                // Clear the complete native register file, not just the range
                // used by the current guest program. A previous program may
                // have left a view above its high-water mark, and copying an
                // aliased resource while that stale SRV remains bound is
                // undefined on D3D11 (and crashes older Intel drivers).
                _renderer.NativeContext.SetShaderResources(stage, 0, new nint[128]);
            }
        }

        // Phase 2: with every SRV detached, refresh all required snapshots.
        // Do not bind a snapshot until every CopySubresourceRegion has
        // completed, because another stage may share the same copy resource.
        nint[][] reboundByStage = new nint[stages.Length][];
        for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            ShaderStage stage = stages[stageIndex];
            int highWater = _desiredShaderResourceHighWater[(int)stage];
            if (highWater < 0)
            {
                continue;
            }

            nint[] rebound = _desiredShaderResources[(int)stage].AsSpan(0, highWater + 1).ToArray();
            if (stageHasConflict[stageIndex])
            {
                D3D11Texture[] textures = _desiredShaderResourceTextures[(int)stage];
                for (int index = 0; index <= highWater; index++)
                {
                    if (IsActiveRenderTarget(textures[index]))
                    {
                        // Snapshot the target once, before OMSetRenderTargets
                        // starts writing this render pass. Later draws reuse it.
                        rebound[index] = textures[index]?.TryGetFeedbackCopyView(refresh: true) ?? 0;
                    }
                }
                long sanitizedCount = ++_hazardSanitizedCount;
                if (ShouldTraceFrequentEvent(sanitizedCount))
                {
                    D3D11DebugTelemetry.Append(
                        $"hazard-sanitized: count={sanitizedCount}, stage={stage}, slots={highWater + 1}, " +
                        "render-target-and-srv-alias; feedback-copy-attempted\n");
                }
            }

            reboundByStage[stageIndex] = rebound;
        }

        // Phase 3: restore the complete desired state, substituting immutable
        // snapshots only for the exact overlapping subresources.
        for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            nint[] rebound = reboundByStage[stageIndex];
            if (rebound is not null)
            {
                _renderer.NativeContext.SetShaderResources(stages[stageIndex], 0, rebound);
            }
        }
    }

    private static bool ShouldTraceFrequentEvent(long count)
    {
        return count <= 16 || (count & (count - 1)) == 0 || count % 4096 == 0;
    }

    private void BindDiagnosticPixelShader()
    {
        if (string.IsNullOrEmpty(_diagnosticPixelShaderMode) &&
            !_forceDiagnosticPixelShader && !_forceVertexColorPixelShader)
        {
            return;
        }

        if (_diagnosticPixelShader == 0)
        {
            D3D11DebugTelemetry.TraceDiagnosticPixelShader(
                _diagnosticPixelShaderMode ?? (_forceVertexColorPixelShader ? "vertex" : "magenta"));
            string source = _diagnosticPixelShaderMode switch
            {
                "vertex" => "float4 main(float4 color : TEXCOORD0) : SV_Target0 { return color; }",
                "texture0-uv0" => "Texture2D<float4> t : register(t0); SamplerState s : register(s0); float4 main(float4 uv : TEXCOORD0) : SV_Target0 { return t.Sample(s, uv.xy); }",
                "texture0-alpha-uv0" => "Texture2D<float4> t : register(t0); SamplerState s : register(s0); float4 main(float4 uv : TEXCOORD0) : SV_Target0 { float a = t.Sample(s, uv.xy).a; return float4(a, a, a, 1.0); }",
                "texture0-uv2" => "Texture2D<float4> t : register(t0); SamplerState s : register(s0); float4 main(float4 uv : TEXCOORD2) : SV_Target0 { return t.Sample(s, uv.xy); }",
                "texture1-uv0" => "Texture2D<float4> t : register(t1); SamplerState s : register(s1); float4 main(float4 uv : TEXCOORD0) : SV_Target0 { return t.Sample(s, uv.xy); }",
                "texture1-uv2" => "Texture2D<float4> t : register(t1); SamplerState s : register(s1); float4 main(float4 uv : TEXCOORD2) : SV_Target0 { return t.Sample(s, uv.xy); }",
                "texture2-uv0" => "Texture2D<float4> t : register(t2); SamplerState s : register(s2); float4 main(float4 uv : TEXCOORD0) : SV_Target0 { return t.Sample(s, uv.xy); }",
                "texture2-uv2" => "Texture2D<float4> t : register(t2); SamplerState s : register(s2); float4 main(float4 uv : TEXCOORD2) : SV_Target0 { return t.Sample(s, uv.xy); }",
                _ when _forceVertexColorPixelShader => "float4 main(float4 color : TEXCOORD0) : SV_Target0 { return color; }",
                _ => "float4 main() : SV_Target0 { return float4(1.0, 0.0, 1.0, 1.0); }",
            };
            byte[] bytecode = _renderer.Host.ShaderCompiler?.Invoke(ShaderStage.Fragment, source)
                ?? throw new InvalidOperationException("The host cannot compile the diagnostic D3D11 pixel shader.");
            _diagnosticPixelShader = _renderer.NativeDevice.CreatePixelShader(bytecode);
        }

        _renderer.NativeContext.SetShader(ShaderStage.Fragment, _diagnosticPixelShader);
        if (_diagnosticPixelShaderMode == "magenta-visible")
        {
            // Diagnostic only: prove whether guest geometry reaches the active
            // render target independently of culling, depth, scissor, blending
            // and color-write masks. This mode is never enabled in production.
            EnsureBlitResources();
            _renderer.NativeContext.OMSetBlendState(0, 0f, 0f, 0f, 0f, uint.MaxValue);
            _renderer.NativeContext.OMSetDepthStencilState(0, 0);
            _renderer.NativeContext.RSSetState(_blitRasterizerState);
        }
    }

    private void TraceDrawGeometry(int vertexCount, int firstVertex)
    {
        if (!D3D11DebugTelemetry.Enabled || _program is null || _geometryTraceCounts.Count > 64)
        {
            return;
        }
        _geometryTraceCounts.TryGetValue(_program, out int count);
        if (count >= 2) return;
        _geometryTraceCounts[_program] = count + 1;
        HashSet<uint> used = ExtractUsedInputSemantics(_program.GetShaderBytecode(ShaderStage.Vertex));
        StringBuilder trace = new($"draw-geometry: program={_program.GetHashCode():X}, count={vertexCount}, first={firstVertex}, topology={_primitiveTopology}; ");
        for (int index = 0; index < _vertexAttribs.Length; index++)
        {
            if (!used.Contains((uint)index)) continue;
            VertexAttribDescriptor attribute = _vertexAttribs[index];
            trace.Append($"attrib={index}, format={attribute.Format}, zero={attribute.IsZero}, slot={attribute.BufferIndex}, offset={attribute.Offset}: ");
            if (attribute.IsZero || (uint)attribute.BufferIndex >= (uint)_vertexBuffers.Length) continue;
            VertexBufferDescriptor buffer = _vertexBuffers[attribute.BufferIndex];
            D3D11BufferResource resource = _renderer.GetOptionalBuffer(buffer.Buffer.Handle);
            if (resource is null) continue;
            trace.Append($"stride={buffer.Stride}, base={buffer.Buffer.Offset}, range={buffer.Buffer.Size}; ");
            for (int vertex = 0; vertex < Math.Min(vertexCount, 4); vertex++)
            {
                long offset = (long)buffer.Buffer.Offset + attribute.Offset + (long)(firstVertex + vertex) * buffer.Stride;
                if (offset < 0 || offset > resource.Size - 16) break;
                byte[] bytes = new byte[16];
                resource.CopyData((int)offset, bytes);
                trace.Append($"v{vertex}=[");
                for (int component = 0; component < 4; component++)
                {
                    trace.Append(BitConverter.ToSingle(bytes, component * 4).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
                }
                trace.Append("]; ");
            }
        }
        D3D11DebugTelemetry.Append(trace.Append('\n').ToString());
    }

    private void TraceIndexedDrawGeometry(int indexCount, int firstIndex, int firstVertex)
    {
        if (!D3D11DebugTelemetry.Enabled || _program is null || _indexedGeometryTraceCounts.Count > 64)
        {
            return;
        }

        _indexedGeometryTraceCounts.TryGetValue(_program, out int count);
        if (count >= 2)
        {
            return;
        }

        _indexedGeometryTraceCounts[_program] = count + 1;
        int indexSize = _indexType switch
        {
            IndexType.UByte => sizeof(byte),
            IndexType.UShort => sizeof(ushort),
            IndexType.UInt => sizeof(uint),
            _ => 0,
        };
        StringBuilder trace = new(
            $"draw-indexed-geometry: program={_program.GetHashCode():X}, count={indexCount}, firstIndex={firstIndex}, " +
            $"baseVertex={firstVertex}, topology={_primitiveTopology}, indexType={_indexType}, " +
            $"restart={_primitiveRestart}/0x{_primitiveRestartIndex:X}, cull={_cullEnabled}/{_cullFace}, front={_frontFace}; ");

        if (_indexBufferResource is not null && indexSize != 0 && indexCount > 0)
        {
            int sampleCount = Math.Min(indexCount, 16);
            int sourceOffset = checked(_indexBuffer.Offset + firstIndex * indexSize);
            int byteCount = checked(sampleCount * indexSize);
            if (sourceOffset >= 0 && sourceOffset <= _indexBufferResource.Size - byteCount)
            {
                byte[] bytes = new byte[byteCount];
                _indexBufferResource.CopyData(sourceOffset, bytes);
                trace.Append("indices=[");
                for (int index = 0; index < sampleCount; index++)
                {
                    if (index != 0) trace.Append(',');
                    trace.Append(ReadGuestIndex(bytes, index, indexSize));
                }
                trace.Append("]; ");
            }
        }

        BlendDescriptor blend = _blendStateSet[0]
            ? _blendStates[0]
            : new BlendDescriptor(false, new ColorF(0, 0, 0, 0), BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendOp.Add, BlendFactor.One, BlendFactor.Zero);
        trace.Append(
            $"blend0={blend.Enable}/{blend.ColorSrcFactor},{blend.ColorDstFactor},{blend.ColorOp}/" +
            $"{blend.AlphaSrcFactor},{blend.AlphaDstFactor},{blend.AlphaOp}, mask=0x{_colorMasks[0]:X}; ");

        HashSet<uint> used = ExtractUsedInputSemantics(_program.GetShaderBytecode(ShaderStage.Vertex));
        trace.Append("attributes=[");
        for (int index = 0; index < _vertexAttribs.Length; index++)
        {
            if (used.Count > 0 && !used.Contains((uint)index)) continue;
            VertexAttribDescriptor attribute = _vertexAttribs[index];
            trace.Append($"{index}:{attribute.Format}@s{attribute.BufferIndex}+{attribute.Offset}{(attribute.IsZero ? "(zero)" : string.Empty)},");
        }
        trace.Append("]\n");
        D3D11DebugTelemetry.Append(trace.ToString());
    }

    private void TraceConstantBuffer(
        ShaderStage stage,
        int guestBinding,
        int hostSlot,
        BufferRange range,
        D3D11BufferResource resource)
    {
        if (!D3D11DebugTelemetry.Enabled || resource is null || range.Size < 16)
        {
            return;
        }

        var key = (stage, guestBinding, hostSlot);
        _constantBufferTraceCounts.TryGetValue(key, out int count);
        count++;
        _constantBufferTraceCounts[key] = count;
        if (count != 1 && count != 10 && count != 100 && count % 1000 != 0)
        {
            return;
        }

        int byteCount = Math.Min(64, range.Size);
        byte[] bytes = new byte[byteCount];
        resource.CopyData(range.Offset, bytes);
        StringBuilder values = new();
        for (int offset = 0; offset + 4 <= byteCount; offset += 4)
        {
            if (offset != 0)
            {
                values.Append(',');
            }
            values.Append(BitConverter.ToSingle(bytes, offset).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        D3D11DebugTelemetry.Append(
            $"constant-buffer: stage={stage}, guestBinding={guestBinding}, hostSlot=b{hostSlot}, " +
            $"offset={range.Offset}, size={range.Size}, sample={count}, floats=[{values}]\n");
    }

    private void EnsureUByteIndexBufferCurrent(bool forceBind = false)
    {
        if (_indexType != IndexType.UByte)
        {
            return;
        }

        if (_indexBufferResource is null || _indexBuffer.Size <= 0)
        {
            _renderer.NativeContext.IASetIndexBuffer(0, D3D11Format.R16Uint, 0);
            return;
        }

        long generation = _indexBufferResource.Generation;
        bool requiresUpload = _expandedUByteIndexBuffer == 0 ||
                              _expandedUByteSourceGeneration != generation ||
                              _expandedUByteSourceOffset != _indexBuffer.Offset ||
                              _expandedUByteSourceSize != _indexBuffer.Size ||
                              _expandedUBytePrimitiveRestart != _primitiveRestart ||
                              _expandedUBytePrimitiveRestartIndex != _primitiveRestartIndex;

        if (requiresUpload)
        {
            int sourceSize = _indexBuffer.Size;
            int expandedSize = checked(sourceSize * sizeof(ushort));
            int nativeSize = Math.Max(4, checked((expandedSize + 3) & ~3));

            byte[] source = new byte[sourceSize];
            byte[] expanded = new byte[nativeSize];
            _indexBufferResource.CopyData(_indexBuffer.Offset, source);

            for (int index = 0; index < source.Length; index++)
            {
                // D3D11 has no R8_UINT index-buffer format. After expanding
                // guest UByte indices to R16_UINT, its fixed strip-cut value
                // is 0xffff rather than the guest's 0xff (or custom 8-bit
                // restart index). Preserve primitive boundaries while doing
                // the mandatory widening; otherwise triangle strips join
                // across cuts and render only corrupted shards.
                ushort value = _primitiveRestart && source[index] == unchecked((byte)_primitiveRestartIndex)
                    ? ushort.MaxValue
                    : source[index];
                int destination = index * sizeof(ushort);
                expanded[destination] = (byte)value;
                expanded[destination + 1] = (byte)(value >> 8);
            }

            if (_expandedUByteIndexBuffer == 0 || _expandedUByteIndexCapacity < nativeSize)
            {
                D3D11Com.Release(_expandedUByteIndexBuffer);
                D3D11BufferDesc descriptor = new()
                {
                    ByteWidth = (uint)nativeSize,
                    Usage = D3D11Usage.Default,
                    BindFlags = D3D11BindFlags.IndexBuffer,
                    CpuAccessFlags = D3D11CpuAccessFlags.None,
                    MiscFlags = D3D11ResourceMiscFlags.None,
                    StructureByteStride = 0,
                };
                _expandedUByteIndexBuffer = _renderer.NativeDevice.CreateBuffer(in descriptor);
                _expandedUByteIndexCapacity = nativeSize;
            }

            _renderer.NativeContext.UpdateSubresource(_expandedUByteIndexBuffer, expanded);
            _expandedUByteSourceGeneration = generation;
            _expandedUByteSourceOffset = _indexBuffer.Offset;
            _expandedUByteSourceSize = sourceSize;
            _expandedUBytePrimitiveRestart = _primitiveRestart;
            _expandedUBytePrimitiveRestartIndex = _primitiveRestartIndex;
            _uByteIndexExpansionCount++;
            forceBind = true;
        }

        if (forceBind)
        {
            // D3D11 accepts only 16-bit and 32-bit IA index formats. Switch
            // command streams may use native 8-bit indices, so expand them
            // losslessly and keep firstIndex relative to the requested range.
            _renderer.NativeContext.IASetIndexBuffer(_expandedUByteIndexBuffer, D3D11Format.R16Uint, 0);
        }
    }

    private unsafe uint ReadUInt32(BufferRange range)
    {
        D3D11BufferResource buffer = _renderer.GetBuffer(range.Handle);
        Span<byte> data = stackalloc byte[sizeof(uint)];
        buffer.CopyData(range.Offset, data);
        return MemoryMarshal.Read<uint>(data);
    }

    private bool EnsureProgram(ShaderStage stage)
    {
        if (_program is not null && _program.GetShader(stage) != 0)
        {
            if (stage == ShaderStage.Vertex) BindHalf3VertexBuffers();
            return true;
        }

        int bit = 1 << (int)stage;
        if ((_missingShaderWarningMask & bit) == 0)
        {
            _missingShaderWarningMask |= bit;
            D3D11DebugTelemetry.Append(
                $"draw-skipped-missing-shader: stage={stage}, program={(_program is null ? "none" : "partial")}, " +
                $"hasAny={_program?.HasAnyNativeShader() == true}\n");
        }

        // A shader translation failure must not tear down the guest GPU FIFO.
        // D3D11 cannot execute a draw without its required stage, so skipping
        // that draw is safer than terminating the entire game session. The
        // diagnostic telemetry preserves the original failure for the title's
        // compatibility report.
        return false;
    }

    private bool TryGetMappedBinding(ShaderStage stage, int set, int binding, out D3D11ResourceBinding mapped)
    {
        if (_program?.BindingLayout.TryGet(stage, set, binding, out mapped) == true)
        {
            return true;
        }

        if (_program?.BindingLayout.HasBindings != true)
        {
            mapped = new D3D11ResourceBinding(binding, binding, binding, binding);
            return true;
        }

        mapped = D3D11ResourceBinding.Empty;
        return false;
    }

    private int GetMappedBinding(ShaderStage stage, int set, int binding, Func<D3D11ResourceBinding, int> selector)
    {
        if (TryGetMappedBinding(stage, set, binding, out D3D11ResourceBinding mapped))
        {
            int result = selector(mapped);
            if (result >= 0)
            {
                return result;
            }
        }

        return binding;
    }

}

