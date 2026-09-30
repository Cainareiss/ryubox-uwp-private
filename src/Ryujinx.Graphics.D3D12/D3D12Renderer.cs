using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.D3D12;

/// <summary>
/// Native GAL adapter. Unsupported texture/state features fail explicitly.
/// The desktop CoreHost smoke route may select it with an owned Win32 swap chain;
/// the UWP SwapChainPanel ownership route remains deliberately separate.
/// </summary>
public sealed unsafe class D3D12Renderer : IRenderer
{
    // RTV/DSV still use fixed per-submission slices. Shader-visible tables
    // use native fence-retired rings; the old limits are kept for A/B tests.
    private const uint ViewCapacity = 16384;
    private const uint SamplerCapacity = 512;
    private readonly bool _descriptorRingEnabled;
    public D3D12NativeDevice Device { get; }
    public IPipeline Pipeline { get; }
    public IWindow Window { get; }
    internal D3D12GpuWatchdog Watchdog { get; }
    public bool PreferThreading => true;
    public uint ProgramCount { get { lock (_programLock) return _disposed ? 0u : (uint)_programs.Count(p => p.Id != 0); } }
    public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;
    private readonly D3D12SpirvCompiler _compiler;
    private readonly object _programLock = new();
    private readonly Dictionary<BufferHandle, D3D12Buffer> _buffers = new();
    private readonly object _bufferLock = new();
    private static long _nextBufferHandle;
    private readonly Dictionary<BufferHandle, int> _bufferSizes = new();
    private readonly D3D12SyncTracker _syncs = new();
    private readonly List<D3D12Program> _programs = new();
    private readonly HashSet<D3D12GalTexture> _textures = new();
    private readonly object _textureLock = new();
    internal int LiveTextureViews { get { lock (_textureLock) return _textures.Count; } }
    private readonly List<D3D12CounterEvent> _events = new();
    private readonly List<D3D12QuerySegment>[] _segments = [new(), new(), new()];
    private readonly ulong[] _counterBase = new ulong[3];
    private readonly ulong[] _active = new ulong[3];
    private readonly List<D3D12QuerySegment> _unsubmitted = new();
    // Legacy desktop Intel D3D12 drivers can access-violate while creating or
    // resolving PIPELINE_STATISTICS query heaps. Queries are telemetry only:
    // they must never be required for a guest draw to execute. The direct-host
    // harness opts into this compatibility path; normal and packaged backends
    // retain real guest counters.
    private static readonly bool DisableGuestCounters =
        (AppContext.TryGetSwitch("Ryujinx.Uwp.D3D12DisableGuestCounters", out bool disableCounters) ? disableCounters : true) &&
        !string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D12_ENABLE_GUEST_COUNTERS"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D12_DISABLE_GUEST_COUNTERS"), "1", StringComparison.Ordinal);
    private bool _commands;
    private volatile bool _disposed;
    private volatile bool _disposing;
    private uint _viewDescriptors, _samplerDescriptors, _targetDescriptors, _depthDescriptors;
    private uint _drawCount;
    private Action<Action> _interrupt;
    private int _gpuThreadId;
    private GraphicsDebugLevel _debug;
    internal D3D12GalTexture LastPresentedTexture { get; set; }
    private D3D12Program _blit;
    private D3D12Program _depthBlit;
    private D3D12Buffer _crop;
    private D3D12Buffer _zeroVertex;
    private BufferHandle _zeroUniformHandle;
    private D3D12Program _maskedClear;
    private D3D12Program _stencilClear;
    private D3D12Buffer _clearColor;
    private D3D12GalTexture _zeroTexture2D;
    private D3D12GalTexture _zeroTextureArray;
    private D3D12GalTexture _zeroTexture3D;
    private D3D12GalTexture _zeroTexture2DMS;
    private D3D12Sampler _zeroSampler;
    internal D3D12Buffer ZeroVertexBuffer
    {
        get
        {
            if (_zeroVertex == null)
            {
                EnsureCommands(); _zeroVertex = Device.CreateBuffer(256); Device.Upload(_zeroVertex, 0, new byte[256]);
            }
            return _zeroVertex;
        }
    }

    internal BufferRange ZeroUniformBuffer
    {
        get
        {
            if (_zeroUniformHandle == BufferHandle.Null)
            {
                _zeroUniformHandle = CreateBuffer(256);
                SetBufferData(_zeroUniformHandle, 0, new byte[256]);
            }
            return new BufferRange(_zeroUniformHandle, 0, 256);
        }
    }

    internal D3D12GalTexture ZeroTexture2D
    {
        get
        {
            if (_zeroTexture2D == null)
            {
                _zeroTexture2D = (D3D12GalTexture)CreateTexture(new TextureCreateInfo(
                    1, 1, 1, 1, 1, 1, 1, 4,
                    Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));
            }
            return _zeroTexture2D;
        }
    }

    internal D3D12GalTexture ZeroTextureArray
    {
        get
        {
            if (_zeroTextureArray == null)
            {
                try
                {
                    _zeroTextureArray = (D3D12GalTexture)CreateTexture(new TextureCreateInfo(
                        1, 1, 6, 1, 1, 1, 1, 4,
                        Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2DArray,
                        SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));
                }
                catch
                {
                    _zeroTextureArray = ZeroTexture2D;
                }
            }
            return _zeroTextureArray;
        }
    }

    internal D3D12GalTexture ZeroTexture3D
    {
        get
        {
            if (_zeroTexture3D == null)
            {
                try
                {
                    _zeroTexture3D = (D3D12GalTexture)CreateTexture(new TextureCreateInfo(
                        1, 1, 1, 1, 1, 1, 1, 4,
                        Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture3D,
                        SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));
                }
                catch
                {
                    _zeroTexture3D = ZeroTexture2D;
                }
            }
            return _zeroTexture3D;
        }
    }

    internal D3D12GalTexture ZeroTexture2DMS
    {
        get
        {
            if (_zeroTexture2DMS == null)
            {
                try
                {
                    _zeroTexture2DMS = (D3D12GalTexture)CreateTexture(new TextureCreateInfo(
                        1, 1, 1, 1, 2, 1, 1, 4,
                        Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2DMultisample,
                        SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));
                }
                catch
                {
                    _zeroTexture2DMS = ZeroTexture2D;
                }
            }
            return _zeroTexture2DMS;
        }
    }

    internal D3D12Sampler ZeroSampler
    {
        get
        {
            if (_zeroSampler == null)
            {
                _zeroSampler = new D3D12Sampler(SamplerCreateInfo.Create(MinFilter.Nearest, MagFilter.Nearest));
            }
            return _zeroSampler;
        }
    }

    internal D3D12GalTexture GetZeroTexture(uint dimension) => dimension switch
    {
        8 => ZeroTexture3D,
        6 or 7 => ZeroTexture2DMS,
        5 or 9 or 10 => ZeroTextureArray,
        _ => ZeroTexture2D,
    };

    public D3D12Renderer(uint width, uint height, string spirvCrossLibrary, nint window = 0, nint panel = 0,
        D3D12DeviceFlags flags = D3D12DeviceFlags.Hardware)
        : this(new D3D12NativeDevice(width, height, window, panel, flags), width, height, spirvCrossLibrary)
    {
    }

    private D3D12Renderer(D3D12NativeDevice device, uint width, uint height, string spirvCrossLibrary)
    {
        Device = device;
        try
        {
            _descriptorRingEnabled = Device.GetDescriptorStats().RingEnabled != 0;
            _compiler = new(spirvCrossLibrary);
        }
        catch { Device.Dispose(); throw; }
        Pipeline = new D3D12Pipeline(this); Window = new D3D12Window(this, width, height);
        Watchdog = new D3D12GpuWatchdog(this);
    }

    public static D3D12Renderer FromFrontendLease(nint context, string spirvCrossLibrary)
    {
        var device = new D3D12NativeDevice(context);
        try
        {
            var info = device.GetInfo();
            return new D3D12Renderer(device, info.Width, info.Height, spirvCrossLibrary);
        }
        catch { device.Dispose(); throw; }
    }
    internal void EnsureCommands()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int thread = Environment.CurrentManagedThreadId;
        if (_gpuThreadId == 0) _gpuThreadId = thread;
        if (_gpuThreadId != thread) throw new InvalidOperationException("D3D12 command recording must run on the GPU owner thread.");
        // Native readback/presentation helpers can submit their own command
        // list. Reconcile with native recording state even when the managed
        // flag still says open; the native BeginCommands export is idempotent.
        Device.StartCommands();
        _commands = true;
    }

    // Call on the GPU owner, before that thread exits. The caller must join
    // the thread before Dispose; this is a handoff, not concurrent access.
    public void CompleteGpuThread()
    {
        if (_disposed) return;
        if (_gpuThreadId != 0 && _gpuThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Only the GPU owner may relinquish command recording.");
        ulong fence = Flush();
        Device.Wait(fence);
        _gpuThreadId = 0;
    }
    internal void BeforeGuestDraw(bool feedback = false)
    {
        EnsureCommands();
        if (DisableGuestCounters) return;
        for (uint i = 0; i < 2; ++i) if (_active[i] == 0) _active[i] = Device.BeginCounter(i);
        if (feedback && _active[2] == 0) _active[2] = Device.BeginCounter(2);
    }
    internal void ReserveDescriptors(D3D12Program program, bool target, bool depth = false)
    {
        uint views = 0, samplers = 0;
        foreach (var binding in program.Bindings)
            if (binding.Type == 3) samplers = checked(samplers + binding.Count); else views = checked(views + binding.Count);
        if (views > ViewCapacity || samplers > SamplerCapacity) throw new NotSupportedException("Shader descriptor footprint exceeds validated native per-draw limits.");
        // Descriptor-table reuse must not turn an arbitrarily long guest
        // workload into a single command list (and eventually a GPU timeout).
        if (_drawCount >= ViewCapacity ||
            !_descriptorRingEnabled && (_viewDescriptors + views > ViewCapacity || _samplerDescriptors + samplers > SamplerCapacity) ||
            target && _targetDescriptors == ViewCapacity || depth && _depthDescriptors == ViewCapacity)
            Flush();
        EnsureCommands();
        if (!_descriptorRingEnabled) { _viewDescriptors += views; _samplerDescriptors += samplers; }
        ++_drawCount;
        if (target) ++_targetDescriptors;
        if (depth) ++_depthDescriptors;
    }
    internal void ReserveClearTarget()
    {
        if (_targetDescriptors == ViewCapacity) Flush();
        EnsureCommands(); ++_targetDescriptors;
    }
    private void EndCounter(int type)
    {
        if (_active[type] == 0) return;
        Device.EndCounter(_active[type]);
        var segment = new D3D12QuerySegment { Id = _active[type] };
        _segments[type].Add(segment); _unsubmitted.Add(segment); _active[type] = 0;
    }
    internal void StopCounters() { EndCounter(0); EndCounter(1); EndCounter(2); }
    internal void EndFeedbackCounter() => EndCounter(2);
    private void Submitted(ulong value)
    {
        _commands = false;
        _viewDescriptors = _samplerDescriptors = _targetDescriptors = _depthDescriptors = 0;
        _drawCount = 0;
        foreach (var segment in _unsubmitted) segment.Fence = value;
        _unsubmitted.Clear();
    }
    internal ulong Flush()
    {
        Watchdog?.NotifyDispatch();
        StopCounters(); ulong value = Device.FlushCommands(); Submitted(value); UpdateCounters(); return value;
    }
    internal void Present(bool vsync)
    {
        Watchdog?.NotifyPresent();
        StopCounters(); EnsureCommands(); ulong value = Device.EndFrame(vsync); Submitted(value); UpdateCounters();
    }
    public void BackgroundContextAction(Action action, bool alwaysBackground = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        // D3D12 PSO creation is serialized by the device wrapper; no GL context binding is required.
        action();
    }
    public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        long serial = System.Threading.Interlocked.Increment(ref _nextBufferHandle);
        if (serial <= 0) throw new InvalidOperationException("D3D12 GAL buffer handle space exhausted.");
        // Round storage for CBVs, while GAL validates the caller's logical ranges.
        var buffer = Device.CreateBuffer(checked(((ulong)size + 255) & ~255UL));
        // Native IDs are device-local and can collide across renderers. GAL
        // handles must never accidentally address a buffer from another device.
        var handle = Unsafe.As<long, BufferHandle>(ref serial);
        lock (_bufferLock) { _buffers.Add(handle, buffer); _bufferSizes.Add(handle, size); }
        return handle;
    }
    public BufferHandle CreateBuffer(nint pointer, int size) => throw new NotSupportedException("D3D12 host-import buffers are not implemented; PrepareHostMapping returns false.");
    public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> ranges) => throw new NotSupportedException("Sparse buffers are not advertised by D3D12.");
    public bool PrepareHostMapping(nint address, ulong size) => false; // Negotiated fallback to GAL-managed uploads.
    internal D3D12Buffer Buffer(BufferHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_bufferLock)
            return _buffers.TryGetValue(handle, out var value) ? value : throw new ArgumentException("Unknown D3D12 buffer.");
    }
    internal D3D12Buffer Buffer(BufferHandle handle, int offset, int size)
    {
        lock (_bufferLock)
        {
            var buffer = Buffer(handle);
            int length = _bufferSizes[handle];
            if (offset < 0 || size < 0 || offset > length || size > length - offset)
                throw new ArgumentOutOfRangeException(nameof(size), "GAL buffer range exceeds its logical allocation.");
            return buffer;
        }
    }
    internal D3D12GalTexture Texture(ITexture texture, bool bound = false)
    {
        if (texture is not D3D12GalTexture result) throw new ArgumentException("Not a D3D12 texture.");
        result.CheckOwner(this, bound); return result;
    }
    public void DeleteBuffer(BufferHandle handle)
    {
        lock (_bufferLock)
        {
            if (!_buffers.Remove(handle, out var buffer)) throw new ArgumentException("Unknown buffer.");
            _bufferSizes.Remove(handle);
            buffer.Dispose();
        }
    }
    public void SetBufferData(BufferHandle handle, int offset, ReadOnlySpan<byte> data)
    {
        var buffer = Buffer(handle, offset, data.Length);
        if (data.IsEmpty) return; // Empty range has no bytes to modify.
        UploadBuffer(buffer, (ulong)offset, data);
    }
    internal void UploadBuffer(D3D12Buffer buffer, ulong offset, ReadOnlySpan<byte> data)
    {
        const int ChunkBytes = 4 * 1024 * 1024;
        for (int copied = 0; copied < data.Length;)
        {
            int length = Math.Min(ChunkBytes, data.Length - copied);
            // Guest memory can fault and invoke GPU synchronization while being
            // read. Snapshot it before entering native code/its context mutex.
            using var snapshot = MemoryOwner<byte>.RentCopy(data.Slice(copied, length));
            EnsureCommands();
            try { Device.Upload(buffer, offset + (ulong)copied, snapshot.Span); }
            catch (COMException error) when (error.HResult == unchecked((int)0x80070718))
            {
                ulong fence = Flush();
                Device.Wait(fence);
                EnsureCommands();
                // One bounded retry, only for pre-copy upload-pool pressure.
                Device.Upload(buffer, offset + (ulong)copied, snapshot.Span);
            }
            copied += length;
        }
    }
    public PinnedSpan<byte> GetBufferData(BufferHandle handle, int offset, int size)
    {
        lock (_bufferLock)
        {
            var buffer = Buffer(handle, offset, size);
            bool background = _gpuThreadId != 0 && _gpuThreadId != Environment.CurrentManagedThreadId;
            if (!background) Flush();
            byte[] result = new byte[size];
            if (size != 0)
            {
                if (background) Device.ReadSubmittedBuffer(buffer, (ulong)offset, result);
                else Device.ReadBuffer(buffer, (ulong)offset, result);
            }
            return Pin(result);
        }
    }
    internal static PinnedSpan<byte> Pin(byte[] bytes)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new((void*)handle.AddrOfPinnedObject(), bytes.Length, () => { if (handle.IsAllocated) handle.Free(); });
    }
    public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
    {
        lock (_programLock)
        {
            ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
            var program = _compiler.CreateProgram(Device, shaders);
            program.UsesGuestSpecialization = true; _programs.Add(program); return program;
        }
    }
    public IProgram LoadProgramBinary(byte[] binary, bool hasFragmentShader, ShaderInfo info)
    {
        lock (_programLock)
        {
            ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
            var program = Device.LoadProgramBinary(binary);
            program.UsesGuestSpecialization = true; _programs.Add(program); return program;
        }
    }
    public ISampler CreateSampler(SamplerCreateInfo info) => new D3D12Sampler(info);
    public ITexture CreateTexture(TextureCreateInfo info)
    { lock (_textureLock) { ObjectDisposedException.ThrowIf(_disposed, this); return TrackTexture(new D3D12GalTexture(this, info)); } }
    private D3D12GalTexture TrackTexture(D3D12GalTexture texture) { _textures.Add(texture); return texture; }
    internal D3D12GalTexture CreateTextureView(D3D12GalTexture source, TextureCreateInfo info, int firstLayer, int firstLevel)
    {
        lock (_textureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return TrackTexture(new D3D12GalTexture(source, info, firstLayer, firstLevel));
        }
    }
    internal void ForgetTexture(D3D12GalTexture texture) { lock (_textureLock) _textures.Remove(texture); }
    public ITextureArray CreateTextureArray(int size, bool isBuffer)
    { if (isBuffer) throw new NotSupportedException("Buffer texture views are not implemented."); return new D3D12TextureArray(size); }
    public IImageArray CreateImageArray(int size, bool isBuffer)
    { if (isBuffer) throw new NotSupportedException("Buffer image views are not implemented."); return new D3D12ImageArray(size); }
    public void CreateSync(ulong id, bool strict) => _syncs.Record(id, Flush(), Device.GetInfo().Completed);
    public ulong GetCurrentSync() => _syncs.Current(Device.GetInfo().Completed);
    public void WaitSync(ulong id)
    {
        ulong fence = _syncs.FenceToWait(id, Device.GetInfo().Completed);
        if (fence != 0)
        {
            Watchdog?.EnterWait(GpuStallReason.FenceWait, fence);
            try
            {
                Device.Wait(fence);
            }
            finally
            {
                Watchdog?.ExitWait();
            }
        }
        _syncs.Current(Device.GetInfo().Completed);
    }
    public HardwareInfo GetHardwareInfo()
    { var info = Device.GetInfo(); return new($"PCI {info.VendorId:X4}", $"D3D12 device {info.DeviceId:X4}", $"Feature level {info.FeatureLevel:X}, binding tier {info.ResourceBindingTier}"); }
    public void Initialize(GraphicsDebugLevel logLevel) { _debug = logLevel; _ = GetHardwareInfo(); }
    public void SetInterruptAction(Action<Action> action) => _interrupt = action ?? throw new ArgumentNullException(nameof(action));
    internal void RunOnGpuThread(Action action) => RunOnGpuThread(action, TimeSpan.FromSeconds(5));
    internal void RunOnGpuThread(Action action, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (_disposed || _disposing) throw new ObjectDisposedException(nameof(D3D12Renderer));
        if (_gpuThreadId == 0 || _gpuThreadId == Environment.CurrentManagedThreadId) { action(); return; }
        if (_interrupt == null) throw new InvalidOperationException("GPU interrupt dispatcher is required for cross-thread query flush.");
        // Pending work may be cancelled. Once execution starts, wait for its actual
        // result: returning early would allow the caller to release resources in use.
        int state = 0; // pending=0, running=1, cancelled=2, finished=3
        var completion = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _interrupt(() =>
            {
                if (System.Threading.Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
                    if (_gpuThreadId != Environment.CurrentManagedThreadId)
                        throw new InvalidOperationException("GPU interrupt dispatcher invoked work on the wrong thread.");
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { System.Threading.Volatile.Write(ref state, 3); }
            });
        }
        catch
        {
            // A dispatcher may enqueue and then throw. Invalidate that closure too.
            if (System.Threading.Interlocked.CompareExchange(ref state, 2, 0) != 0)
                completion.Task.GetAwaiter().GetResult();
            throw;
        }
        bool completed;
        try { completed = completion.Task.Wait(timeout); }
        catch (AggregateException) { completed = true; } // Unwrap the original error below.
        if (!completed &&
            System.Threading.Interlocked.CompareExchange(ref state, 2, 0) == 0)
        {
            completion.TrySetResult(false);
            throw new TimeoutException("D3D12 GPU query dispatcher did not respond; pending work was cancelled.");
        }
        completion.Task.GetAwaiter().GetResult();
    }
    public void PreFrame()
    {
        UpdateCounters();
        lock (_programLock) _programs.RemoveAll(p => p.Id == 0);
        var info = Device.GetInfo();
        // Diagnostic categories deliberately overlap native/texture totals;
        // never sum them to determine process admission. UploadBytes counts
        // staging resources still retained by outstanding GPU submissions.
        RuntimeMemoryBudget.Observe(MemoryCategory.D3D12Resources, checked((long)info.LiveResourceBytes));
        RuntimeMemoryBudget.Observe(MemoryCategory.D3D12Uploads, checked((long)info.UploadBytes));
        if (info.DeviceRemovedReason < 0 || info.DebugErrors != 0)
        {
            string diagnostic = Device.GetDiagnosticMessage();
            throw new InvalidOperationException(
                $"D3D12 device/debug fault: {info.DeviceRemovedReason:X8}, errors={info.DebugErrors}, level={_debug}" +
                (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"; diagnostic={diagnostic}"));
        }
    }
    public void Screenshot()
    {
        // Present closes and submits the command list. A capture requested by
        // CoreHost immediately after Present must read that submitted back
        // buffer directly; a second Flush without recorded commands is an
        // invalid native operation.
        if (_commands)
        {
            Flush();
        }
        var info = Device.GetInfo(); byte[] data = Device.CaptureRgba8();
        // Capture the displayed buffer, including a legitimately black frame.
        // Replacing it with the raw guest texture hides presentation failures
        // and bypasses crop, orientation, aspect ratio and format conversion.
        ScreenCaptured?.Invoke(this, new((int)info.Width, (int)info.Height, false, data, false, false));
    }
    private static int CounterIndex(CounterType type) => type switch
    { CounterType.SamplesPassed => 0, CounterType.PrimitivesGenerated => 1, CounterType.TransformFeedbackPrimitivesWritten => 2, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    public void ResetCounter(CounterType type)
    {
        int index = CounterIndex(type); EndCounter(index);
        // Old segments can still belong to events. Resolve them before dropping our ownership.
        ulong fence = Flush(); Device.Wait(fence);
        foreach (var segment in _segments[index]) Resolve(segment, fence);
        _segments[index].Clear(); _counterBase[index] = 0;
    }
    public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> handler, float divisor, bool hostReserved)
    {
        int index = CounterIndex(type); EndCounter(index);
        var result = new D3D12CounterEvent(this, _counterBase[index], _segments[index], handler, divisor, hostReserved);
        _events.Add(result); return result;
    }
    internal bool Resolve(D3D12QuerySegment segment, ulong completed)
    {
        if (segment.Value.HasValue) return true;
        if (segment.Fence == 0 || segment.Fence > completed) return false;
        segment.Value = Device.ReadCounter(segment.Id); Device.ReleaseCounter(segment.Id); return true;
    }
    public void UpdateCounters()
    {
        ulong completed = Device.GetInfo().Completed;
        foreach (var result in _events.ToArray()) if (result.TryComplete(completed)) _events.Remove(result);
        for (int type = 0; type < _segments.Length; ++type)
            while (_segments[type].Count != 0 && Resolve(_segments[type][0], completed))
            { _counterBase[type] += _segments[type][0].Value.Value; _segments[type].RemoveAt(0); }
    }
    internal void Blit(D3D12GalTexture source, D3D12GalTexture target, Extents2DF src, Extents2DF dst, bool linear,
        D3D12SamplerDescriptor? sampling = null)
    {
        source.CheckOwner(this); target?.CheckOwner(this, bound: true); StopCounters(); EnsureCommands();
        if (dst.X1 == dst.X2 || dst.Y1 == dst.Y2) return;

        bool isDepth = target?.Native.Format.IsDepth == true;

        // Overlapping/self blits must sample a snapshot, never bind one texture
        // simultaneously as RTV/DSV and SRV. The temporary stays alive until fence.
        using var snapshot = target != null && ReferenceEquals(source.Native, target.Native)
            ? Device.CreateTexture((uint)source.Width, (uint)source.Height, source.Native.Format) : null;
        if (snapshot != null) Device.CopyTexture(source.Native, snapshot, 0, 0, 0, 0, (uint)source.Width, (uint)source.Height);

        _crop ??= Device.CreateBuffer(256);
        float[] values = new float[64];
        values[0] = (dst.X1 < dst.X2 ? src.X1 : src.X2) / source.Width;
        values[1] = (dst.Y1 < dst.Y2 ? src.Y1 : src.Y2) / source.Height;
        values[2] = (dst.X1 < dst.X2 ? src.X2 : src.X1) / source.Width;
        values[3] = (dst.Y1 < dst.Y2 ? src.Y2 : src.Y1) / source.Height;
        Device.Upload(_crop, 0, MemoryMarshal.AsBytes(values.AsSpan()));

        bool useLinear = linear && !isDepth;
        using var sampler = new D3D12Sampler(SamplerCreateInfo.Create(useLinear ? MinFilter.Linear : MinFilter.Nearest, useLinear ? MagFilter.Linear : MagFilter.Nearest));

        var draw = D3D12Pipeline.DefaultDraw();
        draw.Count = 3; draw.Instances = 1;
        draw.Viewport = new() { X = Math.Min(dst.X1, dst.X2), Y = Math.Min(dst.Y1, dst.Y2), Width = Math.Abs(dst.X2 - dst.X1), Height = Math.Abs(dst.Y2 - dst.Y1), MaxDepth = 1 };
        draw.Scissor = new() { Left = (int)draw.Viewport.X, Top = (int)draw.Viewport.Y, Right = (int)Math.Max(dst.X1, dst.X2), Bottom = (int)Math.Max(dst.Y1, dst.Y2) };

        if (isDepth)
        {
            _depthBlit ??= Device.CreateProgram(new D3D12ShaderSource(ShaderStage.Vertex, """
                cbuffer Crop : register(b0) { float4 crop; };
                struct V { float4 position:SV_Position; float2 uv:TEXCOORD0; };
                V main(uint id:SV_VertexID) { float2 uv=float2((id<<1)&2,id&2); V v; v.position=float4(uv.x*2-1,1-uv.y*2,0,1); v.uv=lerp(crop.xy,crop.zw,uv); return v; }
                """), new D3D12ShaderSource(ShaderStage.Fragment, """
                Texture2D<float4> image:register(t0); SamplerState sampling:register(s0);
                struct PS_OUT { float depth : SV_Depth; };
                PS_OUT main(float4 position:SV_Position, float2 uv:TEXCOORD0) { PS_OUT o; o.depth = image.Sample(sampling,uv).r; return o; }
                """));

            draw.ColorTarget = 0;
            draw.ColorTargets[0] = 0;
            draw.ColorTargetCount = 0;
            draw.ColorTargetMask = 0u;

            NativeDepthState depthState = new()
            {
                Target = Device.ResourceId(target.Native),
                Enable = 1,
                Write = 1,
                Comparison = 8, // D3D12_COMPARISON_FUNC_ALWAYS
                NoColorTarget = 1
            };

            ReserveDescriptors(_depthBlit, false, depth: true);
            Device.Draw(_depthBlit, [new(ShaderStage.Vertex, 2, 0, 0, _crop, Bytes: 256), new(ShaderStage.Fragment, 0, 0, 0, snapshot ?? source.Native),
                new(ShaderStage.Fragment, 3, 0, 0, null, Sampler: sampling ?? sampler.Descriptor)], ref draw, depthState);
        }
        else
        {
            _blit ??= Device.CreateProgram(new D3D12ShaderSource(ShaderStage.Vertex, """
                cbuffer Crop : register(b0) { float4 crop; };
                struct V { float4 position:SV_Position; float2 uv:TEXCOORD0; };
                V main(uint id:SV_VertexID) { float2 uv=float2((id<<1)&2,id&2); V v; v.position=float4(uv.x*2-1,1-uv.y*2,0,1); v.uv=lerp(crop.xy,crop.zw,uv); return v; }
                """), new D3D12ShaderSource(ShaderStage.Fragment, """
                Texture2D<float4> image:register(t0); SamplerState sampling:register(s0);
                float4 main(float4 position:SV_Position,float2 uv:TEXCOORD0):SV_Target { return image.Sample(sampling,uv); }
                """));

            draw.ColorTarget = target == null ? 0 : Device.ResourceId(target.Native);
            draw.ColorTargets[0] = draw.ColorTarget;
            draw.ColorTargetMask = target == null ? 0u : 1u;

            ReserveDescriptors(_blit, target != null);
            Device.Draw(_blit, [new(ShaderStage.Vertex, 2, 0, 0, _crop, Bytes: 256), new(ShaderStage.Fragment, 0, 0, 0, snapshot ?? source.Native),
                new(ShaderStage.Fragment, 3, 0, 0, null, Sampler: sampling ?? sampler.Descriptor)], ref draw);
        }
    }
    internal void ClearDepth(D3D12GalTexture target, float value)
    {
        target.CheckOwner(this);
        if (!target.Native.Format.IsDepth) throw new ArgumentException("Expected a depth texture.");
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        StopCounters();
        if (_depthDescriptors == ViewCapacity) Flush();
        EnsureCommands(); ++_depthDescriptors;
        Device.ClearDepth(target.Native, Math.Clamp(value, 0, 1));
    }
    internal void ClearStencil(D3D12GalTexture target, int value, int mask)
    {
        target.CheckOwner(this);
        if (target.Info.Format is not (Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.D32FloatS8Uint))
            return; // A depth-only attachment has no stencil bits.
        uint writeMask = (uint)mask & 255;
        if (writeMask == 0) return;
        StopCounters();
        // ClearDepthStencilView cannot preserve a subset of stencil bits.
        // A depth-only utility draw uses REPLACE with the requested write mask.
        _stencilClear ??= Device.CreateProgram(
            new(ShaderStage.Vertex, """
                float4 main(uint id:SV_VertexID):SV_Position {
                    float2 uv=float2((id<<1)&2,id&2); return float4(uv.x*2-1,1-uv.y*2,0,1);
                }
                """), new(ShaderStage.Fragment, "void main() {}"));
        var draw = D3D12Pipeline.DefaultDraw();
        draw.ColorTargetCount = 0;
        draw.Count = 3; draw.Instances = 1;
        draw.Viewport = new() { Width = target.Width, Height = target.Height, MaxDepth = 1 };
        draw.Scissor = new() { Right = target.Width, Bottom = target.Height };
        var depth = new NativeDepthState
        {
            Target = Device.ResourceId(target.Native), Comparison = 8, NoColorTarget = 1,
            StencilEnable = 1,
            FrontFail = 1, FrontPass = 3, FrontDepthFail = 1, FrontFunc = 8,
            BackFail = 1, BackPass = 3, BackDepthFail = 1, BackFunc = 8,
            FrontRef = (uint)value & 255, BackRef = (uint)value & 255,
            FrontReadMask = 255, BackReadMask = 255,
            FrontWriteMask = writeMask, BackWriteMask = writeMask,
        };
        ReserveDescriptors(_stencilClear, false, true);
        Device.Draw(_stencilClear, [], ref draw, depth);
    }
    internal void ClearColorChannels(D3D12GalTexture target, uint mask, ColorF color)
    {
        if ((mask & ~15u) != 0) throw new ArgumentOutOfRangeException(nameof(mask));
        target?.CheckOwner(this, bound: true);
        if (mask == 0) return; // No selected color channels: identity.
        StopCounters();
        if (mask == 15)
        {
            ReserveClearTarget(); Device.Clear(target?.Native, color.Red, color.Green, color.Blue, color.Alpha); return;
        }
        // ClearRenderTargetView has no channel mask. Use a utility draw with
        // blending disabled and an OM write mask; do not change guest state or
        // include utility samples in guest counters.
        _maskedClear ??= Device.CreateProgram(
            new(ShaderStage.Vertex, """
                float4 main(uint id:SV_VertexID):SV_Position {
                    float2 uv=float2((id<<1)&2,id&2); return float4(uv.x*2-1,1-uv.y*2,0,1);
                }
                """), new(ShaderStage.Fragment, """
                cbuffer ClearColor:register(b0) { float4 color; };
                float4 main():SV_Target { return color; }
                """));
        EnsureCommands(); _clearColor ??= Device.CreateBuffer(256);
        float[] values = new float[64];
        values[0] = color.Red; values[1] = color.Green; values[2] = color.Blue; values[3] = color.Alpha;
        Device.Upload(_clearColor, 0, MemoryMarshal.AsBytes(values.AsSpan()));
        var info = Device.GetInfo();
        uint width = target == null ? info.Width : (uint)target.Width;
        uint height = target == null ? info.Height : (uint)target.Height;
        var draw = D3D12Pipeline.DefaultDraw();
        draw.ColorTarget = target == null ? 0 : Device.ResourceId(target.Native); draw.ColorTargets[0] = draw.ColorTarget;
        draw.ColorTargetMask = target == null ? 0u : 1u;
        draw.Count = 3; draw.Instances = 1; draw.Blend.Targets[0].Mask = (byte)mask;
        draw.Viewport = new() { Width = width, Height = height, MaxDepth = 1 };
        draw.Scissor = new() { Right = checked((int)width), Bottom = checked((int)height) };
        ReserveDescriptors(_maskedClear, target != null);
        Device.Draw(_maskedClear, [new(ShaderStage.Fragment, 2, 0, 0, _clearColor, Bytes: 256)], ref draw);
    }
    public Capabilities GetCapabilities() => new(
        api: TargetApi.Direct3D12, vendorName: "Native D3D12", memoryType: SystemMemoryType.BackendManaged,
        hasFrontFacingBug: false, hasVectorIndexingBug: false, needsFragmentOutputSpecialization: false, reduceShaderPrecision: false,
        supportsAstcCompression: false, supportsBc123Compression: false, supportsBc45Compression: false, supportsBc67Compression: false,
        supportsEtc2Compression: false, supports3DTextureCompression: false, supportsBgraFormat: false, supportsR4G4Format: false,
        supportsR4G4B4A4Format: false, supportsScaledVertexFormats: false, supportsSnormBufferTextureFormat: false,
        supports5BitComponentFormat: false, supportsSparseBuffer: false, supportsBlendEquationAdvanced: false,
        supportsFragmentShaderInterlock: false, supportsFragmentShaderOrderingIntel: false, supportsGeometryShader: true,
        supportsGeometryShaderPassthrough: false, supportsTransformFeedback: true, supportsImageLoadFormatted: false,
        supportsLayerVertexTessellation: false, supportsMismatchingViewFormat: false, supportsCubemapView: false,
        supportsNonConstantTextureOffset: false, supportsQuads: false, supportsSeparateSampler: true, supportsShaderBallot: false,
        supportsShaderBarrierDivergence: false, supportsShaderFloat64: false, supportsShaderNonUniformIndexing: false,
        supportsTextureGatherOffsets: false, supportsTextureShadowLod: false, supportsVertexStoreAndAtomics: true,
        supportsViewportIndexVertexTessellation: false, supportsViewportMask: false, supportsViewportSwizzle: false,
        supportsIndirectParameters: true, supportsDepthClipControl: false,
        uniformBufferSetIndex: 0, storageBufferSetIndex: 1, textureSetIndex: 2, imageSetIndex: 3, extraSetBaseIndex: 4, maximumExtraSets: 0,
        maximumUniformBuffersPerStage: 14, maximumStorageBuffersPerStage: 8, maximumTexturesPerStage: 16, maximumImagesPerStage: 8,
        maximumComputeSharedMemorySize: 32768, maximumSupportedAnisotropy: 16, shaderSubgroupSize: 32,
        storageBufferOffsetAlignment: 16, textureBufferOffsetAlignment: 16, gatherBiasPrecision: 0, maximumGpuMemory: 512UL * 1024 * 1024);
    public void Dispose()
    {
        if (_disposed) return;
        lock (_programLock)
        {
            if (_disposed || _disposing) return; // Counter callbacks can re-enter cleanup.
            _disposing = true;
        }
        var failures = new List<Exception>();
        void Release(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
        bool ownsGpuCommands = _gpuThreadId == 0 || _gpuThreadId == Environment.CurrentManagedThreadId;

        // CoreHost joins its graphics thread before DisposeGpu. If a fatal
        // error ended that thread while a command list was open, submitting it
        // from the stop thread violates the command-list ownership contract.
        // Destroying the native context first deliberately abandons that
        // unsubmitted list; its resources were never submitted to the GPU and
        // the SafeHandle makes the following managed cleanup idempotent.
        if (!ownsGpuCommands)
        {
            _commands = false;
            _unsubmitted.Clear();
            Release(Device.Dispose);
        }

        // A failed flush/counter callback must not prevent independent resources
        // and the native device from being released. Do not hide the failures.
        if (ownsGpuCommands)
        {
            Release(() => { ulong fence = Flush(); Device.Wait(fence); UpdateCounters(); });
        }
        lock (_programLock)
        {
            _disposed = true;
            foreach (var program in _programs) Release(program.Dispose);
            _programs.Clear();
            Release(_compiler.Dispose);
        }
        foreach (var result in _events) Release(result.Dispose);
        _events.Clear();
        lock (_textureLock)
        {
            foreach (var texture in _textures.ToArray()) Release(texture.ForceRelease);
            _textures.Clear();
        }
        foreach (var buffer in _buffers.Values) Release(buffer.Dispose);
        _buffers.Clear(); _bufferSizes.Clear(); _syncs.Clear();
        Release(((D3D12Pipeline)Pipeline).DisposeResources);
        Release(() => _blit?.Dispose()); Release(() => _depthBlit?.Dispose()); Release(() => _crop?.Dispose()); Release(() => _zeroVertex?.Dispose());
        Release(() => _maskedClear?.Dispose()); Release(() => _clearColor?.Dispose());
        Release(() => _stencilClear?.Dispose());
        Release(() => _zeroSampler?.Dispose());
        _zeroTexture2D = null; _zeroTextureArray = null; _zeroTexture3D = null; _zeroTexture2DMS = null; _zeroSampler = null;
        Release(() => Watchdog?.Dispose());
        if (ownsGpuCommands) Release(Device.Dispose);
        foreach (var segments in _segments) segments.Clear();
        _unsubmitted.Clear();
        if (failures.Count != 0) throw new AggregateException("D3D12 cleanup completed with failures.", failures);
    }
}
