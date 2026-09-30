using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.D3D12;

[Flags]
public enum D3D12DeviceFlags : uint
{
    Hardware = 0,
    DebugLayer = 1,
    Warp = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D12DeviceInfo
{
    public uint Size;
    public uint AbiVersion;
    public uint VendorId;
    public uint DeviceId;
    public uint FeatureLevel;
    public uint ResourceBindingTier;
    public uint Width;
    public uint Height;
    public ulong Submitted;
    public ulong Completed;
    public ulong LiveResourceBytes;
    public ulong UploadBytes;
    public ulong CpuFenceWaits;
    public int DeviceRemovedReason;
    public uint DebugErrors;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D12DescriptorStats
{
    public uint Size, Version;
    public ulong RingEnabled, ViewCapacity, SamplerCapacity;
    public ulong ViewAllocations, SamplerAllocations;
    public ulong ViewPeakPerSubmission, SamplerPeakPerSubmission;
    public ulong ViewOccupied, SamplerOccupied, ViewPeakOccupied, SamplerPeakOccupied;
    public ulong ViewPages, SamplerPages, PageCreations, HeapSwitches, Wraps, Reclaimed;
    public ulong PressureEvents, AllocationFailures, RingWaits, RingWaitMicroseconds;
    public ulong CpuFenceWaits, CpuFenceWaitMicroseconds, MaxFenceWaitMicroseconds;
    public ulong EstimatedHeapBytes;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D12PerformanceStats
{
    public uint Size, Version;
    public ulong DrawCalls, RenderPasses, ShaderCacheHits, ShaderCacheMisses;
    public ulong ShaderCompileMicroseconds, PsoCacheHits, PsoCreations;
    public ulong PsoMicroseconds, DescriptorBindMicroseconds, RecordMicroseconds;
    public ulong SubmitMicroseconds, PresentMicroseconds, FenceWaitMicroseconds;
    public ulong DepthAttachmentsDetached, InvalidRenderPasses, UavBarriers;
    public ulong DescriptorTableCacheHits, DescriptorTableCacheMisses, DescriptorCopies;
    public ulong PresentAttempts, PresentSuccess;
    public ulong LastPresentFence, LastPresentQpc;
    public int LastPresentResult;
    public uint LastPresentWidth, LastPresentHeight;
    public uint PresentationTarget, Reserved;
}

/// <summary>
/// Native D3D12 command/resource boundary for the future GAL renderer. This
/// class is deliberately not an IRenderer: it cannot execute Switch draws yet.
/// SafeHandle protects each native call against concurrent device disposal.
/// Begin/End must be issued by one logical GPU owner; uploads are copied before
/// return and deletion of resource IDs is deferred until submitted fences.
/// </summary>
public sealed unsafe partial class D3D12NativeDevice : IDisposable
{
    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12_GetDescriptorStats", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetDescriptorStatsNative(DeviceHandle context, ref D3D12DescriptorStats stats);

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12_GetPerformanceStats", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetPerformanceStatsNative(DeviceHandle context, ref D3D12PerformanceStats stats);

    public D3D12DescriptorStats GetDescriptorStats()
    {
        var stats = new D3D12DescriptorStats { Size = (uint)sizeof(D3D12DescriptorStats) };
        Check(GetDescriptorStatsNative(_handle, ref stats));
        if (stats.Version != 1) throw new NotSupportedException($"Descriptor statistics ABI {stats.Version} is not supported.");
        return stats;
    }

    public D3D12PerformanceStats GetPerformanceStats()
    {
        var stats = new D3D12PerformanceStats { Size = (uint)sizeof(D3D12PerformanceStats) };
        Check(GetPerformanceStatsNative(_handle, ref stats));
        if (stats.Version != 3) throw new NotSupportedException($"Performance statistics ABI {stats.Version} is not supported.");
        return stats;
    }
    public string GetDiagnosticMessage()
    {
        if (_handle.IsClosed) return string.Empty;
        byte[] data = new byte[16384];
        fixed (byte* p = data)
        {
            if (Native.GetLastDiagnostic(_handle, p, (uint)data.Length) >= 0)
            {
                int end = Array.IndexOf(data, (byte)0);
                string nativeDiag = System.Text.Encoding.UTF8.GetString(data, 0, end < 0 ? data.Length : end);
                if (!string.IsNullOrWhiteSpace(nativeDiag))
                {
                    return nativeDiag;
                }
            }
            if (ProgramNative.Error(_handle, p, (uint)data.Length) >= 0)
            {
                int end = Array.IndexOf(data, (byte)0);
                return System.Text.Encoding.UTF8.GetString(data, 0, end < 0 ? data.Length : end);
            }
        }
        return string.Empty;
    }
    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12_GetResourceStats", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetResourceStatsNative(DeviceHandle context, ulong* data, uint count);

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12_TrimIdleMemory", CallingConvention = CallingConvention.StdCall)]
    private static extern int TrimIdleMemoryNative(DeviceHandle context, out ulong releasedBytes);

    /// <summary>Thread-safe (native context mutex). Returns released upload bytes.</summary>
    public ulong TrimIdleMemory()
    {
        Check(TrimIdleMemoryNative(_handle, out ulong released));
        return released;
    }

    public ulong[] GetResourceStats()
    {
        var values = new ulong[9];
        fixed (ulong* p = values) Check(GetResourceStatsNative(_handle, p, (uint)values.Length));
        return values;
    }
    private readonly DeviceHandle _handle;

    // The frontend retains ownership of its context for the entire session.
    // Disposing this wrapper must not destroy its swapchain or menu resources.
    internal D3D12NativeDevice(nint borrowedContext)
    {
        if (borrowedContext == 0) throw new ArgumentException("A leased D3D12 context is required.", nameof(borrowedContext));
        _handle = new DeviceHandle(borrowedContext, false);
    }

    public D3D12NativeDevice(uint width, uint height, nint window = 0, nint swapChainPanel = 0,
        D3D12DeviceFlags flags = D3D12DeviceFlags.Hardware)
    {
        CheckStatic(Native.Create(window, swapChainPanel, width, height, (uint)flags, out nint pointer));
        _handle = new DeviceHandle(pointer);
    }

    public D3D12DeviceInfo GetInfo()
    {
        var info = new D3D12DeviceInfo { Size = (uint)sizeof(D3D12DeviceInfo) };
        Check(Native.GetInfo(_handle, ref info));
        if (info.AbiVersion != 1) throw new NotSupportedException($"D3D12 ABI {info.AbiVersion} is not supported.");
        return info;
    }

    public void BeginFrame(float red = 0, float green = 0, float blue = 0, float alpha = 1) =>
        Check(Native.BeginFrame(_handle, red, green, blue, alpha));

    public ulong EndFrame(bool vsync)
    {
        Check(Native.EndFrame(_handle, vsync ? 1u : 0u, out ulong fence));
        return fence;
    }

    public void Wait(ulong fence) => Check(Native.Wait(_handle, fence));
    public void Resize(uint width, uint height) => Check(Native.Resize(_handle, width, height));
    public void SetCompositionScale(float x, float y) => Check(Native.SetCompositionScale(_handle, x, y));

    public D3D12Buffer CreateBuffer(ulong bytes)
    {
        Check(Native.CreateBuffer(_handle, bytes, out ulong id));
        return new D3D12Buffer(this, id, bytes);
    }

    internal D3D12Buffer RetainBuffer(D3D12Buffer source)
    {
        Check(Native.RetainBuffer(_handle, Validate(source), out ulong id));
        return new D3D12Buffer(this, id, source.Size);
    }

    public D3D12Texture CreateTextureRgba8(uint width, uint height)
    {
        Check(Native.CreateTextureRgba8(_handle, width, height, out ulong id));
        return new D3D12Texture(this, id, width, height);
    }

    internal D3D12Texture CreateTexture(uint width, uint height, uint layers, uint levels, D3D12TextureFormat format, uint samples = 1)
    {
        ulong id;
        Check(format.IsDepth ? Native.CreateDepthTextureArray(_handle, width, height, layers, levels, format.Dxgi, samples, out id) :
            Native.CreateColorTextureArray(_handle, width, height, layers, levels, format.Dxgi, samples, out id));
        return new D3D12Texture(this, id, width, height, layers, levels, false, format);
    }

    internal D3D12Texture CreateTexture(uint width, uint height, D3D12TextureFormat format, uint samples = 1)
        => CreateTexture(width, height, 1, 1, format, samples);

    internal D3D12Texture CreateTexture3D(uint width, uint height, uint depth, uint levels, D3D12TextureFormat format)
    {
        Check(Native.CreateColorTexture3D(_handle, width, height, depth, levels, format.Dxgi, out ulong id));
        return new D3D12Texture(this, id, width, height, depth, levels, true, format);
    }

    internal D3D12Texture CreateTextureView(D3D12Texture source, uint firstMip, uint mipCount,
        uint firstSlice, uint sliceCount, uint width, uint height, uint depth, D3D12TextureFormat? viewFormat = null)
    {
        D3D12TextureFormat format = viewFormat ?? source.Format;
        if (D3D12Configuration.TypelessViews && viewFormat.HasValue)
        {
            Check(Native.CreateTextureViewWithFormat(_handle, Validate(source), firstMip, mipCount, firstSlice, sliceCount, format.Dxgi, out ulong id));
            return new D3D12Texture(this, id, width, height, depth, mipCount, source.Is3D, format);
        }
        else
        {
            Check(Native.CreateTextureView(_handle, Validate(source), firstMip, mipCount, firstSlice, sliceCount, out ulong id));
            return new D3D12Texture(this, id, width, height, depth, mipCount, source.Is3D, format);
        }
    }

    public D3D12ComputePipeline CreateComputePipeline(string hlsl)
    {
        ArgumentNullException.ThrowIfNull(hlsl);
        byte[] source = System.Text.Encoding.UTF8.GetBytes(hlsl);
        fixed (byte* pointer = source)
        {
            Check(Native.CreateComputePipeline(_handle, pointer, (ulong)source.Length, out ulong id));
            return new D3D12ComputePipeline(this, id);
        }
    }

    public void Dispatch(D3D12ComputePipeline pipeline, D3D12Buffer source, D3D12Buffer destination,
        ReadOnlySpan<uint> constants, uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        ulong id = Validate(pipeline), from = Validate(source), to = Validate(destination);
        fixed (uint* pointer = constants)
            Check(Native.Dispatch(_handle, id, from, to, pointer, (uint)constants.Length, groupsX, groupsY, groupsZ));
    }

    public void Upload(D3D12Buffer buffer, ulong offset, ReadOnlySpan<byte> data)
    {
        ulong id = Validate(buffer);
        fixed (byte* pointer = data) Check(Native.UploadBuffer(_handle, id, offset, pointer, (ulong)data.Length));
    }

    public void Upload(D3D12Texture texture, ReadOnlySpan<byte> rgba, uint rowPitch)
    {
        ulong id = Validate(texture);
        fixed (byte* pointer = rgba) Check(Native.UploadTextureRgba8(_handle, id, pointer, (ulong)rgba.Length, rowPitch));
    }

    internal void Upload(D3D12Texture texture, uint mip, uint x, uint y, uint z, uint width, uint height,
        uint depth, ReadOnlySpan<byte> data, uint rowPitch, uint depthPitch)
    {
        ulong id = Validate(texture);
        fixed (byte* pointer = data)
            Check(Native.UploadTextureRegion(_handle, id, mip, x, y, z, width, height, depth,
                pointer, (ulong)data.Length, rowPitch, depthPitch));
    }

    public void Copy(D3D12Buffer source, D3D12Buffer destination, ulong sourceOffset, ulong destinationOffset, ulong bytes) =>
        Check(Native.CopyBuffer(_handle, Validate(source), Validate(destination), sourceOffset, destinationOffset, bytes));

    public void DrawTexture(D3D12Texture texture, bool flipY = false,
        float left = 0, float top = 0, float right = 1, float bottom = 1) =>
        Check(Native.DrawTexture(_handle, Validate(texture), left, top, right, bottom, flipY ? 1u : 0u));

    public void ReadBuffer(D3D12Buffer buffer, ulong offset, Span<byte> destination)
    {
        ulong id = Validate(buffer);
        fixed (byte* pointer = destination) Check(Native.ReadBuffer(_handle, id, offset, pointer, (ulong)destination.Length));
    }

    public void ReadSubmittedBuffer(D3D12Buffer buffer, ulong offset, Span<byte> destination)
    {
        ulong id = Validate(buffer);
        fixed (byte* pointer = destination) Check(Native.ReadSubmittedBuffer(_handle, id, offset, pointer, (ulong)destination.Length));
    }

    /// <summary>Explicit, synchronous readback for tests/screenshots only.</summary>
    public byte[] CaptureRgba8()
    {
        D3D12DeviceInfo info = GetInfo();
        byte[] bytes = new byte[checked((int)(info.Width * info.Height * 4))];
        fixed (byte* pointer = bytes) Check(Native.Capture(_handle, pointer, (ulong)bytes.Length));
        return bytes;
    }

    private ulong Validate(D3D12Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!ReferenceEquals(resource.Device, this)) throw new ArgumentException("Resource belongs to another D3D12 device.");
        ObjectDisposedException.ThrowIf(resource.Id == 0, resource);
        return resource.Id;
    }

    internal void Release(ulong id)
    {
        // Destroying the device releases all native resources, so resources may
        // be disposed after their owner without calling a freed native context.
        if (!_handle.IsClosed)
        {
            try { Check(Native.ReleaseResource(_handle, id)); }
            catch (ObjectDisposedException) when (_handle.IsClosed) { }
        }
    }

    internal void ReleasePipeline(ulong id)
    {
        if (!_handle.IsClosed)
        {
            try { Check(Native.ReleaseComputePipeline(_handle, id)); }
            catch (ObjectDisposedException) when (_handle.IsClosed) { }
        }
    }

    public void Dispose() => _handle.Dispose();

    private void Check(int result)
    {
        if (result < 0)
        {
            string diag = GetDiagnosticMessage();
            if (!string.IsNullOrEmpty(diag))
            {
                throw new COMException($"D3D12 native error 0x{(uint)result:X8}: {diag}", result);
            }
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void CheckStatic(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private sealed class DeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceHandle(nint pointer, bool ownsHandle = true) : base(ownsHandle) => SetHandle(pointer);
        protected override bool ReleaseHandle() => Native.Destroy(handle) >= 0;
    }

    private static class Native
    {
        private const string Library = "Ryujinx.Uwp.Native.dll";
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Create", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Create(nint window, nint panel, uint width, uint height, uint flags, out nint context);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Destroy", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Destroy(nint context);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_GetInfo", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetInfo(DeviceHandle context, ref D3D12DeviceInfo info);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_BeginFrame", CallingConvention = CallingConvention.StdCall)]
        internal static extern int BeginFrame(DeviceHandle context, float r, float g, float b, float a);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_EndFrame", CallingConvention = CallingConvention.StdCall)]
        internal static extern int EndFrame(DeviceHandle context, uint interval, out ulong fence);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Wait", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Wait(DeviceHandle context, ulong fence);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Resize", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Resize(DeviceHandle context, uint width, uint height);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_SetCompositionScale", CallingConvention = CallingConvention.StdCall)]
        internal static extern int SetCompositionScale(DeviceHandle context, float x, float y);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateBuffer(DeviceHandle context, ulong bytes, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_RetainBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern int RetainBuffer(DeviceHandle context, ulong source, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateTextureRgba8", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateTextureRgba8(DeviceHandle context, uint width, uint height, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateColorTexture", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateColorTexture(DeviceHandle context, uint width, uint height, uint format, uint samples, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateColorTextureArray", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateColorTextureArray(DeviceHandle context, uint width, uint height, uint layers, uint levels, uint format, uint samples, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateColorTexture3D", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateColorTexture3D(DeviceHandle context, uint width, uint height, uint depth, uint levels, uint format, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateTextureView", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateTextureView(DeviceHandle context, ulong source, uint firstMip, uint mipCount, uint firstSlice, uint sliceCount, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateTextureViewWithFormat", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateTextureViewWithFormat(DeviceHandle context, ulong source, uint firstMip, uint mipCount, uint firstSlice, uint sliceCount, uint format, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateDepthTexture", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateDepthTexture(DeviceHandle context, uint width, uint height, uint format, uint samples, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateDepthTextureArray", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateDepthTextureArray(DeviceHandle context, uint width, uint height, uint layers, uint levels, uint format, uint samples, out ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReleaseResource", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReleaseResource(DeviceHandle context, ulong resource);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateComputePipeline", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateComputePipeline(DeviceHandle context, byte* source, ulong bytes, out ulong pipeline);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReleaseComputePipeline", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReleaseComputePipeline(DeviceHandle context, ulong pipeline);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Dispatch", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Dispatch(DeviceHandle context, ulong pipeline, ulong source, ulong destination,
            uint* constants, uint count, uint x, uint y, uint z);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_UploadBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern int UploadBuffer(DeviceHandle context, ulong resource, ulong offset, void* data, ulong bytes);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CopyBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CopyBuffer(DeviceHandle context, ulong source, ulong destination, ulong from, ulong to, ulong bytes);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_UploadTextureRgba8", CallingConvention = CallingConvention.StdCall)]
        internal static extern int UploadTextureRgba8(DeviceHandle context, ulong resource, void* data, ulong bytes, uint pitch);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_UploadTextureRegion", CallingConvention = CallingConvention.StdCall)]
        internal static extern int UploadTextureRegion(DeviceHandle context, ulong resource, uint mip,
            uint x, uint y, uint z, uint width, uint height, uint depth, void* data, ulong bytes, uint rowPitch, uint depthPitch);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_DrawTexture", CallingConvention = CallingConvention.StdCall)]
        internal static extern int DrawTexture(DeviceHandle context, ulong resource, float left, float top, float right, float bottom, uint flip);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReadBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReadBuffer(DeviceHandle context, ulong resource, ulong offset, void* data, ulong bytes);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReadSubmittedBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReadSubmittedBuffer(DeviceHandle context, ulong resource, ulong offset, void* data, ulong bytes);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Capture", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Capture(DeviceHandle context, void* data, ulong bytes);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_GetLastDiagnostic", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetLastDiagnostic(DeviceHandle context, byte* data, uint capacity);
    }
}

public abstract class D3D12Resource : IDisposable
{
    internal D3D12NativeDevice Device { get; }
    private long _id;
    internal ulong Id => unchecked((ulong)System.Threading.Interlocked.Read(ref _id));
    private protected D3D12Resource(D3D12NativeDevice device, ulong id) { Device = device; _id = unchecked((long)id); }
    public void Dispose()
    {
        ulong id = unchecked((ulong)System.Threading.Interlocked.Exchange(ref _id, 0));
        if (id != 0) Release(id);
    }
    private protected virtual void Release(ulong id) => Device.Release(id);
}

public sealed class D3D12ComputePipeline : D3D12Resource
{
    internal D3D12ComputePipeline(D3D12NativeDevice device, ulong id) : base(device, id) { }
    private protected override void Release(ulong id) => Device.ReleasePipeline(id);
}

public sealed class D3D12Buffer : D3D12Resource
{
    public ulong Size { get; }
    internal D3D12Buffer(D3D12NativeDevice device, ulong id, ulong size) : base(device, id) => Size = size;
}

public sealed class D3D12Texture : D3D12Resource
{
    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public uint MipLevels { get; }
    public bool Is3D { get; }
    internal D3D12TextureFormat Format { get; }
    internal D3D12Texture(D3D12NativeDevice device, ulong id, uint width, uint height, D3D12TextureFormat? format = null)
        : this(device, id, width, height, 1, 1, false, format) { }
    internal D3D12Texture(D3D12NativeDevice device, ulong id, uint width, uint height, uint depth,
        uint mipLevels, bool is3D, D3D12TextureFormat? format = null) : base(device, id)
    { Width = width; Height = height; Depth = depth; MipLevels = mipLevels; Is3D = is3D; Format = format ?? new D3D12TextureFormat(28, 4); }
}
