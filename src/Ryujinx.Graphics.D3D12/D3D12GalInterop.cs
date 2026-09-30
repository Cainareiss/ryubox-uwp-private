using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.D3D12;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAttribute { public nint Name; public uint Index, Format, Slot, Offset, Classification, Divisor; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeVertexBuffer { public ulong Resource, Offset; public uint Bytes, Stride; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeStreamBuffer { public ulong Resource, Offset, Bytes, Counter; }
[InlineArray(4)] internal struct NativeStreamBuffers { private NativeStreamBuffer _element0; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeViewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDepthState
{
    public ulong Target;
    public uint Enable, Write, Comparison, NoColorTarget;
    public uint StencilEnable;
    public uint FrontFail, FrontPass, FrontDepthFail, FrontFunc, FrontRef, FrontReadMask, FrontWriteMask;
    public uint BackFail, BackPass, BackDepthFail, BackFunc, BackRef, BackReadMask, BackWriteMask;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRasterizer
{
    public uint Fill, Cull, FrontCcw;
    public int DepthBias;
    public float DepthBiasClamp, SlopeBias;
    public uint DepthClip, Multisample, AntialiasedLine, ForcedSampleCount, Conservative;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeTargetBlend
{
    public uint Enable, LogicEnable, Source, Destination, Operation, SourceAlpha, DestinationAlpha, AlphaOperation, LogicOp;
    public byte Mask;
}
[InlineArray(8)] internal struct NativeTargetBlends { private NativeTargetBlend _element0; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeBlend { public uint AlphaCoverage, Independent; public NativeTargetBlends Targets; }
[InlineArray(8)] internal struct NativeColorTargets { private ulong _element0; }
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDraw
{
    public ulong ColorTarget;
    public uint ColorTargetCount;
    // Explicit MRT arrays can contain null slots. The mask preserves the
    // guest SV_TargetN slot instead of compacting attachments.
    public uint ColorTargetMask;
    public NativeColorTargets ColorTargets;
    public NativeAttribute* Attributes;
    public NativeVertexBuffer* Vertices;
    public uint AttributeCount, VertexCount;
    public ulong IndexResource, IndexOffset;
    public uint IndexBytes, IndexFormat, Topology, Count, Instances, First, FirstInstance;
    public int BaseVertex;
    public NativeViewport Viewport;
    public NativeRect Scissor;
    public NativeRasterizer Rasterizer;
    public NativeBlend Blend;
    public ulong IndirectResource, IndirectOffset, CountResource, CountOffset;
    public uint MaxCommands, IndirectStride;
    public float BlendRed, BlendGreen, BlendBlue, BlendAlpha;
    public uint RasterizerDiscard;
    public uint StreamOutput;
    public NativeStreamBuffers StreamBuffers;
}

public sealed unsafe partial class D3D12NativeDevice
{
    internal ulong ResourceId(D3D12Resource resource) => Validate(resource);
    internal void StartCommands() => Check(GalNative.Begin(_handle));
    internal ulong FlushCommands() { Check(GalNative.Flush(_handle, out ulong value)); return value; }
    internal void MemoryBarrier() => Check(GalNative.Barrier(_handle));
    internal void Clear(D3D12Texture target, float r, float g, float b, float a)
    {
        float* color = stackalloc float[] { r, g, b, a };
        Check(GalNative.Clear(_handle, target == null ? 0 : Validate(target), color));
    }
    internal void CopyTexture(D3D12Texture from, D3D12Texture to, uint sx, uint sy, uint dx, uint dy, uint w, uint h) =>
        Check(GalNative.Copy(_handle, Validate(from), Validate(to), sx, sy, dx, dy, w, h));
    internal void CopyTexture(D3D12Texture from, D3D12Texture to, uint sourceMip, uint destinationMip,
        uint sx, uint sy, uint sz, uint dx, uint dy, uint dz, uint width, uint height, uint depth) =>
        Check(GalNative.CopyRegion(_handle, Validate(from), Validate(to), sourceMip, destinationMip,
            sx, sy, sz, dx, dy, dz, width, height, depth));
    internal void ClearDepth(D3D12Texture texture, float value) => Check(GalNative.ClearDepth(_handle, Validate(texture), value));
    internal byte[] ReadTexture(D3D12Texture texture)
    {
        byte[] data = new byte[checked((int)((ulong)texture.Width * texture.Height * (uint)texture.Format.Bytes))];
        fixed (byte* pointer = data) Check(GalNative.ReadTexture(_handle, Validate(texture), pointer, (ulong)data.Length));
        return data;
    }
    internal byte[] ReadTexture(D3D12Texture texture, uint mip, int size, uint rowPitch, uint depthPitch)
    {
        byte[] data = new byte[size];
        fixed (byte* pointer = data)
            Check(GalNative.ReadTextureSubresource(_handle, Validate(texture), mip, pointer, (ulong)data.Length, rowPitch, depthPitch));
        return data;
    }
    internal void Draw(D3D12Program program, ReadOnlySpan<D3D12ProgramBinding> bindings, ref NativeDraw draw, NativeDepthState? depth = null)
    {
        Span<NativeProgramBinding> native = bindings.Length <= StackBindingCount
            ? stackalloc NativeProgramBinding[bindings.Length]
            : new NativeProgramBinding[bindings.Length];
        ConvertBindings(bindings, native);
        fixed (NativeProgramBinding* pointer = native)
        fixed (NativeDraw* packet = &draw)
        {
            var state = depth.GetValueOrDefault();
            int result = depth.HasValue ? GalNative.DrawDepth(_handle, Validate(program), pointer, (uint)native.Length, packet, &state) :
                GalNative.Draw(_handle, Validate(program), pointer, (uint)native.Length, packet);
            if (result < 0)
            {
                byte[] detail = new byte[16384];
                fixed (byte* message = detail) ProgramNative.Error(_handle, message, (uint)detail.Length);
                int end = Array.IndexOf(detail, (byte)0);
                throw new InvalidOperationException($"D3D12 draw HRESULT=0x{result:X8}: {System.Text.Encoding.UTF8.GetString(detail, 0, end < 0 ? detail.Length : end)}");
            }
        }
    }
    internal ulong BeginCounter(uint type) { Check(GalNative.BeginCounter(_handle, type, out ulong id)); return id; }
    internal void EndCounter(ulong id) => Check(GalNative.EndCounter(_handle, id));
    internal ulong ReadCounter(ulong id) { Check(GalNative.ReadCounter(_handle, id, out ulong value)); return value; }
    internal void ReleaseCounter(ulong id) => Check(GalNative.ReleaseCounter(_handle, id));

    private static class GalNative
    {
        private const string Library = "Ryujinx.Uwp.Native.dll";
        [DllImport(Library, EntryPoint = "RyuboxD3D12_BeginCommands")] internal static extern int Begin(DeviceHandle c);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_Flush")] internal static extern int Flush(DeviceHandle c, out ulong fence);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_MemoryBarrier")] internal static extern int Barrier(DeviceHandle c);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ClearTexture")] internal static extern int Clear(DeviceHandle c, ulong id, float* color);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CopyTexture")] internal static extern int Copy(DeviceHandle c, ulong src, ulong dst, uint sx, uint sy, uint dx, uint dy, uint w, uint h);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CopyTextureRegion")] internal static extern int CopyRegion(DeviceHandle c,
            ulong src, ulong dst, uint srcMip, uint dstMip, uint sx, uint sy, uint sz, uint dx, uint dy, uint dz, uint w, uint h, uint d);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReadTexture")] internal static extern int ReadTexture(DeviceHandle c, ulong id, void* data, ulong bytes);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReadTextureSubresource")] internal static extern int ReadTextureSubresource(DeviceHandle c,
            ulong id, uint mip, void* data, ulong bytes, uint rowPitch, uint depthPitch);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_DrawProgram")] internal static extern int Draw(DeviceHandle c, ulong id, NativeProgramBinding* bindings, uint count, NativeDraw* draw);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_DrawProgramDepth")] internal static extern int DrawDepth(DeviceHandle c, ulong id, NativeProgramBinding* bindings, uint count, NativeDraw* draw, NativeDepthState* depth);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ClearDepthTexture")] internal static extern int ClearDepth(DeviceHandle c, ulong id, float depth);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_BeginCounter")] internal static extern int BeginCounter(DeviceHandle c, uint type, out ulong id);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_EndCounter")] internal static extern int EndCounter(DeviceHandle c, ulong id);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReadCounter")] internal static extern int ReadCounter(DeviceHandle c, ulong id, out ulong value);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReleaseCounter")] internal static extern int ReleaseCounter(DeviceHandle c, ulong id);
    }
}
