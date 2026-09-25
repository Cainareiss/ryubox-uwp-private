using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.D3D12;

/// <summary>SPIRV-Cross C API, HLSL 5.1, original descriptor sets as register spaces.</summary>
public sealed unsafe class D3D12SpirvCompiler : IDisposable
{
    private readonly object _sync = new();
    private nint _library;
    private readonly delegate* unmanaged[Cdecl]<nint*, int> _create;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, nuint, nint*, int> _parse;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint*, int> _compiler;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> _options;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, int> _setUint;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, byte, int> _setBool;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int> _install;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> _compile;
    private readonly delegate* unmanaged[Cdecl]<nint, nint> _error;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, uint, int> _entryPoint;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _numWorkgroups;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void> _decoration;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, byte*, void> _name;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, void> _executionMode;

    public D3D12SpirvCompiler(string libraryPath)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        _library = LoadPackagedLibrary(Path.GetFileName(libraryPath), 0);
        if (_library == 0) _library = NativeLibrary.Load(libraryPath);
        try
        {
            _create = (delegate* unmanaged[Cdecl]<nint*, int>)Get("spvc_context_create");
            _destroy = (delegate* unmanaged[Cdecl]<nint, void>)Get("spvc_context_destroy");
            _parse = (delegate* unmanaged[Cdecl]<nint, uint*, nuint, nint*, int>)Get("spvc_context_parse_spirv");
            _compiler = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint*, int>)Get("spvc_context_create_compiler");
            _options = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Get("spvc_compiler_create_compiler_options");
            _setUint = (delegate* unmanaged[Cdecl]<nint, uint, uint, int>)Get("spvc_compiler_options_set_uint");
            _setBool = (delegate* unmanaged[Cdecl]<nint, uint, byte, int>)Get("spvc_compiler_options_set_bool");
            _install = (delegate* unmanaged[Cdecl]<nint, nint, int>)Get("spvc_compiler_install_compiler_options");
            _compile = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Get("spvc_compiler_compile");
            _error = (delegate* unmanaged[Cdecl]<nint, nint>)Get("spvc_context_get_last_error_string");
            _entryPoint = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)Get("spvc_compiler_set_entry_point");
            _numWorkgroups = (delegate* unmanaged[Cdecl]<nint, uint>)Get("spvc_compiler_hlsl_remap_num_workgroups_builtin");
            _decoration = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void>)Get("spvc_compiler_set_decoration");
            _name = (delegate* unmanaged[Cdecl]<nint, uint, byte*, void>)Get("spvc_compiler_set_name");
            _executionMode = (delegate* unmanaged[Cdecl]<nint, uint, void>)Get("spvc_compiler_set_execution_mode");
        }
        catch { NativeLibrary.Free(_library); _library = 0; throw; }
    }

    public D3D12ShaderSource Translate(ShaderSource shader, uint tessellationControlPoints = 0)
        => TranslateLinked(shader, tessellationControlPoints, []);

    private D3D12ShaderSource TranslateLinked(ShaderSource shader, uint tessellationControlPoints, ReadOnlySpan<uint> domainModes)
    {
        if (shader.Language != TargetLanguage.Spirv) throw new NotSupportedException("D3D12 expects GAL SPIR-V shaders.");
        byte[] bytes = shader.BinaryCode;
        if (bytes is null || bytes.Length < 20 || bytes.Length > 4 * 1024 * 1024 || bytes.Length % 4 != 0 ||
            BitConverter.ToUInt32(bytes) != 0x07230203) throw new ArgumentException("Invalid SPIR-V module.");
        uint model = shader.Stage switch
        {
            ShaderStage.Vertex => 0, ShaderStage.TessellationControl => 1, ShaderStage.TessellationEvaluation => 2,
            ShaderStage.Geometry => 3, ShaderStage.Fragment => 4, ShaderStage.Compute => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(shader)),
        };
        // Validate the instruction walk and reflected IDs before entering the
        // native parser. This is bounded preflight, not full spirv-val coverage.
        var streamOutput = D3D12SpirvStreamOutput.Read(bytes);
        if (tessellationControlPoints > 32 || (tessellationControlPoints != 0 && shader.Stage is not (ShaderStage.TessellationEvaluation or ShaderStage.TessellationControl)))
            throw new ArgumentOutOfRangeException(nameof(tessellationControlPoints));
        if (shader.Stage == ShaderStage.TessellationEvaluation && tessellationControlPoints == 0)
            throw new ArgumentException("Domain shader translation requires the linked hull shader output control-point count.");
        if (shader.Stage == ShaderStage.TessellationControl && domainModes.IsEmpty)
            throw new ArgumentException("Hull shader translation requires the linked domain shader execution modes; use CreateProgram.");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_library == 0, this);
            nint context = 0;
            try
            {
                Check(_create(&context), context);
                nint ir = 0, compiler = 0, options = 0, source = 0;
                fixed (byte* data = bytes) Check(_parse(context, (uint*)data, (nuint)(bytes.Length / 4), &ir), context);
                Check(_compiler(context, 2, ir, 1, &compiler), context);
                fixed (byte* entry = "main\0"u8) Check(_entryPoint(compiler, entry, model), context);
                Check(_options(compiler, &options), context);
                Check(_setUint(options, 0x4000000 | 13, 51), context);
                // Direct3D 10+ has no programmable point-size output. SPIRV-Cross
                // can retain valid guest shaders which declare PointSize by
                // intentionally ignoring that output, matching the D3D11 backend.
                Check(_setBool(options, 0x4000000 | 14, 1), context);
                Check(_setBool(options, 0x4000000 | 16, 1), context); // Base vertex/instance, supplied by native draw.
                Check(_setBool(options, 0x4000000 | 90, 1), context); // Preserve entry point name.
                Check(_setBool(options, 0x1000000 | 3, 0), context); // GAL already handles depth conversion.
                if (shader.Stage is ShaderStage.TessellationEvaluation or ShaderStage.TessellationControl)
                    Check(_setUint(options, 0x4000000 | 0xf001, tessellationControlPoints), context);
                if (shader.Stage == ShaderStage.TessellationControl)
                    foreach (uint mode in domainModes) _executionMode(compiler, mode);
                // PointSize is the one intentional D3D compatibility path above.
                // PointCoord and all other unsupported guest semantics must still
                // fail explicitly instead of being silently rewritten.
                Check(_install(compiler, options), context);
                if (shader.Stage == ShaderStage.Compute)
                {
                    uint id = _numWorkgroups(compiler);
                    if (id != 0)
                    {
                        _decoration(compiler, id, 34, 0x7fffffff); // Reserved compiler-owned register space.
                        _decoration(compiler, id, 33, 1);
                        fixed (byte* name = "Ryubox_DispatchInfo\0"u8) _name(compiler, id, name);
                    }
                }
                Check(_compile(compiler, &source), context);
                string hlsl = Marshal.PtrToStringUTF8(source) ?? throw new InvalidOperationException("Empty HLSL.");
                // SPVC's C API lacks set_hlsl_aux_buffer_binding. Reserve an
                // explicit space for this compiler-owned declaration only.
                const string auxiliary = "cbuffer SPIRV_Cross_VertexInfo\n";
                hlsl = hlsl.Replace(auxiliary, "cbuffer SPIRV_Cross_VertexInfo : register(b0, space2147483647)\n", StringComparison.Ordinal);
                if (shader.Stage == ShaderStage.Geometry)
                {
                    hlsl = NormalizeGeometryPerVertexInputs(hlsl);
                    hlsl = NormalizeInputStageInterface(hlsl);
                    hlsl = NormalizeOutputStageInterface(hlsl);
                }
                else if (shader.Stage is ShaderStage.Vertex or ShaderStage.TessellationEvaluation)
                {
                    hlsl = NormalizeOutputStageInterface(hlsl);
                }
                else if (shader.Stage == ShaderStage.Fragment)
                {
                    hlsl = NormalizeInputStageInterface(hlsl);
                }

                if (shader.Stage == ShaderStage.Compute)
                {
                    hlsl = D3D12ComputeSharedNormalizer.Normalize(hlsl);
                }

                if (D3D12Pipeline.ProbeTargets)
                {
                    try { File.WriteAllText(System.IO.Path.Combine(D3D12Pipeline.LogDir, $"last_{shader.Stage}.hlsl"), hlsl); } catch {}
                }
                return new(shader.Stage, hlsl, streamOutput, OriginalSpirv: (byte[])bytes.Clone());
            }
            finally { if (context != 0) _destroy(context); }
        }
    }

    public D3D12Program CreateProgram(D3D12NativeDevice device, ShaderSource[] shaders)
    {
        ArgumentNullException.ThrowIfNull(shaders);
        var sources = new D3D12ShaderSource[shaders.Length];
        uint outputVertices = 0;
        uint[] domainModes = [];
        foreach (var shader in shaders)
        {
            if (shader.Stage is not (ShaderStage.TessellationControl or ShaderStage.TessellationEvaluation)) continue;
            var modes = ReadMainExecutionModes(shader);
            if (shader.Stage == ShaderStage.TessellationControl)
            {
                if (outputVertices != 0 || !modes.TryGetValue(26, out outputVertices) || outputVertices is < 1 or > 32)
                    throw new ArgumentException("Hull shader requires one OutputVertices execution mode in [1,32].");
            }
            else
            {
                if (domainModes.Length != 0) throw new ArgumentException("Duplicate domain shader.");
                var selected = new List<uint>();
                int domains = 0, spacing = 0, order = 0;
                foreach (uint mode in modes.Keys)
                {
                    if (mode is 22 or 24 or 25) domains++;
                    else if (mode is 1 or 2 or 3) spacing++;
                    else if (mode is 4 or 5) order++;
                    else if (mode != 10) continue;
                    selected.Add(mode);
                }
                if (domains != 1 || spacing != 1 || order > 1)
                    throw new ArgumentException("Domain shader requires one domain and spacing execution mode.");
                domainModes = selected.ToArray();
            }
        }
        if ((outputVertices == 0) != (domainModes.Length == 0))
            throw new ArgumentException("Tessellation requires both hull and domain shaders.");
        for (int i = 0; i < sources.Length; ++i)
            sources[i] = TranslateLinked(shaders[i], shaders[i].Stage == ShaderStage.TessellationEvaluation ? outputVertices :
                shaders[i].Stage == ShaderStage.TessellationControl ? 32u : 0, domainModes);
        if (outputVertices != 0) D3D12TessellationLinker.Link(sources, outputVertices);
        return device.CreateProgram(sources);
    }
    private static Dictionary<uint, uint> ReadMainExecutionModes(ShaderSource shader)
    {
        if (shader.Language != TargetLanguage.Spirv || shader.BinaryCode is null || shader.BinaryCode.Length < 20 ||
            shader.BinaryCode.Length % 4 != 0 || BitConverter.ToUInt32(shader.BinaryCode) != 0x07230203)
            throw new ArgumentException("Invalid tessellation SPIR-V module.");
        D3D12SpirvStreamOutput.Read(shader.BinaryCode);
        var words = MemoryMarshal.Cast<byte, uint>(shader.BinaryCode);
        uint entry = 0, model = shader.Stage == ShaderStage.TessellationControl ? 1u : 2u;
        for (int offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            if ((words[offset] & 0xffff) == 15 && (words[offset] >> 16) >= 5 && words[offset + 1] == model &&
                words[offset + 3] == 0x6e69616d && (words[offset + 4] & 255) == 0)
            {
                if (entry != 0) throw new ArgumentException("Ambiguous SPIR-V main entry point.");
                entry = words[offset + 2];
            }
        if (entry == 0) throw new ArgumentException("Missing SPIR-V main entry point.");
        var modes = new Dictionary<uint, uint>();
        for (int offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            if ((words[offset] & 0xffff) == 16 && (words[offset] >> 16) >= 3 && words[offset + 1] == entry)
            {
                uint mode = words[offset + 2], size = words[offset] >> 16;
                if (mode == 26 && size != 4) throw new ArgumentException("Malformed OutputVertices execution mode.");
                if (!modes.TryAdd(mode, size >= 4 ? words[offset + 3] : 0))
                    throw new ArgumentException("Duplicate execution mode.");
            }
        return modes;
    }

    private static string NormalizeGeometryPerVertexInputs(string source)
    {
        const string Prefix = "gl_in[";
        const string Suffix = "].gl_Position";
        const string ReplacementPrefix = "gl_PositionIn[";

        if (!source.Contains("gl_PositionIn", StringComparison.Ordinal) ||
            !source.Contains(Prefix, StringComparison.Ordinal))
        {
            return source;
        }

        StringBuilder result = new(source.Length);
        int copyFrom = 0;
        int rewrittenInputs = 0;
        while (true)
        {
            int start = source.IndexOf(Prefix, copyFrom, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(source, copyFrom, source.Length - copyFrom);
                break;
            }

            int expressionStart = start + Prefix.Length;
            int end = source.IndexOf(Suffix, expressionStart, StringComparison.Ordinal);
            if (end < 0)
            {
                result.Append(source, copyFrom, source.Length - copyFrom);
                break;
            }

            result.Append(source, copyFrom, start - copyFrom);
            result.Append(ReplacementPrefix);
            result.Append(source, expressionStart, end - expressionStart);
            result.Append(']');
            copyFrom = end + Suffix.Length;
            rewrittenInputs++;
        }

        return rewrittenInputs == 0 ? source : result.ToString();
    }

    private static string NormalizeOutputStageInterface(string source)
    {
        const string OutputDeclaration = "SPIRV_Cross_Output stage_output;";

        string result = NormalizeTexCoordSignature(
            source,
            "struct SPIRV_Cross_Output",
            "ryubox_link_dummy",
            out int[] insertedLocations,
            out _);
        if (insertedLocations.Length == 0)
        {
            return result;
        }

        StringBuilder initializers = new();
        initializers.AppendLine();
        foreach (int location in insertedLocations)
        {
            initializers.Append("        stage_output.ryubox_link_dummy")
                .Append(location)
                .AppendLine(" = gl_Position;");
        }

        string replacement = OutputDeclaration + initializers.ToString();
        return result.Replace(OutputDeclaration, replacement, StringComparison.Ordinal);
    }

    private static string NormalizeInputStageInterface(string source)
    {
        return NormalizeTexCoordSignature(
            source,
            "struct SPIRV_Cross_Input",
            "ryubox_link_input_dummy",
            out _,
            out _);
    }

    private static string NormalizeTexCoordSignature(
        string source,
        string structName,
        string dummyPrefix,
        out int[] insertedLocations,
        out bool reordered)
    {
        const int MaxTexCoordLocations = 16;
        Regex texCoordDeclaration = new(
            @"(?m)^[ \t]*(?<declaration>[^\r\n;:{}]+:\s*TEXCOORD(?<location>\d+)\s*;)[ \t]*(?:\r?\n|$)",
            RegexOptions.CultureInvariant);

        insertedLocations = [];
        reordered = false;
        int structStart = source.IndexOf(structName, StringComparison.Ordinal);
        if (structStart < 0)
        {
            return source;
        }

        int bodyStart = source.IndexOf('{', structStart);
        int bodyEnd = bodyStart < 0
            ? -1
            : source.IndexOf("};", bodyStart, StringComparison.Ordinal);
        if (bodyStart < 0 || bodyEnd < 0)
        {
            return source;
        }

        string body = source[(bodyStart + 1)..bodyEnd];
        MatchCollection matches = texCoordDeclaration.Matches(body);
        if (matches.Count == 0)
        {
            return source;
        }

        List<string>[] declarations = new List<string>[MaxTexCoordLocations];
        for (int i = 0; i < MaxTexCoordLocations; i++)
        {
            declarations[i] = [];
        }

        List<int> originalOrder = new(matches.Count);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["location"].Value, out int location) ||
                (uint)location >= MaxTexCoordLocations)
            {
                continue;
            }

            declarations[location].Add(match.Groups["declaration"].Value.Trim());
            originalOrder.Add(location);
        }

        List<int> missing = [];
        for (int location = 0; location < MaxTexCoordLocations; location++)
        {
            if (declarations[location].Count > 0)
            {
                continue;
            }

            declarations[location].Add($"float4 {dummyPrefix}{location} : TEXCOORD{location};");
            missing.Add(location);
        }

        insertedLocations = missing.ToArray();
        for (int index = 1; index < originalOrder.Count; index++)
        {
            if (originalOrder[index - 1] > originalOrder[index])
            {
                reordered = true;
                break;
            }
        }

        string remainingBody = texCoordDeclaration.Replace(body, match =>
            int.TryParse(match.Groups["location"].Value, out int location) &&
            (uint)location < MaxTexCoordLocations ? string.Empty : match.Value).TrimStart('\r', '\n');
        StringBuilder normalizedBody = new(body.Length + missing.Count * 48);
        normalizedBody.AppendLine();
        for (int location = 0; location < MaxTexCoordLocations; location++)
        {
            foreach (string decl in declarations[location])
            {
                normalizedBody.Append("    ").AppendLine(decl);
            }
        }

        string[] remainingLines = remainingBody.Split(["\r\n", "\n"], StringSplitOptions.None);
        List<string> otherLines = [];
        List<string> positionLines = [];
        foreach (string line in remainingLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Contains("SV_Position", StringComparison.OrdinalIgnoreCase))
            {
                positionLines.Add(line.Trim());
            }
            else
            {
                otherLines.Add(line.Trim());
            }
        }

        foreach (string line in otherLines)
        {
            normalizedBody.Append("    ").AppendLine(line);
        }

        foreach (string line in positionLines)
        {
            normalizedBody.Append("    ").AppendLine(line);
        }

        return source[..(bodyStart + 1)] + normalizedBody + source[bodyEnd..];
    }

    private nint Get(string name) => NativeLibrary.GetExport(_library, name);
    private void Check(int result, nint context)
    {
        if (result != 0) throw new InvalidOperationException($"SPIRV-Cross ({result}): {(context == 0 ? "context creation failed" : Marshal.PtrToStringUTF8(_error(context)))}");
    }
    public void Dispose()
    {
        lock (_sync) { if (_library != 0) { NativeLibrary.Free(_library); _library = 0; } }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadPackagedLibrary(string libraryName, uint reserved);
}
