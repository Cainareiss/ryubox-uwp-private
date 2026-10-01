using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.D3D12;

public sealed unsafe class D3D12Pipeline : IPipeline
{
    private readonly D3D12Renderer _renderer;
    private NativeDraw _state = DefaultDraw();
    private D3D12Program _program;
    private D3D12GalTexture _target;
    private D3D12GalTexture[] _colorTargets = [];
    private readonly D3D12Texture[] _attachmentProxies = new D3D12Texture[8];
    private BufferRange _index;
    private IndexType _indexType;
    private VertexAttribDescriptor[] _attributes = [];
    private VertexBufferDescriptor[] _vertices = [];
    private readonly Dictionary<int, BufferRange> _uniforms = new(), _storage = new();
    private readonly Dictionary<(ShaderStage, int), (D3D12GalTexture Texture, D3D12Sampler Sampler)> _textures = new();
    private readonly Dictionary<(ShaderStage, int), D3D12TextureArray> _arrays = new(), _separateArrays = new();
    private readonly Dictionary<(ShaderStage, int), D3D12ImageArray> _images = new();
    private readonly Dictionary<(ShaderStage, int), D3D12ImageArray> _separateImages = new();
    private readonly Dictionary<(ShaderStage, int), D3D12GalTexture> _imageTextures = new();
    private PrimitiveTopology _topology = PrimitiveTopology.Triangles;
    private int _patchVertices = 3;
    private readonly float[] _patchOuter = [1,1,1,1], _patchInner = [1,1];
    private bool _discard, _alphaTest, _restart;
    private uint _restartIndex;
    private bool _cullEnable;
    private Face _cullFace = Face.Back;
    private PolygonMode _polygonMode = PolygonMode.Fill;
    private PolygonModeMask _depthBiasModes;
    private float _depthBiasFactor;
    private int _depthBiasUnits;
    private float _depthBiasClamp;
    private float _renderScale = 1;
    private DepthMode _depthMode;
    private bool _feedback;
    private BufferRange[] _feedbackBuffers = [];
    private readonly D3D12Buffer[] _feedbackCounters = new D3D12Buffer[4];

    internal D3D12Pipeline(D3D12Renderer renderer)
    {
        _renderer = renderer; var info = renderer.Device.GetInfo();
        _state.Viewport = new() { Width = info.Width, Height = info.Height, MaxDepth = 1 };
        _state.Scissor = new() { Right = (int)info.Width, Bottom = (int)info.Height };
    }
    internal static NativeDraw DefaultDraw()
    {
        NativeDraw draw = default; draw.Topology = 4; draw.ColorTargetCount = 1;
        draw.Rasterizer = new() { Fill = 3, Cull = 1, DepthClip = 1 };
        for (int i = 0; i < 8; ++i) draw.Blend.Targets[i] = new() { Source = 2, SourceAlpha = 2, Destination = 1, DestinationAlpha = 1, Operation = 1, AlphaOperation = 1, LogicOp = 4, Mask = 15 };
        return draw;
    }
    private static NotSupportedException Unsupported(string operation) => new($"D3D12 GAL: {operation} is not implemented. No draw was silently discarded.");
    public float RenderScale { set { if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); _renderScale = value; } }
    public void Barrier() { _renderer.EnsureCommands(); _renderer.Device.MemoryBarrier(); }
    public void TextureBarrier() => Barrier();
    public void TextureBarrierTiled() => Barrier();
    public void CommandBufferBarrier() => _renderer.Flush();
    public void CopyBuffer(BufferHandle from, BufferHandle to, int srcOffset, int dstOffset, int size)
    {
        var source = _renderer.Buffer(from, srcOffset, size); var target = _renderer.Buffer(to, dstOffset, size);
        if (size == 0) return;
        _renderer.EnsureCommands(); _renderer.Device.Copy(source, target, (ulong)srcOffset, (ulong)dstOffset, (ulong)size);
    }
    public void ClearBuffer(BufferHandle target, int offset, int size, uint value)
    {
        if (offset < 0 || size < 0 || (size & 3) != 0) throw new ArgumentOutOfRangeException(nameof(size));
        _renderer.Buffer(target, offset, size);
        uint[] chunk = new uint[Math.Min(size / 4, 4096)]; Array.Fill(chunk, value);
        for (int done = 0; done < size; done += chunk.Length * 4)
            _renderer.SetBufferData(target, checked(offset + done), MemoryMarshal.AsBytes(chunk.AsSpan()).Slice(0, Math.Min(chunk.Length * 4, size - done)));
    }
    public void ClearRenderTargetColor(int index, int layer, int layers, uint mask, ColorF color)
    {
        if ((mask & ~15u) != 0) throw new ArgumentOutOfRangeException(nameof(mask));
        if ((uint)index >= (uint)Math.Max(1, _colorTargets.Length)) throw new ArgumentOutOfRangeException(nameof(index));
        D3D12GalTexture target = _colorTargets.Length == 0 ? null : _colorTargets[index];
        // Match DrawProgram's implicit backbuffer convention. Explicit null
        // attachment slots (and depth-only targets) must remain unbound.
        if (target == null)
        {
            if (layer != 0 || layers != 1) throw Unsupported("layered backbuffer clear");
            if (_colorTargets.Length == 0 && _depthTarget == null)
                _renderer.ClearColorChannels(null, mask, color);
            return;
        }

        int availableLayers = target.Info.Target == Target.Texture3D ? target.Info.Depth : target.Info.GetLayers();
        if (layer < 0 || layers <= 0 || layer > availableLayers - layers)
            throw new ArgumentOutOfRangeException(nameof(layer));
        if (mask == 0) return;
        // Layer zero is still a single-slice view when the backing texture
        // contains multiple slices. Clearing the original RTV clears them all.
        if (layer == 0 && layers == 1 && availableLayers == 1)
        {
            _renderer.ClearColorChannels(target, mask, color);
            return;
        }

        // A full-channel ClearRenderTargetView applies to every slice in a
        // layered/3D RTV. A masked utility draw has no SV_RenderTargetArrayIndex,
        // so issue one draw per one-slice RTV to preserve the other channels.
        if (mask == 15)
        {
            D3D12GalTexture range = target.CreateRenderTargetSliceView(layer, layers);
            try { _renderer.ClearColorChannels(range, mask, color); }
            finally { range.Release(); }
        }
        else
        {
            for (int slice = 0; slice < layers; ++slice)
            {
                D3D12GalTexture range = target.CreateRenderTargetSliceView(checked(layer + slice), 1);
                try { _renderer.ClearColorChannels(range, mask, color); }
                finally { range.Release(); }
            }
        }
    }
    private D3D12GalTexture _depthTarget;
    private NativeDepthState _depthState = new() { Comparison = 8 };
    public void ClearRenderTargetDepthStencil(int layer, int layers, float depth, bool depthMask, int stencil, int stencilMask)
    {
        if (_depthTarget == null || (!depthMask && (stencilMask & 255) == 0)) return;
        if (depthMask && !float.IsFinite(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        // Validate the complete range before clearing any slice. Utility stencil
        // draws do not emit SV_RenderTargetArrayIndex, so clear slices separately.
        D3D12GalTexture range = _depthTarget.CreateRenderTargetSliceView(layer, layers);
        try
        {
            if (depthMask) _renderer.ClearDepth(range, depth);
            if ((stencilMask & 255) != 0)
            {
                for (int slice = 0; slice < layers; slice++)
                {
                    D3D12GalTexture single = range.CreateRenderTargetSliceView(slice, 1);
                    try { _renderer.ClearStencil(single, stencil, stencilMask); }
                    finally { if (!ReferenceEquals(single, range)) single.Release(); }
                }
            }
        }
        finally { if (!ReferenceEquals(range, _depthTarget)) range.Release(); }
    }
    public void SetProgram(IProgram program)
    {
        if (program is not D3D12Program p) throw new ArgumentException("Foreign program.");
        _renderer.Device.ResourceId(p); _program = p;
    }
    public void DispatchCompute(int x, int y, int z)
    {
        if (_program == null) throw new InvalidOperationException("No shader program.");
        var bindings = Bindings(); _renderer.ReserveDescriptors(_program, false);
        _renderer.Device.Dispatch(_program, bindings, checked((uint)x), checked((uint)y), checked((uint)z));
    }
    private D3D12ProgramBinding[] Bindings(bool logDiag = false)
    {
        var result = new List<D3D12ProgramBinding>(_program.BindingSlotCount);
        var diagLines = logDiag ? new System.Text.StringBuilder() : null;
        if (logDiag)
        {
            diagLines.AppendLine($"  _uniforms keys=[{string.Join(',', _uniforms.Keys)}]");
            diagLines.AppendLine($"  _textures keys=[{string.Join(',', _textures.Keys.Select(k => $"{k.Item1}:{k.Item2}"))}]");
        }
        foreach (var b in _program.Bindings)
            for (uint element = 0; element < b.Count; ++element)
            {
                int binding = checked((int)(b.Binding + element)); var stage = (ShaderStage)b.Stage;
                // Resource spaces are part of the D3D12 translation contract:
                // spaces 0 and 1 contain GAL buffer bindings (older/generated
                // shaders use space 0; translated guest SSBOs use space 1),
                // while space 2 is the texture/sampler namespace. A buffer
                // SRV in space 2 is a buffer texture and must come from
                // SetTextureAndSampler. Restricting SSBOs to space 1 made
                // valid space-0 RWByteAddressBuffers fall through to a zero
                // texture; the native reflection then correctly rejected that
                // texture against a buffer descriptor. Besides failing dense
                // compute passes, that can leave 3D material data unbound.
                bool isStorageBuffer = b.Dimension is 1 or 11 && (b.Space is 0 or 1) && b.Type != 3;
                if (b.Type == 2 || isStorageBuffer)
                {
                    var set = b.Type == 2 ? _uniforms : _storage;
                    bool found = set.TryGetValue(binding, out var buffer);
                    bool valid = found && !(b.Type == 2 && (buffer.Handle == BufferHandle.Null || buffer.Size <= 0));
                    if (!valid)
                    {
                        // Optional/optimized-out constant buffers are legal in
                        // the GAL. D3D12 still needs a valid CBV descriptor;
                        // bind a zero-filled, aligned snapshot. Storage buffers
                        // remain strict because inventing one changes writes.
                        if (b.Type != 2)
                            throw new InvalidOperationException($"Missing D3D12 buffer {stage}:space{b.Space}, binding{binding}.");
                        buffer = _renderer.ZeroUniformBuffer;
                        diagLines?.AppendLine($"  CBV MISS: stage={stage} space={b.Space} binding={binding} found={found}");
                    }
                    else
                    {
                        diagLines?.AppendLine($"  CBV OK:   stage={stage} space={b.Space} binding={binding} handle={buffer.Handle} sz={buffer.Size}");
                    }
                    result.Add(new(stage, b.Type, b.Space, (uint)binding, _renderer.Buffer(buffer.Handle, buffer.Offset, buffer.Size), checked((ulong)buffer.Offset), checked((ulong)buffer.Size)));
                }
                else
                {
                    D3D12GalTexture texture = null; D3D12Sampler sampler = null;
                    if (_textures.TryGetValue((stage, binding), out var pair)) (texture, sampler) = pair;
                    D3D12TextureArray array = null; int index = (int)element;
                    if (_separateArrays.TryGetValue((stage, (int)b.Space), out array)) index = binding;
                    else _arrays.TryGetValue((stage, (int)b.Binding), out array);
                    if (array != null)
                    {
                        if (array.Textures == null || array.Samplers == null) throw new ObjectDisposedException(nameof(array));
                        if (b.Type != 3) texture = array.Textures[index] == null ? null : _renderer.Texture(array.Textures[index], bound: true);
                        else sampler = array.Samplers[index] as D3D12Sampler;
                    }
                    if (b.Type == 1 || b.Space == 3)
                    {
                        _imageTextures.TryGetValue((stage, binding), out texture);
                        D3D12ImageArray images = null; int imageIndex = (int)element;
                        if (_separateImages.TryGetValue((stage, (int)b.Space), out images)) imageIndex = binding;
                        else _images.TryGetValue((stage, (int)b.Binding), out images);
                        if (images != null)
                        {
                            if (images.Images == null) throw new ObjectDisposedException(nameof(images));
                            texture = images.Images[imageIndex] == null ? null : _renderer.Texture(images.Images[imageIndex], bound: true);
                        }
                    }
                    if (b.Type == 3)
                    {
                        sampler ??= _renderer.ZeroSampler;
                        result.Add(new(stage, 3, b.Space, (uint)binding, null, Sampler: sampler.Descriptor));
                    }
                    else
                    {
                        bool texMissed = texture == null;
                        texture ??= _renderer.GetZeroTexture(b.Dimension);
                        // Texture swizzles are a view property. The native layer
                        // encodes this mapping in the SRV rather than changing the
                        // underlying guest texture bytes.
                        result.Add(texture.GetBinding(stage, b.Type, b.Space, (uint)binding));
                        if (texMissed) diagLines?.AppendLine($"  SRV MISS: stage={stage} space={b.Space} binding={binding}");
                        else diagLines?.AppendLine($"  SRV OK:   stage={stage} space={b.Space} binding={binding} {texture.DiagnosticView}");
                    }
                }
            }
        if (diagLines != null)
        {
            string path = System.IO.Path.Combine(LogDir, "binding_diag.txt");
            try { System.IO.File.AppendAllText(path, diagLines.ToString()); } catch { }
        }
        return result.ToArray();
    }
    internal int Last1080pRenderWidth { get; private set; }
    internal int Last1080pRenderHeight { get; private set; }

    public void Draw(int count, int instances, int first, int firstInstance) => Execute(false, count, instances, first, 0, firstInstance);
    public void DrawIndexed(int count, int instances, int first, int baseVertex, int firstInstance) => Execute(true, count, instances, first, baseVertex, firstInstance);
    public void DrawIndirect(BufferRange args) => Execute(false, 0, 0, 0, 0, 0, args, default, 1, 16);
    public void DrawIndexedIndirect(BufferRange args) => Execute(true, 0, 0, 0, 0, 0, args, default, 1, 20);
    public void DrawIndirectCount(BufferRange args, BufferRange count, int max, int stride) => Execute(false, 0, 0, 0, 0, 0, args, count, max, stride);
    public void DrawIndexedIndirectCount(BufferRange args, BufferRange count, int max, int stride) => Execute(true, 0, 0, 0, 0, 0, args, count, max, stride);
    private int _executeCount;
    private readonly HashSet<(Format Format, int Phase)> _targetProbes = new();
    internal static readonly bool ProbeTargets = Environment.GetEnvironmentVariable("RYUBOX_D3D12_PROBE_TARGETS") == "1";
    internal static readonly bool BindingDiag = Environment.GetEnvironmentVariable("RYUBOX_D3D12_BINDING_DIAG") == "1";
    private static readonly int BindingDiagStartDraw =
        int.TryParse(Environment.GetEnvironmentVariable("RYUBOX_D3D12_BINDING_DIAG_START_DRAW"), out int startDraw)
            ? Math.Max(0, startDraw) : 0;
    private static readonly int BindingDiagWindowDraws =
        int.TryParse(Environment.GetEnvironmentVariable("RYUBOX_D3D12_BINDING_DIAG_WINDOW_DRAWS"), out int windowDraws)
            ? Math.Clamp(windowDraws, 0, 256) : 0;
    // Diagnostic A/B only: leave the original color target bound and let the
    // native renderer omit an incompatible DSV for this draw. Never change
    // the default path based on a single title without comparing captures.
    private static readonly bool DiagnosticSkipAttachmentProxy =
        Environment.GetEnvironmentVariable("RYUBOX_D3D12_DIAG_SKIP_ATTACHMENT_PROXY") == "1";
    private readonly HashSet<D3D12Program> _bindingDiagPrograms = new();
    internal static readonly string LogDir = ResolveLogDir();

    private static string ResolveLogDir()
    {
        // NativeAOT/CoreHost can start with System32 as its current directory.
        // Diagnostics must never write there or turn a probe into an emulation
        // failure. Prefer the explicit harness/package log directory.
        string configured = Environment.GetEnvironmentVariable("RYUBOX_D3D12_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try { System.IO.Directory.CreateDirectory(configured); return configured; }
            catch { }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string packageLogDir = System.IO.Path.Combine(localAppData, @"Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\Logs");
        try { System.IO.Directory.CreateDirectory(packageLogDir); return packageLogDir; }
        catch
        {
            string tempLogDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ryubox", "Logs");
            try { System.IO.Directory.CreateDirectory(tempLogDir); } catch { }
            return tempLogDir;
        }
    }
    private void Execute(bool indexed, int count, int instances, int first, int baseVertex, int firstInstance,
        BufferRange indirect = default, BufferRange counter = default, int max = 0, int stride = 0)
    {
        int ec = System.Threading.Interlocked.Increment(ref _executeCount);
        if (ProbeTargets && (ec == 1 || ec % 500 == 0))
        {
            try
            {
                string targetDesc = _target != null ? $"{_target.Width}x{_target.Height} fmt={_target.Info.Format} id={_target.Native?.Id}" : "null";
                string depthDesc = _depthTarget != null ? $"{_depthTarget.Width}x{_depthTarget.Height} fmt={_depthTarget.Info.Format} id={_depthTarget.Native?.Id}" : "null";
                string line = $"[Draw #{ec}] target={targetDesc}, depth={depthDesc}, vp={_state.Viewport.Width}x{_state.Viewport.Height}\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, "draw_targets.txt"), line);
            }
            catch {}
        }

        if (_target != null && _target.Width == 1920 && _target.Height == 1080)
        {
            int vw = (int)MathF.Round(_state.Viewport.Width);
            int vh = (int)MathF.Round(_state.Viewport.Height);
            if (vw > Last1080pRenderWidth) Last1080pRenderWidth = vw;
            if (vh > Last1080pRenderHeight) Last1080pRenderHeight = vh;
        }

        if (_program == null) throw new InvalidOperationException("No shader program.");
        // IA topology belongs to graphics draws; never reject a compute dispatch
        // because the last graphics operation selected patches.
        if (_topology == PrimitiveTopology.Patches && !_program.HasHullShader)
            throw Unsupported($"tessellation without a hull shader (default outer levels {string.Join(',', _patchOuter)}, inner {string.Join(',', _patchInner)})");
        if (_alphaTest && !_discard && !_program.UsesGuestSpecialization)
            throw Unsupported("alpha test on raw HLSL; use the GAL's specialized SPIR-V program");
        // Primitive restart interprets an index value; non-indexed draws have
        // no restart marker. Do not expand/upload a temporary index buffer
        // just because a previous indexed draw left restart enabled.
        if (D3D12Topology.NeedsExpansion(_topology) || indexed && (_restart || _indexType == IndexType.UByte))
        {
            FallbackDraw(indexed, count, instances, first, baseVertex, firstInstance, indirect, counter, max, stride);
            return;
        }
        var draw = _state;
        // Translated guest shaders retain the GAL winding convention. Match
        // D3D11's upper-left host-origin conversion; native utility HLSL is
        // already authored in the host convention and must not be flipped.
        if (_program.UsesGuestSpecialization)
            draw.Rasterizer.FrontCcw ^= 1u;
        // Native reflection classifies the final primitive (GS/tessellation can
        // change the IA topology). Bit 0 discards all; bit 1 culls polygons only.
        draw.RasterizerDiscard = (_discard ? 1u : 0u) |
            (_cullEnable && _cullFace == Face.FrontAndBack ? 2u : 0u);
        if (_feedback)
        {
            if (!_program.HasStreamOutput) throw new InvalidOperationException("Active transform feedback requires a reflected output layout.");
            draw.StreamOutput = 1;
            for (int i = 0; i < _feedbackBuffers.Length; ++i)
            {
                var buffer = _feedbackBuffers[i];
                if (buffer.Size == 0) continue;
                draw.StreamBuffers[i] = new() { Resource = _renderer.Device.ResourceId(_renderer.Buffer(buffer.Handle, buffer.Offset, buffer.Size)),
                    Offset = checked((ulong)buffer.Offset), Bytes = checked((ulong)buffer.Size), Counter = _renderer.Device.ResourceId(_feedbackCounters[i]) };
            }
        }
        // GAL callers commonly retain a fixed-size attachment array and leave
        // its tail null. D3D12 receives the count as an OM descriptor count,
        // so forwarding that capacity would bind null RTV handles and can
        // fault legacy drivers. Trim only trailing nulls; a hole before a
        // later attachment changes the guest SV_Target index and needs shader
        // remapping rather than silent compaction.
        int colorTargetCount = _colorTargets.Length;
        while (colorTargetCount > 0 && _colorTargets[colorTargetCount - 1] == null)
        {
            colorTargetCount--;
        }


        draw.ColorTargetCount = colorTargetCount == 0 && _depthTarget != null
            ? 0u
            : checked((uint)Math.Max(1, colorTargetCount));
        for (int i = 0; i < colorTargetCount; i++)
        {
            D3D12GalTexture target = _colorTargets[i];
            if (target == null) continue;
            draw.ColorTargets[i] = _renderer.Device.ResourceId(target.Native);
            draw.ColorTargetMask |= 1u << i;
        }
        draw.ColorTarget = draw.ColorTargets[0];
        draw.Topology = Topology(_topology, _patchVertices);
        draw.Count = checked((uint)count); draw.Instances = checked((uint)instances); draw.First = checked((uint)first);
        draw.BaseVertex = baseVertex; draw.FirstInstance = checked((uint)firstInstance);
        if (indexed)
        {
            draw.IndexResource = _renderer.Device.ResourceId(_renderer.Buffer(_index.Handle, _index.Offset, _index.Size));
            draw.IndexOffset = checked((ulong)_index.Offset); draw.IndexBytes = checked((uint)_index.Size);
            draw.IndexFormat = _indexType switch { IndexType.UShort => 57, IndexType.UInt => 42, _ => throw Unsupported("8-bit indices") };
        }
        if (max > 0)
        {
            if (stride < (indexed ? 20 : 16) || (stride & 3) != 0 || (long)max * stride > indirect.Size)
                throw new ArgumentOutOfRangeException(nameof(indirect), "Indirect arguments exceed the supplied range.");
            draw.IndirectResource = _renderer.Device.ResourceId(_renderer.Buffer(indirect.Handle, indirect.Offset, indirect.Size));
            draw.IndirectOffset = checked((ulong)indirect.Offset); draw.MaxCommands = (uint)max; draw.IndirectStride = checked((uint)stride);
            if (counter.Size > 0)
            {
                if (counter.Size < 4) throw new ArgumentException("Indirect count range is too small.");
                draw.CountResource = _renderer.Device.ResourceId(_renderer.Buffer(counter.Handle, counter.Offset, counter.Size)); draw.CountOffset = checked((ulong)counter.Offset);
            }
        }
        var vertexList = new List<NativeVertexBuffer>();
        var remappedSlots = new Dictionary<int, uint>();
        var attributes = new NativeAttribute[_program.VertexInputs.Length];
        var names = new List<nint>();
        D3D12Buffer zero = null;
        try
        {
            for (int i = 0; i < attributes.Length; ++i)
            {
                var input = _program.VertexInputs[i];
                if (!input.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) || input.Index >= _attributes.Length)
                    throw new InvalidOperationException($"Unmapped vertex input {input.Semantic}{input.Index}.");
                var a = _attributes[input.Index];
                nint name = Marshal.StringToCoTaskMemUTF8(input.Semantic); names.Add(name);
                uint slot, format; int divisor = 0, offset = 0;
                if (a.IsZero)
                {
                    if (zero == null)
                    {
                        zero = _renderer.ZeroVertexBuffer;
                    }
                    // A stride-zero float4/uint4/int4 supplies ALL four zero
                    // components, including W, independent of vertex/instance ID.
                    slot = (uint)vertexList.Count;
                    vertexList.Add(new() { Resource = _renderer.Device.ResourceId(zero), Bytes = 16 });
                    format = input.ComponentType switch { 1 => 3, 2 => 4, 3 => 2, _ => throw Unsupported("vertex component type") };
                }
                else
                {
                    if ((uint)a.BufferIndex >= _vertices.Length) throw new ArgumentException("Missing vertex buffer slot.");
                    var v = _vertices[a.BufferIndex]; divisor = v.Divisor; offset = a.Offset; format = VertexFormat(a.Format);
                    if (!remappedSlots.TryGetValue(a.BufferIndex, out slot))
                    {
                        slot = (uint)vertexList.Count; remappedSlots.Add(a.BufferIndex, slot);
                        vertexList.Add(new() { Resource = _renderer.Device.ResourceId(_renderer.Buffer(v.Buffer.Handle, v.Buffer.Offset, v.Buffer.Size)),
                            Offset = checked((ulong)v.Buffer.Offset), Bytes = checked((uint)v.Buffer.Size), Stride = checked((uint)v.Stride) });
                    }
                }
                attributes[i] = new() { Name = name, Index = input.Index, Format = format, Slot = slot,
                    Offset = checked((uint)offset), Classification = divisor == 0 ? 0u : 1, Divisor = checked((uint)divisor) };
            }
            var vertices = vertexList.ToArray();
            // A splash can repeat one shader thousands of times. Sample distinct
            // programs instead of exhausting the diagnostic budget on that splash.
            bool doDiag = BindingDiag && ec >= BindingDiagStartDraw && _bindingDiagPrograms.Count < 128 &&
                _bindingDiagPrograms.Add(_program);
            bool logBindingWindow = BindingDiag && BindingDiagWindowDraws > 0 &&
                ec >= BindingDiagStartDraw && ec - BindingDiagStartDraw < BindingDiagWindowDraws;
            if (doDiag || logBindingWindow)
            {
                string hdr = $"\n[Draw #{ec}] target={_target?.Width}x{_target?.Height} prog={_program.GetHashCode():X8}\n";
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, "binding_diag.txt"), hdr); } catch { }
            }
            var bindings = Bindings(doDiag || logBindingWindow);
            if (doDiag)
            {
                // Bounded, opt-in readback: distinguish a resolved descriptor
                // from an all-zero material buffer. Never enable during FPS tests.
                try
                {
                    string prefix = $"draw-{ec}-program-{_program.GetHashCode():X8}";
                    var stateDump = new System.Text.StringBuilder();
                    stateDump.AppendLine($"indexed={indexed} count={count} instances={instances} first={first} baseVertex={baseVertex} firstInstance={firstInstance} topology={_topology} indexType={_indexType}");
                    stateDump.AppendLine($"cull={draw.Rasterizer.Cull} frontCcw={draw.Rasterizer.FrontCcw} depthTarget={_depthTarget?.Info.Format} depth={_depthState.Comparison} viewport={draw.Viewport.Width}x{draw.Viewport.Height}");
                    for (int vi = 0; vi < _vertices.Length; vi++)
                    {
                        var vertex = _vertices[vi];
                        stateDump.AppendLine($"vertex{vi}: stride={vertex.Stride} divisor={vertex.Divisor} offset={vertex.Buffer.Offset} size={vertex.Buffer.Size}");
                        if (vertex.Buffer.Handle == BufferHandle.Null || vertex.Buffer.Size <= 0) continue;
                        using var data = _renderer.GetBufferData(vertex.Buffer.Handle, vertex.Buffer.Offset, Math.Min(vertex.Buffer.Size, 65536));
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(LogDir, $"{prefix}-vertex{vi}.bin"), data.Get().ToArray());
                    }
                    for (int ai = 0; ai < _attributes.Length; ai++) stateDump.AppendLine($"attribute{ai}: {_attributes[ai]}");
                    if (indexed && _index.Handle != BufferHandle.Null && _index.Size > 0)
                    {
                        using var data = _renderer.GetBufferData(_index.Handle, _index.Offset, Math.Min(_index.Size, 65536));
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(LogDir, $"{prefix}-indices.bin"), data.Get().ToArray());
                    }
                    foreach (var entry in _textures.Take(16))
                    {
                        var texture = entry.Value.Texture;
                        if (texture == null) continue;
                        stateDump.AppendLine($"texture{entry.Key}: {texture.Info} mapping={texture.ComponentMapping}");
                        if (_bindingDiagPrograms.Count <= 8 && (long)texture.Width * texture.Height <= 2097152 &&
                            texture.Info.Format is Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb)
                        {
                            using var data = texture.GetData(0, 0);
                            System.IO.File.WriteAllBytes(System.IO.Path.Combine(LogDir, $"{prefix}-texture-{entry.Key.Item1}-{entry.Key.Item2}.bin"), data.Get().ToArray());
                        }
                    }
                    System.IO.File.WriteAllText(System.IO.Path.Combine(LogDir, $"{prefix}-state.txt"), stateDump.ToString());
                    foreach (var shader in D3D12Program.Decode(_program.GetBinary()))
                        if (!string.IsNullOrEmpty(shader.Hlsl))
                            System.IO.File.WriteAllText(System.IO.Path.Combine(LogDir, $"{prefix}-{shader.Stage}.hlsl"), shader.Hlsl);
                    foreach (var b in _program.Bindings)
                    {
                        if (b.Type != 2 || !_uniforms.TryGetValue((int)b.Binding, out var range) || range.Size <= 0) continue;
                        int size = Math.Min(range.Size, 65536);
                        using var data = _renderer.GetBufferData(range.Handle, range.Offset, size);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(LogDir, $"{prefix}-{b.Stage}-b{b.Binding}.bin"), data.Get().ToArray());
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"D3D12 binding diagnostic failed: {ex.Message}");
                }
            }
            uint proxyMask = DiagnosticSkipAttachmentProxy ? 0 : PrepareAttachmentProxies(ref draw);
            if (proxyMask != 0) System.Threading.Interlocked.Increment(ref _attachmentProxyDraws);
            _renderer.ReserveDescriptors(_program, draw.ColorTargetCount != 0, _depthTarget != null);
            _renderer.BeforeGuestDraw(_feedback);
            fixed (NativeVertexBuffer* v = vertices)
            fixed (NativeAttribute* a = attributes)
            {
                draw.Vertices = v; draw.VertexCount = (uint)vertices.Length; draw.Attributes = a; draw.AttributeCount = (uint)attributes.Length;
                var depth = _depthState;
                depth.Target = _depthTarget == null ? 0 : _renderer.Device.ResourceId(_depthTarget.Native);
                depth.NoColorTarget = draw.ColorTargetCount == 0 && _depthTarget != null ? 1u : 0u;
                _renderer.Device.Draw(_program, bindings, ref draw, depth);
            }
            for (int i = 0; i < _attachmentProxies.Length; i++)
            {
                if ((proxyMask & (1u << i)) == 0) continue;
                var proxy = _attachmentProxies[i];
                _renderer.Device.CopyTexture(proxy, _colorTargets[i].Native, 0, 0, 0, 0, proxy.Width, proxy.Height);
            }
            if (ProbeTargets && _target != null && _target.Width >= 1280 && _target.Height >= 720)
            {
                int phase = ec >= 20000 ? 3 : ec >= 5000 ? 2 : ec >= 1000 ? 1 : 0;
                var key = (_target.Info.Format, phase);
                if (_targetProbes.Count < 24 && _targetProbes.Add(key))
                {
                    try
                    {
                        using var pixels = _target.GetData(0, 0);
                        var bytes = pixels.Get();
                        int colored = 0;
                        for (int i = 0; i + 3 < bytes.Length; i += 64)
                            if ((bytes[i] | bytes[i + 1] | bytes[i + 2]) != 0) colored++;
                        string sourceIds = string.Join(',', bindings.Where(b => b.Type == 0).Take(8)
                            .Select(b => $"{b.Stage}:{b.Space}:{b.Binding}={b.Resource?.Id}"));
                        string result = $"[D3D12 target probe] draw={ec} phase={phase} format={key.Format} size={_target.Width}x{_target.Height} colored={colored}/{bytes.Length / 64} " +
                            $"mask={draw.Blend.Targets[0].Mask} cull={draw.Rasterizer.Cull} frontCCW={draw.Rasterizer.FrontCcw} " +
                            $"vp=({draw.Viewport.X},{draw.Viewport.Y},{draw.Viewport.Width},{draw.Viewport.Height}) " +
                            $"scissor=({draw.Scissor.Left},{draw.Scissor.Top},{draw.Scissor.Right},{draw.Scissor.Bottom}) " +
                            $"textures=[{sourceIds}]";
                        Logger.Warning?.Print(LogClass.Gpu, result);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, "d3d12-target-probe.log"), result + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        string result = $"[D3D12 target probe] draw={ec} format={key.Format} readback failed: {ex.Message}";
                        Logger.Warning?.Print(LogClass.Gpu, result);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, "d3d12-target-probe.log"), result + Environment.NewLine);
                    }
                }
            }
        }
        finally { foreach (nint name in names) Marshal.FreeCoTaskMem(name); }
    }
    private void FallbackDraw(bool indexed, int count, int instances, int first, int baseVertex, int firstInstance,
        BufferRange indirect, BufferRange counter, int max, int stride)
    {
        // Correctness fallback for non-DX topologies/8-bit indices. It performs
        // explicit readback only here; normal indexed/indirect draws stay on GPU.
        if (max > 0)
        {
            int argumentSize = indexed ? 20 : 16;
            if (max > 4096 || stride < argumentSize || (stride & 3) != 0 || (long)max * stride > indirect.Size)
                throw new ArgumentOutOfRangeException(nameof(max));
            if (counter.Size > 0)
            {
                if (counter.Size < 4) throw new ArgumentException("Indirect count range is too small.");
                using var counterData = _renderer.GetBufferData(counter.Handle, counter.Offset, 4);
                max = (int)Math.Min((uint)max, MemoryMarshal.Read<uint>(counterData.Get()));
            }
            using var arguments = _renderer.GetBufferData(indirect.Handle, indirect.Offset, checked(max * stride));
            for (int i = 0; i < max; ++i)
            {
                var a = MemoryMarshal.Cast<byte, uint>(arguments.Get().Slice(i * stride, argumentSize));
                FallbackDraw(indexed, checked((int)a[0]), checked((int)a[1]), checked((int)a[2]),
                    indexed ? unchecked((int)a[3]) : 0, checked((int)a[indexed ? 4 : 3]), default, default, 0, 0);
            }
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(count); ArgumentOutOfRangeException.ThrowIfNegative(instances);
        ArgumentOutOfRangeException.ThrowIfNegative(first); ArgumentOutOfRangeException.ThrowIfNegative(firstInstance);
        if (count == 0 || instances == 0) return; // A zero-sized draw has no primitives or shader invocations.
        if (count > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(count), "Expanded draw exceeds bounded fallback storage.");
        var input = new uint[count];
        if (indexed)
        {
            int elementSize = _indexType switch { IndexType.UByte => 1, IndexType.UShort => 2, IndexType.UInt => 4, _ => throw new ArgumentOutOfRangeException() };
            if ((long)(first + (long)count) * elementSize > _index.Size) throw new ArgumentException("Index range too small.");
            using var bytes = _renderer.GetBufferData(_index.Handle, checked(_index.Offset + first * elementSize), checked(count * elementSize));
            for (int i = 0; i < count; ++i) input[i] = elementSize switch
            {
                1 => bytes.Get()[i], 2 => MemoryMarshal.Read<ushort>(bytes.Get().Slice(i * 2)),
                _ => MemoryMarshal.Read<uint>(bytes.Get().Slice(i * 4)),
            };
        }
        else for (int i = 0; i < count; ++i) input[i] = (uint)i;
        var expanded = D3D12Topology.Expand(_topology, input, _restart && indexed, _restartIndex, _patchVertices);
        if (expanded.Indices.Length == 0) return;
        BufferHandle buffer = _renderer.CreateBuffer(checked(expanded.Indices.Length * 4));
        var oldIndex = _index; var oldType = _indexType; var oldTopology = _topology; bool oldRestart = _restart;
        try
        {
            _renderer.SetBufferData(buffer, 0, MemoryMarshal.AsBytes(expanded.Indices.AsSpan()));
            _index = new(buffer, 0, expanded.Indices.Length * 4); _indexType = IndexType.UInt;
            _topology = expanded.Topology; _restart = false;
            Execute(true, expanded.Indices.Length, instances, 0, indexed ? baseVertex : first, firstInstance);
        }
        finally { _index = oldIndex; _indexType = oldType; _topology = oldTopology; _restart = oldRestart; _renderer.DeleteBuffer(buffer); }
    }
    private static uint Topology(PrimitiveTopology topology, int vertices) => topology switch
    {
        PrimitiveTopology.Points => 1, PrimitiveTopology.Lines => 2, PrimitiveTopology.LineStrip => 3,
        PrimitiveTopology.Triangles => 4, PrimitiveTopology.TriangleStrip => 5,
        PrimitiveTopology.LinesAdjacency => 10, PrimitiveTopology.LineStripAdjacency => 11,
        PrimitiveTopology.TrianglesAdjacency => 12, PrimitiveTopology.TriangleStripAdjacency => 13,
        PrimitiveTopology.Patches => checked((uint)(32 + vertices)), _ => throw Unsupported($"topology {topology}; index expansion required"),
    };
    private static uint VertexFormat(Format f) => f switch
    {
        Format.R32G32B32A32Float => 2, Format.R32G32B32A32Uint => 3, Format.R32G32B32A32Sint => 4,
        Format.R32G32B32Float => 6, Format.R32G32B32Uint => 7, Format.R32G32B32Sint => 8,
        Format.R16G16B16A16Float => 10, Format.R16G16B16A16Unorm => 11, Format.R16G16B16A16Uint => 12, Format.R16G16B16A16Snorm => 13, Format.R16G16B16A16Sint => 14,
        Format.R32G32Float => 16, Format.R32G32Uint => 17, Format.R32G32Sint => 18,
        Format.R10G10B10A2Unorm or Format.B10G10R10A2Unorm or Format.R10G10B10A2Snorm or Format.R10G10B10A2Uscaled or Format.R10G10B10A2Sscaled => 24,
        Format.R10G10B10A2Uint or Format.R10G10B10A2Sint => 25,
        Format.R11G11B10Float => 26,
        Format.R8G8B8A8Unorm => 28, Format.R8G8B8A8Uint => 30, Format.R8G8B8A8Snorm => 31, Format.R8G8B8A8Sint => 32,
        Format.A8B8G8R8Uint => 30,
        Format.B8G8R8A8Unorm => 87,
        Format.R16G16Float => 34, Format.R16G16Unorm => 35, Format.R16G16Uint => 36, Format.R16G16Snorm => 37, Format.R16G16Sint => 38,
        Format.R32Float => 41, Format.R32Uint => 42, Format.R32Sint => 43,
        Format.R8G8Unorm => 49, Format.R8G8Uint => 50, Format.R8G8Snorm => 51, Format.R8G8Sint => 52,
        Format.R16Float => 54, Format.R16Unorm => 56, Format.R16Uint => 57, Format.R16Snorm => 58, Format.R16Sint => 59,
        Format.R8Unorm => 61, Format.R8Uint => 62, Format.R8Snorm => 63, Format.R8Sint => 64,
        Format.R8Uscaled => 61, Format.R8Sscaled => 63,
        Format.R16Uscaled => 56, Format.R16Sscaled => 58,
        Format.R32Uscaled => 42, Format.R32Sscaled => 43,
        Format.R8G8Uscaled => 49, Format.R8G8Sscaled => 51,
        Format.R16G16Uscaled => 35, Format.R16G16Sscaled => 37,
        Format.R32G32Uscaled => 17, Format.R32G32Sscaled => 18,
        Format.R32G32B32Uscaled => 7, Format.R32G32B32Sscaled => 8,
        Format.R8G8B8A8Uscaled => 28, Format.R8G8B8A8Sscaled => 31,
        Format.R16G16B16A16Uscaled => 11, Format.R16G16B16A16Sscaled => 13,
        Format.R32G32B32A32Uscaled => 3, Format.R32G32B32A32Sscaled => 4,
        _ => throw Unsupported($"vertex format {f}"),
    };
    public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF src, Extents2DF dst)
    {
        if (sampler is not D3D12Sampler s) throw new ArgumentException("Foreign sampler.");
        if (_target != null && _target.Width == 1920 && _target.Height == 1080)
        {
            int dw = (int)MathF.Round(MathF.Abs(dst.X2 - dst.X1));
            int dh = (int)MathF.Round(MathF.Abs(dst.Y2 - dst.Y1));
            if (dw > Last1080pRenderWidth) Last1080pRenderWidth = dw;
            if (dh > Last1080pRenderHeight) Last1080pRenderHeight = dh;
        }
        _renderer.Blit(_renderer.Texture(texture), _target, src, dst, (s.Descriptor.Filter & 4) != 0, s.Descriptor);
    }
    public void SetRenderTargets(Span<ITexture> colors, ITexture depth)
    {
        if (colors.Length > 8) throw Unsupported("more than eight render targets");
        var targets = new D3D12GalTexture[colors.Length];
        D3D12GalTexture firstColor = null;
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors[i] == null) continue;
            targets[i] = _renderer.Texture(colors[i]);
            if (targets[i].Native.Format.IsDepth) throw new ArgumentException("A depth texture cannot be a color attachment.", nameof(colors));
            firstColor ??= targets[i];
            if (targets[i] != firstColor && (targets[i].Width != firstColor.Width || targets[i].Height != firstColor.Height))
                throw new ArgumentException("All color attachments must have identical dimensions.", nameof(colors));
        }
        var color = firstColor;
        var depthTexture = depth == null ? null : _renderer.Texture(depth);
        if (depthTexture != null && !depthTexture.Native.Format.IsDepth)
            throw new ArgumentException("Color/depth attachment format mismatch.");
        // D3D12 hardware allows color and depth render targets with different dimensions;
        // the GPU renders into the intersection of their extents. Some titles (e.g. Pokémon
        // Let's Go) intentionally bind a smaller depth buffer alongside a full-resolution
        // color target. Throwing here was incorrect — log a one-time diagnostic instead.
        if (depthTexture != null && color != null && (color.Width != depthTexture.Width || color.Height != depthTexture.Height))
        {
            // Sample at powers of two; this path fires on nearly every draw in
            // some titles and previously wrote tens of MB of repeated text.
            long mismatch = ++_targetDimensionMismatchCount;
            if (mismatch <= 4 || (mismatch & (mismatch - 1)) == 0)
            {
                try
                {
                    string mismatchLine = $"[SetRenderTargets] Dimension mismatch sample={mismatch}: color={color.Width}x{color.Height} depth={depthTexture.Width}x{depthTexture.Height}\n";
                    System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, "draw_targets.txt"), mismatchLine);
                }
                catch { }
            }
        }
        int retained = 0;
        try
        {
            foreach (var target in targets) { target?.RetainBinding(); ++retained; }
            depthTexture?.RetainBinding();
        }
        catch
        {
            for (int i = 0; i < retained; ++i) targets[i]?.ReleaseBinding();
            throw;
        }
        foreach (var target in _colorTargets) target?.ReleaseBinding();
        _depthTarget?.ReleaseBinding();
        _colorTargets = targets; _target = color; _depthTarget = depthTexture;
        if (ProbeTargets && color != _lastReportedTarget)
        {
            _lastReportedTarget = color;
            try
            {
                string targetDesc = color != null ? $"{color.Width}x{color.Height} fmt={color.Info.Format} id={color.Native?.Id}" : "null";
                string depthDesc = depthTexture != null ? $"{depthTexture.Width}x{depthTexture.Height} fmt={depthTexture.Info.Format} id={depthTexture.Native?.Id}" : "null";
                string line = $"[SetRenderTarget] target={targetDesc}, depth={depthDesc}\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(D3D12Pipeline.LogDir, "draw_targets.txt"), line);
            }
            catch {}
        }
    }
    private D3D12GalTexture _lastReportedTarget;
    private long _targetDimensionMismatchCount;
    private long _attachmentProxyDraws;
    private long _negativeViewportCalls;
    private long _positiveViewportCalls;
    internal long TargetDimensionMismatchCount => _targetDimensionMismatchCount;
    internal long AttachmentProxyDraws => System.Threading.Interlocked.Read(ref _attachmentProxyDraws);
    internal long NegativeViewportCalls => System.Threading.Interlocked.Read(ref _negativeViewportCalls);
    internal long PositiveViewportCalls => System.Threading.Interlocked.Read(ref _positiveViewportCalls);
    internal float LastGuestViewportSignedHeight { get; private set; }
    internal float LastGuestViewportDepthNear { get; private set; }
    internal float LastGuestViewportDepthFar { get; private set; }
    public void SetRenderTargetColorMasks(ReadOnlySpan<uint> masks)
    {
        if (masks.Length > 8) throw Unsupported("more than eight render-target masks");
        for (int i = 0; i < 8; i++) _state.Blend.Targets[i].Mask = i < masks.Length ? checked((byte)masks[i]) : (byte)0;
    }

    private (int Width, int Height) GetActiveTargetSize()
    {
        if (_target != null)
        {
            return (_target.Width, _target.Height);
        }

        if (_depthTarget != null)
        {
            return (_depthTarget.Width, _depthTarget.Height);
        }

        var info = _renderer.Device.GetInfo();
        return (checked((int)info.Width), checked((int)info.Height));
    }

    private void SetFullScissor()
    {
        var (width, height) = GetActiveTargetSize();
        _state.Scissor = new NativeRect { Left = 0, Top = 0, Right = width, Bottom = height };
    }

    private void SetScissor(Rectangle<int> region)
    {
        if (region.Width < 0 || region.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Scissor dimensions must not be negative.");
        }

        // GAL uses this canonical rectangle for a disabled scissor. D3D12 has
        // no dynamic scissor-enable bit, so represent it by the full target.
        if ((region.X | region.Y) == 0 && region.Width == 0xffff && region.Height == 0xffff)
        {
            SetFullScissor();
            return;
        }

        _state.Scissor = new NativeRect
        {
            Left = region.X,
            Top = region.Y,
            Right = checked(region.X + region.Width),
            Bottom = checked(region.Y + region.Height),
        };
    }

    public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
    {
        // This backend honestly advertises no multiview support. Mirror the
        // Vulkan single-view fallback: consume viewport/scissor zero and do
        // not turn extra inactive entries from a guest state update into a
        // fatal error. An empty list explicitly disables clipping.
        if (regions.IsEmpty)
        {
            SetFullScissor();
            return;
        }

        SetScissor(regions[0]);
    }
    public void SetViewports(ReadOnlySpan<Viewport> viewports)
    {
        if (viewports.IsEmpty)
        {
            var (fallbackWidth, fallbackHeight) = GetActiveTargetSize();
            _state.Viewport = new NativeViewport { Width = fallbackWidth, Height = fallbackHeight, MaxDepth = 1 };
            LastGuestViewportSignedHeight = fallbackHeight;
            LastGuestViewportDepthNear = 0f;
            LastGuestViewportDepthFar = 1f;
            return;
        }

        var v = viewports[0];
        LastGuestViewportSignedHeight = v.Region.Height;
        if (v.Region.Height < 0f) System.Threading.Interlocked.Increment(ref _negativeViewportCalls);
        else System.Threading.Interlocked.Increment(ref _positiveViewportCalls);
        LastGuestViewportDepthNear = v.DepthNear;
        LastGuestViewportDepthFar = v.DepthFar;
        if (v.SwizzleX != ViewportSwizzle.PositiveX || v.SwizzleY != ViewportSwizzle.PositiveY || v.SwizzleZ != ViewportSwizzle.PositiveZ || v.SwizzleW != ViewportSwizzle.PositiveW)
            throw Unsupported("viewport swizzle");

        // Maxwell/GAL state may carry a flipped extent or a depth interval
        // outside D3D12's legal [0, 1] range. Preserve the covered rectangle
        // and depth ordering while converting it to the native viewport
        // contract; forwarding those values makes RSSetViewports reject the
        // subsequent draw with E_INVALIDARG.
        var (targetWidth, targetHeight) = GetActiveTargetSize();
        float x = float.IsFinite(v.Region.X) ? v.Region.X : 0f;
        float y = float.IsFinite(v.Region.Y) ? v.Region.Y : 0f;
        float width = float.IsFinite(v.Region.Width) ? v.Region.Width : targetWidth;
        float height = float.IsFinite(v.Region.Height) ? v.Region.Height : targetHeight;

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

        float minDepth = float.IsFinite(v.DepthNear) ? Math.Clamp(v.DepthNear, 0f, 1f) : 0f;
        float maxDepth = float.IsFinite(v.DepthFar) ? Math.Clamp(v.DepthFar, 0f, 1f) : 1f;

        if (maxDepth < minDepth)
        {
            (minDepth, maxDepth) = (maxDepth, minDepth);
        }

        x = Math.Clamp(x, -32768f, 32767f);
        y = Math.Clamp(y, -32768f, 32767f);
        width = Math.Clamp(width <= 0f ? targetWidth : width, 0.0001f, 16384f);
        height = Math.Clamp(height <= 0f ? targetHeight : height, 0.0001f, 16384f);

        _state.Viewport = new() { X = x, Y = y, Width = width, Height = height, MinDepth = minDepth, MaxDepth = maxDepth };
    }
    public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> attributes) => _attributes = attributes.ToArray();
    public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertices) => _vertices = vertices.ToArray();
    public void SetIndexBuffer(BufferRange buffer, IndexType type) { _index = buffer; _indexType = type; }
    public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers) { foreach (var b in buffers) _uniforms[b.Binding] = b.Range; }
    public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers) { foreach (var b in buffers) _storage[b.Binding] = b.Range; }
    public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
    {
        if (sampler != null && sampler is not D3D12Sampler) throw new ArgumentException("Foreign sampler.");
        var next = texture == null ? null : _renderer.Texture(texture);
        next?.RetainBinding();
        _textures.TryGetValue((stage, binding), out var previous);
        _textures[(stage, binding)] = (next, sampler as D3D12Sampler);
        previous.Item1?.ReleaseBinding();
    }
    public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array) => _arrays[(stage, binding)] = array as D3D12TextureArray ?? throw new ArgumentException("Foreign array.");
    public void SetTextureArraySeparate(ShaderStage stage, int set, ITextureArray array) => _separateArrays[(stage, set)] = array as D3D12TextureArray ?? throw new ArgumentException("Foreign array.");
    public void SetImage(ShaderStage stage, int binding, ITexture texture)
    {
        var next = texture == null ? null : _renderer.Texture(texture);
        next?.RetainBinding();
        _imageTextures.TryGetValue((stage, binding), out var previous);
        _imageTextures[(stage, binding)] = next;
        previous?.ReleaseBinding();
    }
    public void SetImageArray(ShaderStage stage, int binding, IImageArray array) => _images[(stage, binding)] = array as D3D12ImageArray ?? throw new ArgumentException("Foreign image array.");
    public void SetImageArraySeparate(ShaderStage stage, int set, IImageArray array) => _separateImages[(stage, set)] = array as D3D12ImageArray ?? throw new ArgumentException("Foreign image array.");
    public void SetPrimitiveTopology(PrimitiveTopology topology) => _topology = topology;
    public void SetPatchParameters(int vertices, ReadOnlySpan<float> outer, ReadOnlySpan<float> inner)
    {
        if (vertices is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(vertices));
        if ((!outer.IsEmpty && outer.Length != 4) || (!inner.IsEmpty && inner.Length != 2))
            throw new ArgumentException("Tessellation defaults require four outer and two inner factors.");
        // GAL sends these even without an active patch draw. With a hull shader,
        // its output factors supersede the stored guest defaults. A draw needing
        // default-factor synthesis is rejected in Execute, not at state update.
        outer.CopyTo(_patchOuter); inner.CopyTo(_patchInner);
        _patchVertices = vertices;
    }
    public void SetFaceCulling(bool enable, Face face)
    {
        if (face is not (Face.Front or Face.Back or Face.FrontAndBack)) throw new ArgumentOutOfRangeException(nameof(face));
        _cullEnable = enable;
        _cullFace = face;
        _state.Rasterizer.Cull = !enable ? 1u : face switch
        {
            Face.Front => 2u,
            Face.Back => 3u,
            Face.FrontAndBack => 1u,
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }
    public void SetFrontFace(FrontFace face) => _state.Rasterizer.FrontCcw = face switch
    {
        FrontFace.CounterClockwise => 1u,
        FrontFace.Clockwise => 0u,
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };
    internal NativeRasterizer RasterizerState => _state.Rasterizer;
    public void SetPolygonMode(PolygonMode front, PolygonMode back)
    {
        if (front is not (PolygonMode.Point or PolygonMode.Line or PolygonMode.Fill) ||
            back is not (PolygonMode.Point or PolygonMode.Line or PolygonMode.Fill))
            throw new ArgumentOutOfRangeException(nameof(front));
        if (front != back || front == PolygonMode.Point) throw Unsupported("asymmetric/point polygon modes");
        _polygonMode = front;
        _state.Rasterizer.Fill = front == PolygonMode.Line ? 2u : 3;
        UpdateDepthBias();
    }
    public void SetDepthBias(PolygonModeMask modes, float factor, float units, float clamp)
    {
        if ((modes & ~(PolygonModeMask.Point | PolygonModeMask.Line | PolygonModeMask.Fill)) != 0)
            throw new ArgumentOutOfRangeException(nameof(modes));
        if (!float.IsFinite(factor) || !float.IsFinite(units) || !float.IsFinite(clamp))
            throw new ArgumentOutOfRangeException(nameof(factor), "Depth bias values must be finite.");
        // D3D12 stores the constant bias as an integer number of depth units.
        // Truncation would turn fractional guest offsets (GAL halves the
        // Maxwell register) such as 0.5 into no bias at all.
        int roundedUnits = checked((int)MathF.Round(units, MidpointRounding.AwayFromZero));
        _depthBiasModes = modes;
        _depthBiasFactor = factor;
        _depthBiasUnits = roundedUnits;
        _depthBiasClamp = clamp;
        UpdateDepthBias();
    }
    private void UpdateDepthBias()
    {
        var mask = _polygonMode switch
        {
            PolygonMode.Point => PolygonModeMask.Point,
            PolygonMode.Line => PolygonModeMask.Line,
            _ => PolygonModeMask.Fill,
        };

        if (_depthBiasModes.HasFlag(mask))
        {
            _state.Rasterizer.DepthBias = _depthBiasUnits;
            _state.Rasterizer.DepthBiasClamp = _depthBiasClamp;
            _state.Rasterizer.SlopeBias = _depthBiasFactor;
        }
        else
        {
            _state.Rasterizer.DepthBias = 0;
            _state.Rasterizer.DepthBiasClamp = 0;
            _state.Rasterizer.SlopeBias = 0;
        }
    }
    public void SetDepthClamp(bool clamp) => _state.Rasterizer.DepthClip = clamp ? 0u : 1;
    public void SetDepthMode(DepthMode mode) => _depthMode = mode; // Applied by the shared shader translator.
    public void SetDepthTest(DepthTestDescriptor depth)
    {
        uint comparison = depth.TestEnable ? D3D12Sampler.Comparison(depth.Func) : 8;
        _depthState.Enable = depth.TestEnable ? 1u : 0;
        _depthState.Write = depth.WriteEnable ? 1u : 0;
        _depthState.Comparison = comparison;
    }
    public void SetStencilTest(StencilTestDescriptor stencil)
    {
        var state = _depthState;
        state.StencilEnable = stencil.TestEnable ? 1u : 0u;
        if (!stencil.TestEnable)
        {
            _depthState = state;
            return;
        }

        state.FrontFail = StencilOperation(stencil.FrontSFail);
        state.FrontPass = StencilOperation(stencil.FrontDpPass);
        state.FrontDepthFail = StencilOperation(stencil.FrontDpFail);
        state.FrontFunc = D3D12Sampler.Comparison(stencil.FrontFunc);
        state.FrontRef = unchecked((uint)stencil.FrontFuncRef) & 255;
        state.FrontReadMask = unchecked((uint)stencil.FrontFuncMask) & 255;
        state.FrontWriteMask = unchecked((uint)stencil.FrontMask) & 255;

        state.BackFail = StencilOperation(stencil.BackSFail);
        state.BackPass = StencilOperation(stencil.BackDpPass);
        state.BackDepthFail = StencilOperation(stencil.BackDpFail);
        state.BackFunc = D3D12Sampler.Comparison(stencil.BackFunc);
        state.BackRef = unchecked((uint)stencil.BackFuncRef) & 255;
        state.BackReadMask = unchecked((uint)stencil.BackFuncMask) & 255;
        state.BackWriteMask = unchecked((uint)stencil.BackMask) & 255;
        _depthState = state;
    }
    private static uint StencilOperation(StencilOp operation) => operation switch
    {
        StencilOp.Keep or StencilOp.KeepGl => 1u,
        StencilOp.Zero or StencilOp.ZeroGl => 2u,
        StencilOp.Replace or StencilOp.ReplaceGl => 3u,
        StencilOp.IncrementAndClamp or StencilOp.IncrementAndClampGl => 4u,
        StencilOp.DecrementAndClamp or StencilOp.DecrementAndClampGl => 5u,
        StencilOp.Invert or StencilOp.InvertGl => 6u,
        StencilOp.IncrementAndWrap or StencilOp.IncrementAndWrapGl => 7u,
        StencilOp.DecrementAndWrap or StencilOp.DecrementAndWrapGl => 8u,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
    public void SetLineParameters(float width, bool smooth)
    { if (width != 1) throw Unsupported("wide lines"); _state.Rasterizer.AntialiasedLine = smooth ? 1u : 0; }
    public void SetMultisampleState(MultisampleDescriptor state)
    {
        // Alpha-to-coverage is a native D3D12 blend-state flag and must be
        // preserved in the PSO key/descriptor. D3D12 exposes neither a
        // fixed-function alpha-to-coverage dither nor alpha-to-one control.
        // The established D3D11 compatibility behaviour is to preserve the
        // native alpha-to-coverage bit and leave those guest hints to the
        // shader path when a title needs them. Treating them as a fatal state
        // request prevents otherwise valid draws (Rivals uses both during
        // boot) and is less accurate than the existing D3D11 fallback.
        _state.Blend.AlphaCoverage = state.AlphaToCoverageEnable ? 1u : 0u;
    }
    public void SetPointParameters(float size, bool programSize, bool sprite, Origin origin)
    {
        // D3D12 has no fixed-function point-size or sprite-coordinate state.
        // The GAL shader translation supplies point expansion/coordinates just
        // as the established D3D11 path does, so do not reject a legal guest
        // point-state update merely because there is no rasterizer field to
        // mirror it into. The guest can also write an unused transitional
        // value here; D3D11 deliberately accepts it. The actual draw topology
        // remains validated below.
    }
    // GpuAccessor and DiskCacheGpuAccessor request shader alpha-test on D3D12.
    // EmitterContext emits the comparison/discard; ShaderSpecializationState
    // invalidates the program when enable/reference/compare changes.
    public void SetAlphaTest(bool enable, float reference, CompareOp op)
    {
        if (enable && !float.IsFinite(reference)) throw new ArgumentOutOfRangeException(nameof(reference));
        _alphaTest = enable;
    }
    public void SetRasterizerDiscard(bool discard) => _discard = discard;
    public void SetPrimitiveRestart(bool enable, int index) { _restart = enable; _restartIndex = unchecked((uint)index); }
    public void SetUserClipDistance(int index, bool enable) { if (enable) throw Unsupported("user clip-distance mask"); }
    public void BeginTransformFeedback(PrimitiveTopology topology)
    {
        if (_feedback) throw new InvalidOperationException("Transform feedback already active.");
        // Unlike OpenGL, D3D12 derives the capture primitive family from the
        // bound shader/topology. GAL passes strips, adjacency and patches too.
        if (!Enum.IsDefined(topology)) throw new ArgumentOutOfRangeException(nameof(topology));
        _renderer.EnsureCommands();
        for (int i = 0; i < 4; ++i)
        {
            _feedbackCounters[i] ??= _renderer.Device.CreateBuffer(8);
            _renderer.Device.Upload(_feedbackCounters[i], 0, new byte[8]);
        }
        _feedback = true;
    }
    public void EndTransformFeedback()
    {
        if (!_feedback) throw new InvalidOperationException("No active transform feedback.");
        _renderer.EndFeedbackCounter(); _feedback = false;
    }
    public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
    {
        if (buffers.Length > 4) throw new ArgumentOutOfRangeException(nameof(buffers));
        foreach (var b in buffers)
            if (b.Offset < 0 || b.Size < 0 || (b.Offset & 3) != 0 || (b.Size & 3) != 0) throw new ArgumentException("Unaligned stream-output range.");
        _feedbackBuffers = buffers.ToArray();
    }
    // Preserve the original depth/stencil storage when color is larger. Copying
    // a depth rectangle into a larger proxy is illegal in D3D12 and a depth-only
    // shader copy would lose stencil. Color can be copied exactly, without a
    // resolve or a change of viewport coordinates. Limit this path to 2D 1x
    // attachments; array/MSAA compatibility requires a separate implementation.
    private uint PrepareAttachmentProxies(ref NativeDraw draw)
    {
        if (_depthTarget == null || _discard || draw.ColorTargetCount == 0 ||
            _depthTarget.Info.Samples != 1 || _depthTarget.Info.Target != Target.Texture2D)
            return 0;
        int width = _depthTarget.Width, height = _depthTarget.Height;
        bool needed = false;
        for (int i = 0; i < draw.ColorTargetCount; i++)
        {
            var color = _colorTargets[i];
            if (color == null) continue;
            if (color.Info.Samples != 1 || color.Info.Target != Target.Texture2D) return 0;
            needed |= color.Width > _depthTarget.Width || color.Height > _depthTarget.Height;
            width = Math.Min(width, color.Width);
            height = Math.Min(height, color.Height);
        }
        if (!needed) return 0;
        uint mask = 0;
        for (int i = 0; i < draw.ColorTargetCount; i++)
        {
            var color = _colorTargets[i];
            if (color == null) continue;
            var proxy = _attachmentProxies[i];
            if (proxy == null || proxy.Width != width || proxy.Height != height || proxy.Format != color.Native.Format)
            {
                var replacement = _renderer.Device.CreateTexture((uint)width, (uint)height, color.Native.Format);
                _attachmentProxies[i] = replacement;
                proxy?.Dispose();
                proxy = replacement;
            }
            _renderer.Device.CopyTexture(color.Native, proxy, 0, 0, 0, 0, (uint)width, (uint)height);
            draw.ColorTargets[i] = _renderer.Device.ResourceId(proxy);
            mask |= 1u << i;
        }
        draw.ColorTarget = draw.ColorTargets[0];
        return mask;
    }

    internal void DisposeResources()
    {
        foreach (var pair in _textures.Values) pair.Item1?.ReleaseBinding();
        _textures.Clear();
        foreach (var image in _imageTextures.Values) image?.ReleaseBinding();
        _imageTextures.Clear();
        foreach (var target in _colorTargets) target?.ReleaseBinding();
        _colorTargets = [];
        _depthTarget?.ReleaseBinding(); _depthTarget = null; _target = null;
        foreach (var counter in _feedbackCounters) counter?.Dispose();
        foreach (var proxy in _attachmentProxies) proxy?.Dispose();
    }
    public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool equal) => false; // GAL performs the counter fallback.
    public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool equal) => false;
    public void EndHostConditionalRendering() => throw new InvalidOperationException("No native conditional-rendering scope was started.");
    public void SetBlendState(AdvancedBlendDescriptor blend) => throw Unsupported("advanced blending");
    private static uint BlendOperation(BlendOp op) => op switch
    {
        BlendOp.Add or BlendOp.AddGl => 1, BlendOp.Subtract or BlendOp.SubtractGl => 2,
        BlendOp.ReverseSubtract or BlendOp.ReverseSubtractGl => 3, BlendOp.Minimum or BlendOp.MinimumGl => 4,
        BlendOp.Maximum or BlendOp.MaximumGl => 5, _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
    private static uint BlendValue(BlendFactor f) => f switch
    {
        BlendFactor.Zero or BlendFactor.ZeroGl => 1, BlendFactor.One or BlendFactor.OneGl => 2,
        BlendFactor.SrcColor or BlendFactor.SrcColorGl => 3, BlendFactor.OneMinusSrcColor or BlendFactor.OneMinusSrcColorGl => 4,
        BlendFactor.SrcAlpha or BlendFactor.SrcAlphaGl => 5, BlendFactor.OneMinusSrcAlpha or BlendFactor.OneMinusSrcAlphaGl => 6,
        BlendFactor.DstAlpha or BlendFactor.DstAlphaGl => 7, BlendFactor.OneMinusDstAlpha or BlendFactor.OneMinusDstAlphaGl => 8,
        BlendFactor.DstColor or BlendFactor.DstColorGl => 9, BlendFactor.OneMinusDstColor or BlendFactor.OneMinusDstColorGl => 10,
        BlendFactor.SrcAlphaSaturate or BlendFactor.SrcAlphaSaturateGl => 11,
        BlendFactor.ConstantColor or BlendFactor.ConstantAlpha => 14,
        BlendFactor.OneMinusConstantColor or BlendFactor.OneMinusConstantAlpha => 15,
        BlendFactor.Src1Color or BlendFactor.Src1ColorGl => 16,
        BlendFactor.OneMinusSrc1Color or BlendFactor.OneMinusSrc1ColorGl => 17,
        BlendFactor.Src1Alpha or BlendFactor.Src1AlphaGl => 18,
        BlendFactor.OneMinusSrc1Alpha or BlendFactor.OneMinusSrc1AlphaGl => 19,
        _ => throw Unsupported($"blend factor {f}"),
    };
    private static bool UsesConstantColor(BlendFactor factor) => factor is BlendFactor.ConstantColor or BlendFactor.OneMinusConstantColor;
    private static bool UsesConstantAlpha(BlendFactor factor) => factor is BlendFactor.ConstantAlpha or BlendFactor.OneMinusConstantAlpha;
    private static uint AlphaBlendValue(BlendFactor factor) => BlendValue(factor) switch
    {
        // D3D12 rejects COLOR factors in the alpha equation; their alpha
        // component is precisely the corresponding ALPHA factor.
        3 => 5, 4 => 6, 9 => 7, 10 => 8, 11 => 2, 16 => 18, 17 => 19,
        var value => value,
    };
    public void SetBlendState(int index, BlendDescriptor blend)
    {
        if ((uint)index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        // Build locally so rejecting a combination cannot leave partial state.
        var target = _state.Blend.Targets[index]; target.Enable = blend.Enable ? 1u : 0;
        var constant = blend.BlendConstant;
        if (blend.Enable)
        {
            if (!float.IsFinite(constant.Red) || !float.IsFinite(constant.Green) || !float.IsFinite(constant.Blue) || !float.IsFinite(constant.Alpha))
                throw new ArgumentException("Non-finite blend constant.", nameof(blend));
            bool alphaRgb = UsesConstantAlpha(blend.ColorSrcFactor) || UsesConstantAlpha(blend.ColorDstFactor);
            bool colorRgb = UsesConstantColor(blend.ColorSrcFactor) || UsesConstantColor(blend.ColorDstFactor);
            // Baseline D3D12 has one RGBA constant. Newer ALPHA_FACTOR support
            // is optional; never assume it exists on the Xbox/UWP runtime.
            if (alphaRgb && colorRgb && (constant.Red != constant.Alpha || constant.Green != constant.Alpha || constant.Blue != constant.Alpha))
                throw Unsupported("simultaneous distinct constant-color and constant-alpha RGB factors");
            if (alphaRgb) constant = new(constant.Alpha, constant.Alpha, constant.Alpha, constant.Alpha);
            target.Source = BlendValue(blend.ColorSrcFactor); target.Destination = BlendValue(blend.ColorDstFactor); target.Operation = BlendOperation(blend.ColorOp);
            target.SourceAlpha = AlphaBlendValue(blend.AlphaSrcFactor); target.DestinationAlpha = AlphaBlendValue(blend.AlphaDstFactor); target.AlphaOperation = BlendOperation(blend.AlphaOp);
        }
        // D3D12 exposes independent blend descriptors for all eight RTV
        // slots. Blend constants themselves are dynamic and global, matching
        // the GAL contract; each state update carries the current value.
        _state.Blend.Targets[index] = target;
        _state.BlendRed = constant.Red; _state.BlendGreen = constant.Green;
        _state.BlendBlue = constant.Blue; _state.BlendAlpha = constant.Alpha;
    }
    public void SetLogicOpState(bool enable, LogicalOp op)
    {
        if (enable)
        {
            throw Unsupported("logic operations on UNORM render targets (Part 2 shader emulation dependency)");
        }
        _state.Blend.Targets[0].LogicEnable = 0;
    }
}
