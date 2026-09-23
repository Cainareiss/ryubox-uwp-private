using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.D3D11;

/// <summary>
/// Initial native Direct3D 11 renderer boundary for the UWP/Xbox port.
///
/// This class deliberately consumes host-owned COM pointers instead of creating
/// a desktop HWND/swap chain. That keeps device creation and surface ownership
/// inside the UWP/Xbox host, where the correct graphics API contract is known.
/// Resource and pipeline translation is implemented incrementally behind the GAL
/// interfaces in the next stages.
/// </summary>
public sealed class D3D11Renderer : IRenderer
{
    private readonly D3D11HostContext _host;
    private readonly D3D11Pipeline _pipeline;
    private readonly D3D11Window _window;
    private readonly Dictionary<BufferHandle, D3D11BufferResource> _buffers = new();
    private readonly Dictionary<ulong, nint> _syncQueries = new();
    private readonly Queue<D3D11CounterEvent> _counterEvents = new();
    private readonly Dictionary<CounterType, D3D11CounterEvent> _activeCounters = new();
    private readonly Dictionary<CounterType, D3D11CounterEvent.Accumulator> _counterAccumulators = new();
    private readonly object _counterLock = new();
    private volatile bool _disposing;
    internal int PendingCounterCount => _counterEvents.Count;
    private D3D11Device _device;
    private D3D11DeviceContext _context;
    private ulong _nextBufferHandle;
    private ulong _currentSync;
    private Action<Action> _interruptAction;
    private bool _initialized;

    public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

    public bool PreferThreading => false;

    public IPipeline Pipeline => _pipeline;
    public IWindow Window => _window;
    public uint ProgramCount { get; private set; }

    public D3D11HostContext Host => _host;

    internal D3D11Device NativeDevice => _device;
    internal D3D11DeviceContext NativeContext => _context;

    internal D3D11BufferResource GetBuffer(BufferHandle handle)
    {
        return _buffers.TryGetValue(handle, out D3D11BufferResource resource)
            ? resource
            : throw new InvalidOperationException($"Unknown D3D11 buffer handle: {handle}");
    }

    internal D3D11BufferResource GetOptionalBuffer(BufferHandle handle)
    {
        if (handle == BufferHandle.Null)
        {
            return null;
        }

        return _buffers.TryGetValue(handle, out D3D11BufferResource resource) ? resource : null;
    }

    public D3D11Renderer(D3D11HostContext host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pipeline = new D3D11Pipeline(this);
        _window = new D3D11Window(host, _pipeline);
    }

    public static D3D11Renderer Create(D3D11HostContext host)
    {
        return new D3D11Renderer(host);
    }

    public void Initialize(GraphicsDebugLevel logLevel)
    {
        _host.Validate();
        _device = new D3D11Device(_host.Device);
        _context = new D3D11DeviceContext(_host.DeviceContext);
        _initialized = true;
        foreach (CounterType type in Enum.GetValues<CounterType>()) ResetCounter(type);

        Logger.Notice.Print(LogClass.Gpu,
            $"Direct3D 11 host initialized: device=0x{_host.Device.ToInt64():X}, " +
            $"context=0x{_host.DeviceContext.ToInt64():X}, " +
            $"swapChain=0x{_host.SwapChain.ToInt64():X}");
    }

    public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
    {
        EnsureInitialized();

        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        BufferHandle handle = NewBufferHandle();
        _buffers.Add(handle, new D3D11BufferResource(_device, _context, size));
        return handle;
    }

    public unsafe BufferHandle CreateBuffer(nint pointer, int size)
    {
        EnsureInitialized();

        if (pointer == 0)
        {
            throw new ArgumentNullException(nameof(pointer));
        }

        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        BufferHandle handle = NewBufferHandle();
        D3D11BufferResource resource = new(_device, _context, size);
        _buffers.Add(handle, resource);
        resource.SetData(0, new ReadOnlySpan<byte>((void*)pointer, size));
        return handle;
    }

    public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
    {
        EnsureInitialized();

        int size = 0;

        foreach (BufferRange range in storageBuffers)
        {
            size = checked(Math.Max(size, checked(range.Offset + range.Size)));
        }

        return CreateBuffer(size, BufferAccess.DeviceMemory);
    }

    public IImageArray CreateImageArray(int size, bool isBuffer)
    {
        return new D3D11ImageArray(size);
    }

    public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
    {
        EnsureInitialized();
        ProgramCount++;
        return new D3D11Program(
            _device,
            _host.ShaderCompiler,
            _host.SpirvCompiler,
            _host.SpirvCompilerWithLayout,
            shaders,
            info);
    }

    public ISampler CreateSampler(SamplerCreateInfo info)
    {
        EnsureInitialized();
        return new D3D11Sampler(_device, info);
    }

    public ITexture CreateTexture(TextureCreateInfo info)
    {
        EnsureInitialized();

        // A few UWP/Xbox D3D11 drivers expose the multisample render target
        // but do not reliably resolve it through the compatibility readback.
        // Keep this fallback opt-in while validating titles that otherwise
        // present an all-zero framebuffer (such as Alien Hominid). The guest
        // render target is not sampled by the game, so single-sample rendering
        // keeps the image visible at the cost of hardware MSAA quality.
        if (D3D11PerformanceSettings.ForceSingleSample && info.Samples > 1)
        {
            Target target = info.Target switch
            {
                Target.Texture2DMultisample => Target.Texture2D,
                Target.Texture2DMultisampleArray => Target.Texture2DArray,
                _ => info.Target,
            };

            info = new TextureCreateInfo(
                info.Width,
                info.Height,
                info.Depth,
                info.Levels,
                1,
                info.BlockWidth,
                info.BlockHeight,
                info.BytesPerPixel,
                info.Format,
                info.DepthStencilMode,
                target,
                info.SwizzleR,
                info.SwizzleG,
                info.SwizzleB,
                info.SwizzleA);
        }

        D3D11DebugTelemetry.TraceTextureConfiguration("resource", info);
        return new D3D11Texture(_device, _context, info, GetBuffer, _pipeline.TryCopyTextureGpu);
    }

    public ITextureArray CreateTextureArray(int size, bool isBuffer)
    {
        return new D3D11TextureArray(size);
    }

    public bool PrepareHostMapping(nint address, ulong size)
    {
        return false;
    }

    public void CreateSync(ulong id, bool strict)
    {
        EnsureInitialized();
        if (_syncQueries.Remove(id, out nint previousQuery))
        {
            D3D11Com.Release(previousQuery);
        }

        nint query = _device.CreateQuery(D3D11QueryType.Event);
        _context.End(query);
        _syncQueries.Add(id, query);
        _currentSync = Math.Max(_currentSync, id);
    }

    public void DeleteBuffer(BufferHandle buffer)
    {
        if (_buffers.Remove(buffer, out D3D11BufferResource resource))
        {
            resource.Dispose();
        }
    }

    public unsafe PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
    {
        if (!_buffers.TryGetValue(buffer, out D3D11BufferResource resource))
        {
            throw new InvalidOperationException($"Unknown D3D11 buffer handle: {buffer}");
        }

        return resource.GetData(offset, size);
    }

    public Capabilities GetCapabilities()
    {
        return new Capabilities(
            api: TargetApi.Direct3D11,
            vendorName: "Microsoft Direct3D 11",
            memoryType: SystemMemoryType.BackendManaged,
            hasFrontFacingBug: false,
            hasVectorIndexingBug: false,
            needsFragmentOutputSpecialization: false,
            reduceShaderPrecision: false,
            supportsAstcCompression: false,
            supportsBc123Compression: true,
            supportsBc45Compression: true,
            supportsBc67Compression: true,
            supportsEtc2Compression: false,
            supports3DTextureCompression: false,
            supportsBgraFormat: true,
            supportsR4G4Format: false,
            // DXGI_FORMAT_B4G4R4A4_UNORM is not channel-compatible with the
            // guest R4G4B4A4 layout. Force the texture compatibility layer to
            // expand this format to RGBA8 instead of silently swapping red
            // and blue channels.
            supportsR4G4B4A4Format: false,
            supportsScaledVertexFormats: true,
            supportsSnormBufferTextureFormat: true,
            // D3D11's packed 5/6-bit formats only cover BGR ordering. The
            // guest also uses R5G6B5 and related layouts, so use the verified
            // RGBA8 conversion path for correctness.
            supports5BitComponentFormat: false,
            supportsSparseBuffer: false,
            supportsBlendEquationAdvanced: false,
            supportsFragmentShaderInterlock: false,
            supportsFragmentShaderOrderingIntel: false,
            supportsGeometryShader: true,
            supportsGeometryShaderPassthrough: false,
            supportsTransformFeedback: true,
            // The current SM5 path does not implement typed UAV load
            // fallbacks for every Switch image format.
            supportsImageLoadFormatted: false,
            supportsLayerVertexTessellation: false,
            supportsMismatchingViewFormat: false,
            supportsCubemapView: true,
            supportsNonConstantTextureOffset: true,
            supportsQuads: false,
            supportsSeparateSampler: true,
            supportsShaderBallot: false,
            supportsShaderBarrierDivergence: false,
            supportsShaderFloat64: false,
            supportsShaderNonUniformIndexing: false,
            supportsTextureGatherOffsets: true,
            supportsTextureShadowLod: false,
            // UAV binding is currently implemented for pixel and compute
            // stages only; advertising vertex stores produces invalid HLSL.
            supportsVertexStoreAndAtomics: false,
            supportsViewportIndexVertexTessellation: false,
            supportsViewportMask: false,
            supportsViewportSwizzle: false,
            supportsIndirectParameters: true,
            // D3D11 uses a fixed zero-to-one clip depth. The shader translator
            // must materialize minus-one-to-one conversion when requested.
            supportsDepthClipControl: false,
            uniformBufferSetIndex: 0,
            storageBufferSetIndex: 1,
            textureSetIndex: 2,
            imageSetIndex: 3,
            extraSetBaseIndex: 0,
            maximumExtraSets: 0,
            maximumUniformBuffersPerStage: 14,
            maximumStorageBuffersPerStage: 8,
            // D3D11 exposes 128 SRVs but only 16 samplers per stage. GAL's
            // combined texture/sampler accounting must use the lower limit.
            maximumTexturesPerStage: 16,
            maximumImagesPerStage: 8,
            maximumComputeSharedMemorySize: 32768,
            maximumSupportedAnisotropy: 16,
            shaderSubgroupSize: 32,
            storageBufferOffsetAlignment: 16,
            textureBufferOffsetAlignment: 16,
            gatherBiasPrecision: 0,
            maximumGpuMemory: 0);
    }

    public ulong GetCurrentSync()
    {
        return _currentSync;
    }

    public HardwareInfo GetHardwareInfo()
    {
        return new HardwareInfo("Microsoft", "Direct3D 11 host adapter", "UWP/Xbox host");
    }

    public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
    {
        EnsureInitialized();
        ProgramCount++;
        return new D3D11Program(_device, programBinary, hasFragmentShader, info);
    }

    public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
    {
        if (!_buffers.TryGetValue(buffer, out D3D11BufferResource resource))
        {
            throw new InvalidOperationException($"Unknown D3D11 buffer handle: {buffer}");
        }

        resource.SetData(offset, data);
    }

    public void UpdateCounters()
    {
        lock (_counterLock)
        {
            // Ordered completion preserves semaphore writes and accumulation.
            // Polling must never turn a normal GPU command into a busy wait.
            while (_counterEvents.TryPeek(out D3D11CounterEvent counter) && counter.TryPoll())
            {
                _counterEvents.Dequeue();
                counter.ReportResult();
            }
        }
    }

    private void FlushCounters(D3D11CounterEvent target)
    {
        // Threaded GAL can ask from its frontend thread; GetData must still
        // run on the immediate-context owner, not on a concurrent worker.
        if (_interruptAction is not null) _interruptAction(() => FlushCountersOnOwner(target));
        else FlushCountersOnOwner(target);
    }

    private void FlushCountersOnOwner(D3D11CounterEvent target)
    {
        lock (_counterLock)
        {
            SpinWait wait = new();
            while (!target.Reported && _counterEvents.TryPeek(out D3D11CounterEvent counter))
            {
                if (_disposing) throw new OperationCanceledException("D3D11 counter wait canceled by renderer cleanup.");
                if (counter.TryPoll())
                {
                    _counterEvents.Dequeue();
                    counter.ReportResult();
                }
                else wait.SpinOnce();
            }
        }
    }

    public void PreFrame()
    {
        EnsureInitialized();

        if (D3D11DebugTelemetry.Enabled)
        {
            D3D11DebugTelemetry.Append(_device.DrainDebugMessages());
        }

        int reason = _device.GetDeviceRemovedReason();
        if (reason < 0)
        {
            _host.NotifyDeviceLost(reason);
        }
    }

    public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
    {
        EnsureInitialized();
        lock (_counterLock)
        {
            if (!_activeCounters.TryGetValue(type, out D3D11CounterEvent counter))
                throw new ArgumentOutOfRangeException(nameof(type));
            // End at the guest report, not at the next polling call. Otherwise
            // the query counts the wrong draw interval (after its semaphore).
            counter.Complete(resultHandler, divisor);
            _counterEvents.Enqueue(counter);
            _activeCounters[type] = new(_device, _context, type, _counterAccumulators[type], FlushCounters);
            return counter;
        }
    }

    public void ResetCounter(CounterType type)
    {
        EnsureInitialized();
        lock (_counterLock)
        {
            if (_activeCounters.Remove(type, out D3D11CounterEvent old)) old.Release();
            // Pending reports keep their previous epoch. Reset must not erase
            // an earlier semaphore that has not completed on the GPU yet.
            D3D11CounterEvent.Accumulator accumulator = new();
            _counterAccumulators[type] = accumulator;
            _activeCounters[type] = new(_device, _context, type, accumulator, FlushCounters);
        }
    }

    public void WaitSync(ulong id)
    {
        if (!_syncQueries.Remove(id, out nint query))
        {
            return;
        }

        long waitStarted = Stopwatch.GetTimestamp();
        try
        {
            // D3D11_QUERY_EVENT returns a BOOL (4 bytes). Passing a one-byte
            // result buffer makes GetData fail with E_INVALIDARG on some
            // drivers/runtime implementations, aborting the WaitSync path.
            Span<byte> data = stackalloc byte[sizeof(uint)];
            Stopwatch timeout = Stopwatch.StartNew();
            bool slowWaitLogged = false;
            while (true)
            {
                int result;
                try
                {
                    result = _context.GetData(query, data);
                }
                catch (D3D11Exception exception) when ((uint)exception.HResultCode == 0x80070057)
                {
                    D3D11DebugTelemetry.Append(
                        $"sync-getdata-invalidarg: id={id}, query=0x{query.ToInt64():X}, " +
                        $"dataSize={data.Length}, hresult=0x{exception.HResultCode:X8}\n");
                    throw;
                }

                if (result <= 0)
                {
                    break;
                }

                int reason = _device.GetDeviceRemovedReason();
                if (reason < 0)
                {
                    _host.NotifyDeviceLost(reason);
                    throw new D3D11Exception("D3D11 synchronization/device removed", reason);
                }
                if (timeout.Elapsed > TimeSpan.FromSeconds(30))
                {
                    throw new TimeoutException($"D3D11 synchronization {id} did not complete within 30 seconds.");
                }
                if (!slowWaitLogged && timeout.ElapsedMilliseconds >= 10)
                {
                    slowWaitLogged = true;
                    D3D11DebugTelemetry.Append(
                        $"sync-wait-slow: id={id}, query=0x{query.ToInt64():X}, elapsed={timeout.ElapsedMilliseconds}ms, " +
                        $"deviceReason=0x{unchecked((uint)_device.GetDeviceRemovedReason()):X8}\n");
                }
                Thread.Yield();
            }
        }
        finally
        {
            RuntimeGpuTelemetry.RecordQuery(waitStarted);
            double elapsedMs = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
            if (elapsedMs >= 100)
                RuntimeDiagnosticLog.Write("gpu-stalls", $"reason=host-query syncId={id} waitMs={elapsedMs:F2} {RuntimeGpuTelemetry.Snapshot()}");
            D3D11Com.Release(query);
        }
    }

    public void BackgroundContextAction(Action action, bool alwaysBackground = false)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (alwaysBackground && _interruptAction is not null)
        {
            _interruptAction(action);
        }
        else
        {
            action();
        }
    }

    public void SetInterruptAction(Action<Action> interruptAction)
    {
        _interruptAction = interruptAction;
    }

    public void Screenshot()
    {
        // Screenshot capture will be wired to the UWP host readback path.
    }

    public void Dispose()
    {
        _disposing = true;
        lock (_counterLock)
        {
            foreach (D3D11CounterEvent counter in _counterEvents) counter.Release();
            foreach (D3D11CounterEvent counter in _activeCounters.Values) counter.Release();
            _counterEvents.Clear();
            _activeCounters.Clear();
            _counterAccumulators.Clear();
        }
        foreach (D3D11BufferResource resource in _buffers.Values)
        {
            resource.Dispose();
        }

        _buffers.Clear();
        foreach (nint query in _syncQueries.Values)
        {
            D3D11Com.Release(query);
        }
        _syncQueries.Clear();
        _pipeline.Dispose();
        _window.Dispose();
        _initialized = false;
    }

    internal void Present()
    {
        _host.Present();
    }

    private BufferHandle NewBufferHandle()
    {
        ulong value = ++_nextBufferHandle;
        return Unsafe.As<ulong, BufferHandle>(ref value);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("D3D11Renderer.Initialize must be called before creating resources.");
        }
    }

}
