using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.D3D12;

public readonly record struct D3D12StreamOutput(string Semantic, uint Index, uint Component, uint Components, uint Buffer, uint Stream, uint Offset, uint Stride);
public readonly record struct D3D12ShaderSource(ShaderStage Stage, string Hlsl = null,
    D3D12StreamOutput[] StreamOutput = null, byte[] Binary = null, byte[] OriginalSpirv = null);

internal static class D3D12ShaderFailureDiagnostics
{
    public static void Write(D3D12ShaderSource[] sources, int result, string compilerMessage)
    {
        string root = Environment.GetEnvironmentVariable("RYUBOX_D3D12_SHADER_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(root))
        {
            string logs = Environment.GetEnvironmentVariable("RYUBOX_D3D12_LOG_DIR");
            root = string.IsNullOrWhiteSpace(logs)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ryubox", "ShaderFailures")
                : Path.Combine(logs, "shader-failures");
        }
        if (string.IsNullOrWhiteSpace(root)) return;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (D3D12ShaderSource source in sources)
        {
            hash.AppendData(BitConverter.GetBytes((int)source.Stage));
            if (source.OriginalSpirv is { Length: > 0 }) hash.AppendData(source.OriginalSpirv);
            else if (source.Hlsl != null) hash.AppendData(Encoding.UTF8.GetBytes(source.Hlsl));
        }
        string id = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);

        for (int index = 0; index < sources.Length; index++)
        {
            D3D12ShaderSource source = sources[index];
            string prefix = $"{index}-{source.Stage.ToString().ToLowerInvariant()}";
            if (source.Hlsl != null)
                File.WriteAllText(Path.Combine(directory, prefix + ".hlsl"), source.Hlsl, new UTF8Encoding(false));
            if (source.OriginalSpirv is { Length: > 0 } spirv)
                File.WriteAllBytes(Path.Combine(directory, prefix + ".spv"), spirv);
        }

        string message = compilerMessage ?? string.Empty;
        if (message.Length > 64 * 1024) message = message[..(64 * 1024)];
        File.WriteAllText(Path.Combine(directory, "compiler.txt"),
            $"hash={id}{Environment.NewLine}hresult=0x{result:X8}{Environment.NewLine}{message}",
            new UTF8Encoding(false));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeStreamOutput
{
    public fixed byte Semantic[256];
    public uint Index, Component, Components, Buffer, Stream, Offset, Stride;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D12BindingInfo
{
    public uint Stage, Type, Space, Binding, Count, Dimension;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeVertexInput
{
    public fixed byte Semantic[256];
    public uint Index, ComponentType, Mask;
}
internal readonly record struct D3D12VertexInput(string Semantic, uint Index, uint ComponentType, uint Mask);

[StructLayout(LayoutKind.Sequential)]
public unsafe struct D3D12SamplerDescriptor
{
    public uint Filter, AddressU, AddressV, AddressW;
    public float MipLodBias;
    public uint MaxAnisotropy, Comparison;
    public fixed float BorderColor[4];
    public float MinLod, MaxLod;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeProgramBinding
{
    public uint Stage, Type, Space, Binding;
    public ulong Resource, Offset, Bytes;
    public uint Format, Stride, ComponentMapping;
    public D3D12SamplerDescriptor Sampler;
}

/// <summary>A bound resource uses its exact reflected stage/register/space.</summary>
public readonly record struct D3D12ProgramBinding(ShaderStage Stage, uint Type, uint Space, uint Binding,
    D3D12Resource Resource, ulong Offset = 0, ulong Bytes = 0, uint Stride = 0,
    D3D12SamplerDescriptor Sampler = default, uint ComponentMapping = 0, uint Format = 0);

/// <summary>GAL program backed by compiled D3D12 shaders and a reflected root signature.</summary>
public sealed class D3D12Program : D3D12Resource, IProgram
{
    private readonly byte[] _binary;
    private readonly D3D12BindingInfo[] _bindings;
    public ReadOnlySpan<D3D12BindingInfo> Bindings => _bindings;
    internal D3D12VertexInput[] VertexInputs { get; }
    // Set only by the GAL renderer entry points. The GPU translator specializes
    // alpha test into these shaders; raw device HLSL has no such guarantee.
    internal bool UsesGuestSpecialization { get; set; }
    internal bool HasStreamOutput { get; }
    internal bool HasHullShader { get; }
    internal D3D12Program(D3D12NativeDevice device, ulong id, D3D12ShaderSource[] sources, D3D12BindingInfo[] bindings, D3D12VertexInput[] inputs)
        : base(device, id)
    {
        _bindings = bindings;
        VertexInputs = inputs;
        HasStreamOutput = sources.Any(s => s.StreamOutput?.Length > 0);
        HasHullShader = sources.Any(s => s.Stage == ShaderStage.TessellationControl);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x35503244); // D2P5: preserve actual bytecode, not just HLSL sources.
        writer.Write(sources.Length);
        foreach (var source in sources)
        {
            writer.Write((int)source.Stage); writer.Write(source.Hlsl ?? string.Empty);
            // Hull variants still require source specialization for the input
            // patch size. Other stages restore without invoking FXC.
            byte[] compiled = source.Binary ?? (source.Stage == ShaderStage.TessellationControl
                ? [] : device.GetProgramBytecode(id, source.Stage));
            writer.Write(compiled.Length);
            writer.Write(compiled);
            writer.Write(source.StreamOutput?.Length ?? 0);
            if (source.StreamOutput != null) foreach (var e in source.StreamOutput)
            {
                writer.Write(e.Semantic); writer.Write(e.Index); writer.Write(e.Component); writer.Write(e.Components);
                writer.Write(e.Buffer); writer.Write(e.Stream); writer.Write(e.Offset); writer.Write(e.Stride);
            }
        }
        _binary = stream.ToArray();
    }
    public ProgramLinkStatus CheckProgramLink(bool blocking)
    {
        ObjectDisposedException.ThrowIf(Id == 0, this);
        return ProgramLinkStatus.Success; // Native compilation and reflection are synchronous.
    }
    public byte[] GetBinary()
    {
        ObjectDisposedException.ThrowIf(Id == 0, this);
        return (byte[])_binary.Clone();
    }
    internal static D3D12ShaderSource[] Decode(byte[] binary)
    {
        ArgumentNullException.ThrowIfNull(binary);
        if (binary.Length > 24 * 1024 * 1024) throw new InvalidDataException("D3D12 program cache exceeds size limit.");
        using var stream = new MemoryStream(binary, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        int version = reader.ReadInt32();
        if (version != 0x34503244 && version != 0x35503244) throw new InvalidDataException("Not a supported D3D12 program cache (D2P4/D2P5).");
        int count = reader.ReadInt32();
        if (count is < 1 or > 5) throw new InvalidDataException("Invalid shader count.");
        var shaders = new D3D12ShaderSource[count];
        for (int i = 0; i < count; ++i)
        {
            int stage = reader.ReadInt32();
            if (stage is < 0 or > 5) throw new InvalidDataException("Invalid shader stage.");
            string hlsl = reader.ReadString();
            byte[] compiledPayload = null;
            if (version == 0x35503244)
            {
                int size = reader.ReadInt32();
                if (size < 0 || size > 4194304 || size > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid cached bytecode length.");
                if (size != 0) compiledPayload = reader.ReadBytes(size);
            }
            int entries = reader.ReadInt32();
            if (entries is < 0 or > 128) throw new InvalidDataException("Invalid stream-output layout count.");
            var layout = new D3D12StreamOutput[entries];
            for (int j = 0; j < entries; ++j)
                layout[j] = new(reader.ReadString(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
            shaders[i] = new((ShaderStage)stage, hlsl, layout, compiledPayload);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing program cache data.");
        return shaders;
    }
    private protected override void Release(ulong id) => Device.ReleaseProgram(id);
}

public sealed unsafe partial class D3D12NativeDevice
{
    internal byte[] GetProgramBytecode(ulong id, ShaderStage stage)
    {
        ulong size = 0;
        Check(ProgramNative.Bytecode(_handle, id, (uint)stage, null, ref size));
        if (size is 0 or > 4194304) throw new InvalidDataException("Invalid native shader bytecode size.");
        var data = new byte[(int)size];
        fixed (byte* p = data) Check(ProgramNative.Bytecode(_handle, id, (uint)stage, p, ref size));
        return data;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeShader { public uint Stage; public nint Source; public ulong Bytes; }

    public D3D12Program CreateProgram(params D3D12ShaderSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length is < 1 or > 5) throw new ArgumentException("Invalid stage count.", nameof(sources));
        sources = sources.ToArray();
        var pins = new GCHandle[sources.Length];
        var native = new NativeShader[sources.Length];
        ulong id = 0;
        try
        {
            for (int i = 0; i < sources.Length; ++i)
            {
                byte[] bytes = sources[i].Binary ?? (sources[i].Hlsl != null ? Encoding.UTF8.GetBytes(sources[i].Hlsl) :
                    throw new ArgumentException("Shader must provide either Binary or Hlsl."));
                if (bytes.Length is 0 or > 4194304) throw new ArgumentException("Invalid shader size.");
                pins[i] = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                native[i] = new() { Stage = (uint)sources[i].Stage, Source = pins[i].AddrOfPinnedObject(), Bytes = (ulong)bytes.Length };
            }
            int result;
            fixed (NativeShader* pointer = native) result = ProgramNative.Create(_handle, pointer, (uint)native.Length, out id);
            if (result < 0)
            {
                byte[] detail = new byte[16384];
                fixed (byte* pointer = detail) ProgramNative.Error(_handle, pointer, (uint)detail.Length);
                int end = Array.IndexOf(detail, (byte)0);
                string err = Encoding.UTF8.GetString(detail, 0, end < 0 ? detail.Length : end);
                try { D3D12ShaderFailureDiagnostics.Write(sources, result, err); }
                catch { /* Diagnostics must never replace the compiler error. */ }
                throw new InvalidOperationException($"D3D12 program HRESULT=0x{result:X8}: {err}");
            }
            foreach (var source in sources)
            {
                if (source.StreamOutput is not { Length: > 0 } layout) continue;
                if (layout.Length > 128) throw new ArgumentException("Stream-output layout is too large.");
                var entries = new NativeStreamOutput[layout.Length];
                for (int i = 0; i < entries.Length; ++i)
                {
                    var e = layout[i]; byte[] name = Encoding.UTF8.GetBytes(e.Semantic);
                    if (name.Length is 0 or >= 256 || Array.IndexOf(name, (byte)0) >= 0) throw new ArgumentException("Invalid stream-output semantic.");
                    entries[i] = new() { Index = e.Index, Component = e.Component, Components = e.Components, Buffer = e.Buffer,
                        Stream = e.Stream, Offset = e.Offset, Stride = e.Stride };
                    fixed (byte* target = entries[i].Semantic) name.CopyTo(new Span<byte>(target, 256));
                }
                fixed (NativeStreamOutput* pointer = entries)
                    Check(ProgramNative.StreamOutput(_handle, id, (uint)source.Stage, pointer, (uint)entries.Length));
            }
            uint count = 0;
            Check(ProgramNative.GetBindings(_handle, id, null, ref count));
            var bindings = new D3D12BindingInfo[count];
            fixed (D3D12BindingInfo* pointer = bindings) Check(ProgramNative.GetBindings(_handle, id, pointer, ref count));
            uint inputCount = 0;
            Check(ProgramNative.GetInputs(_handle, id, null, ref inputCount));
            var inputs = new NativeVertexInput[inputCount];
            fixed (NativeVertexInput* pointer = inputs) Check(ProgramNative.GetInputs(_handle, id, pointer, ref inputCount));
            var managedInputs = new D3D12VertexInput[inputCount];
            for (int i = 0; i < inputs.Length; ++i)
            {
                fixed (byte* name = inputs[i].Semantic)
                    managedInputs[i] = new(Marshal.PtrToStringUTF8((nint)name), inputs[i].Index, inputs[i].ComponentType, inputs[i].Mask);
            }
            return new D3D12Program(this, id, sources, bindings, managedInputs);
        }
        catch
        {
            if (id != 0) ReleaseProgram(id);
            throw;
        }
        finally { foreach (var pin in pins) if (pin.IsAllocated) pin.Free(); }
    }

    public D3D12Program LoadProgramBinary(byte[] binary) => CreateProgram(D3D12Program.Decode(binary));

    private NativeProgramBinding[] ConvertBindings(ReadOnlySpan<D3D12ProgramBinding> bindings)
    {
        var result = new NativeProgramBinding[bindings.Length];
        for (int i = 0; i < result.Length; ++i)
        {
            ref readonly var b = ref bindings[i];
            result[i] = new() { Stage = (uint)b.Stage, Type = b.Type, Space = b.Space, Binding = b.Binding,
                Resource = b.Type == 3 ? 0 : Validate(b.Resource), Offset = b.Offset, Bytes = b.Bytes, Stride = b.Stride,
                Format = b.Format, ComponentMapping = b.ComponentMapping, Sampler = b.Sampler };
        }
        return result;
    }
    public void Dispatch(D3D12Program program, ReadOnlySpan<D3D12ProgramBinding> bindings, uint x, uint y = 1, uint z = 1)
    {
        ulong id = Validate(program);
        var native = ConvertBindings(bindings);
        fixed (NativeProgramBinding* pointer = native) Check(ProgramNative.Dispatch(_handle, id, pointer, (uint)native.Length, x, y, z));
    }
    internal void ReleaseProgram(ulong id)
    {
        if (!_handle.IsClosed)
        {
            try { Check(ProgramNative.Release(_handle, id)); }
            catch (ObjectDisposedException) when (_handle.IsClosed) { }
        }
    }

    private static class ProgramNative
    {
        [DllImport(Library, EntryPoint = "RyuboxD3D12_GetProgramBytecode", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Bytecode(DeviceHandle context, ulong id, uint stage, byte* data, ref ulong size);
        private const string Library = "Ryujinx.Uwp.Native.dll";
        [DllImport(Library, EntryPoint = "RyuboxD3D12_CreateProgram", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Create(DeviceHandle context, NativeShader* shaders, uint count, out ulong id);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_GetProgramError", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Error(DeviceHandle context, byte* data, uint capacity);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ReleaseProgram", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Release(DeviceHandle context, ulong id);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_GetProgramBindings", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetBindings(DeviceHandle context, ulong id, D3D12BindingInfo* bindings, ref uint count);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_GetVertexInputs", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetInputs(DeviceHandle context, ulong id, NativeVertexInput* inputs, ref uint count);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_ConfigureStreamOutput", CallingConvention = CallingConvention.StdCall)]
        internal static extern int StreamOutput(DeviceHandle context, ulong id, uint stage, NativeStreamOutput* entries, uint count);
        [DllImport(Library, EntryPoint = "RyuboxD3D12_DispatchProgram", CallingConvention = CallingConvention.StdCall)]
        internal static extern int Dispatch(DeviceHandle context, ulong id, NativeProgramBinding* bindings, uint count, uint x, uint y, uint z);
    }
}
