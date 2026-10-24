using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Ryujinx.Graphics.D3D11;

/// <summary>
/// Optional SPIR-V to D3D11 shader bridge. The host supplies the compiled DXBC/DXIL
/// callback because Xbox/UWP does not guarantee that a desktop shader compiler DLL
/// can be loaded by the managed renderer.
/// </summary>
public sealed class D3D11SpirvCrossCompiler : IDisposable
{
    private const uint BackendHlsl = 2;
    private const uint CaptureTakeOwnership = 1;
    private const uint CommonOptionBit = 0x1000000;
    private const uint HlslOptionBit = 0x4000000;
    private const uint OptionFixupDepthConvention = CommonOptionBit | 3;
    private const uint OptionHlslShaderModel = HlslOptionBit | 13;
    private const uint OptionHlslPointSizeCompat = HlslOptionBit | 14;
    private const uint OptionHlslPointCoordCompat = HlslOptionBit | 15;
    private const uint OptionHlslSupportNonzeroBase = HlslOptionBit | 16;
    private const uint OptionHlslUseEntryPointName = HlslOptionBit | 90;
    private const uint OptionForceZeroInitializedVariables = CommonOptionBit | 54;
    private const uint OptionEnableStorageImageQualifierDeduction = CommonOptionBit | 52;
    private const uint OptionHlslNonwritableUavTextureAsSrv = HlslOptionBit | 55;
    private const uint OptionHlslFlattenMatrixVertexInputSemantics = HlslOptionBit | 71;

    private readonly nint _library;
    private readonly Func<ShaderStage, string, byte[]> _compileHlsl;
    private readonly ContextCreateDelegate _contextCreate;
    private readonly ContextDestroyDelegate _contextDestroy;
    private readonly ContextParseSpirvDelegate _contextParseSpirv;
    private readonly ContextCreateCompilerDelegate _contextCreateCompiler;
    private readonly CompilerCreateOptionsDelegate _compilerCreateOptions;
    private readonly CompilerOptionsSetBoolDelegate _optionsSetBool;
    private readonly CompilerOptionsSetUintDelegate _optionsSetUint;
    private readonly CompilerInstallOptionsDelegate _compilerInstallOptions;
    private readonly CompilerCompileDelegate _compilerCompile;
    private readonly HlslBindingInitDelegate _hlslBindingInit;
    private readonly HlslAddResourceBindingDelegate _hlslAddResourceBinding;
    private readonly HlslIsResourceUsedDelegate _hlslIsResourceUsed;
    private readonly ContextGetLastErrorDelegate _contextGetLastError;
    private readonly Action<string> _diagnosticLog;
    private int _compileSequence;
    private bool _disposed;

    public D3D11SpirvCrossCompiler(
        string libraryPath,
        Func<ShaderStage, string, byte[]> compileHlsl,
        Action<string> diagnosticLog = null)
    {
        _compileHlsl = compileHlsl ?? throw new ArgumentNullException(nameof(compileHlsl));
        _diagnosticLog = diagnosticLog;
        ArgumentNullException.ThrowIfNull(libraryPath);

        // UWP resolves native components through the package dependency graph.
        // LoadPackagedLibrary gives deterministic Xbox behavior while the
        // NativeLibrary fallback keeps the unpackaged desktop smoke test usable.
        string packagedName = Path.GetFileName(libraryPath);
        _library = LoadPackagedLibrary(packagedName, 0);
        int packagedLoadError = _library == 0 ? Marshal.GetLastWin32Error() : 0;
        if (_library == 0)
        {
            try
            {
                _library = NativeLibrary.Load(libraryPath);
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
            {
                throw new DllNotFoundException(
                    $"Unable to load packaged SPIRV-Cross '{packagedName}' " +
                    $"(LoadPackagedLibrary Win32 error 0x{packagedLoadError:X8}). " +
                    "The DLL or one of its UWP dependencies is unavailable.",
                    exception);
            }
        }

        _contextCreate = Load<ContextCreateDelegate>("spvc_context_create");
        _contextDestroy = Load<ContextDestroyDelegate>("spvc_context_destroy");
        _contextParseSpirv = Load<ContextParseSpirvDelegate>("spvc_context_parse_spirv");
        _contextCreateCompiler = Load<ContextCreateCompilerDelegate>("spvc_context_create_compiler");
        _compilerCreateOptions = Load<CompilerCreateOptionsDelegate>("spvc_compiler_create_compiler_options");
        _optionsSetBool = Load<CompilerOptionsSetBoolDelegate>("spvc_compiler_options_set_bool");
        _optionsSetUint = Load<CompilerOptionsSetUintDelegate>("spvc_compiler_options_set_uint");
        _compilerInstallOptions = Load<CompilerInstallOptionsDelegate>("spvc_compiler_install_compiler_options");
        _compilerCompile = Load<CompilerCompileDelegate>("spvc_compiler_compile");
        _hlslBindingInit = Load<HlslBindingInitDelegate>("spvc_hlsl_resource_binding_init");
        _hlslAddResourceBinding = Load<HlslAddResourceBindingDelegate>("spvc_compiler_hlsl_add_resource_binding");
        _hlslIsResourceUsed = Load<HlslIsResourceUsedDelegate>("spvc_compiler_hlsl_is_resource_used");
        _contextGetLastError = Load<ContextGetLastErrorDelegate>("spvc_context_get_last_error_string");
    }

    public byte[] Compile(ShaderStage stage, byte[] spirv)
    {
        return CompileCore(stage, spirv, null);
    }

    public byte[] Compile(ShaderStage stage, byte[] spirv, Ryujinx.Graphics.GAL.ShaderInfo info)
    {
        return CompileCore(stage, spirv, new D3D11BindingLayout(info));
    }

    private byte[] CompileCore(ShaderStage stage, byte[] spirv, D3D11BindingLayout bindingLayout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (spirv is null || spirv.Length == 0 || (spirv.Length & 3) != 0)
        {
            throw new ArgumentException("SPIR-V must contain a non-empty sequence of 32-bit words.", nameof(spirv));
        }

        uint[] words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        nint context = 0;
        int sequence = Interlocked.Increment(ref _compileSequence);
        long startedAt = Environment.TickCount64;
        Log($"SPIRV-Cross begin: sequence={sequence}, stage={stage}, bytes={spirv.Length}, layout={bindingLayout?.HasBindings == true}");

        try
        {
            Check(_contextCreate(out context), context, "spvc_context_create");

            GCHandle pin = GCHandle.Alloc(words, GCHandleType.Pinned);
            try
            {
                Check(
                    _contextParseSpirv(context, pin.AddrOfPinnedObject(), (nuint)words.Length, out nint parsedIr),
                    context,
                    "spvc_context_parse_spirv");
                Log($"SPIRV-Cross parsed: sequence={sequence}, elapsed={Environment.TickCount64 - startedAt} ms");

                Check(
                    _contextCreateCompiler(context, BackendHlsl, parsedIr, CaptureTakeOwnership, out nint compiler),
                    context,
                    "spvc_context_create_compiler");

                Check(_compilerCreateOptions(compiler, out nint options), context, "spvc_compiler_create_compiler_options");
                Check(_optionsSetUint(options, OptionHlslShaderModel, 50), context, "spvc_compiler_options_set_uint(shader model)");
                // HLSL SM4+ has no direct equivalents for SPIR-V PointSize and
                // PointCoord. SPIRV-Cross' compatibility path ignores writes to
                // PointSize and returns the documented (0.5, 0.5) fallback for
                // PointCoord, which keeps non-point rendering shaders valid.
                Check(_optionsSetBool(options, OptionHlslPointSizeCompat, 1), context, "spvc_compiler_options_set_bool(point size compatibility)");
                Check(_optionsSetBool(options, OptionHlslPointCoordCompat, 1), context, "spvc_compiler_options_set_bool(point coordinate compatibility)");
                // Ryujinx's shader translator already emits the OpenGL/Vulkan
                // to D3D depth conversion when depth-clip control is unavailable.
                // Enabling SPIRV-Cross' fixup here applies it a second time and
                // can push every primitive outside the visible depth range.
                Check(_optionsSetBool(options, OptionFixupDepthConvention, 0), context, "spvc_compiler_options_set_bool(depth convention)");
                Check(_optionsSetBool(options, OptionHlslSupportNonzeroBase, 1), context, "spvc_compiler_options_set_bool(base instance)");
                Check(_optionsSetBool(options, OptionHlslUseEntryPointName, 1), context, "spvc_compiler_options_set_bool(entry point)");
                Check(_optionsSetBool(options, OptionForceZeroInitializedVariables, 1), context, "spvc_compiler_options_set_bool(zero initialize)");
                Check(_optionsSetBool(options, OptionEnableStorageImageQualifierDeduction, 1), context, "spvc_compiler_options_set_bool(image qualifiers)");
                Check(_optionsSetBool(options, OptionHlslNonwritableUavTextureAsSrv, 1), context, "spvc_compiler_options_set_bool(read-only images)");
                Check(_optionsSetBool(options, OptionHlslFlattenMatrixVertexInputSemantics, 1), context, "spvc_compiler_options_set_bool(matrix semantics)");
                Check(_compilerInstallOptions(compiler, options), context, "spvc_compiler_install_compiler_options");
                ApplyResourceBindings(compiler, stage, bindingLayout, context);
                Log($"SPIRV-Cross emit begin: sequence={sequence}, elapsed={Environment.TickCount64 - startedAt} ms");
                Check(_compilerCompile(compiler, out nint source), context, "spvc_compiler_compile");

                string hlsl = Marshal.PtrToStringAnsi(source)
                    ?? throw new InvalidOperationException("SPIRV-Cross returned a null HLSL source pointer.");
                // Older SPIRV-Cross builds used by the Xbox package can emit
                // every writable resource as register(u0), even after the
                // explicit resource-binding table was installed. FXC rejects
                // that source with X4500. Keep the resource order emitted by
                // SPIR-V and repair only duplicate UAV ranges; the same
                // deterministic order is used by D3D11BindingLayout when the
                // guest resources are submitted to the pipeline.
                hlsl = NormalizeUavRegisters(hlsl, stage, out int rewrittenUavs);
                if (rewrittenUavs != 0)
                {
                    Log($"SPIRV-Cross UAV register fixup: sequence={sequence}, stage={stage}, rewritten={rewrittenUavs}");
                }
                if (stage.IsVtg)
                {
                    // D3D11 does not accept UAV buffer declarations in the
                    // vertex/tessellation/geometry stages on the UWP/Xbox
                    // feature levels we target. SPIRV-Cross preserves the
                    // guest's writable storage-buffer type as RW* even when
                    // the shader only performs loads. The matching binding
                    // layout exposes that resource as an SRV for these stages;
                    // lower the read-only HLSL declaration to the valid SRV
                    // type before FXC/Create*Shader sees it.
                    string rewritten = RewriteVtgStorageBuffers(hlsl);
                    if (!string.Equals(rewritten, hlsl, StringComparison.Ordinal))
                    {
                        hlsl = rewritten;
                        Log($"SPIRV-Cross VTG storage-buffer SRV fixup: sequence={sequence}");
                    }
                }
                if (stage == ShaderStage.Vertex)
                {
                    hlsl = NormalizeVertexStageInterface(hlsl, out int insertedOutputs, out bool reorderedOutputs);
                    if (insertedOutputs != 0 || reorderedOutputs)
                    {
                        Log($"SPIRV-Cross vertex interface fixup: sequence={sequence}, inserted={insertedOutputs}, reordered={reorderedOutputs}");
                    }
                }
                else if (stage == ShaderStage.Fragment)
                {
                    hlsl = D3D11LoopGradients.Lower(hlsl, out int explicitGradients);
                    if (explicitGradients != 0)
                        Log($"SPIRV-Cross loop gradients: sequence={sequence}, hoisted={explicitGradients}, policy=uniform-offset-preserve-lod");
                    hlsl = NormalizeFragmentStageInterface(hlsl, out int insertedInputs, out bool reorderedInputs);
                    if (insertedInputs != 0 || reorderedInputs)
                    {
                        Log($"SPIRV-Cross fragment interface fixup: sequence={sequence}, inserted={insertedInputs}, reordered={reorderedInputs}");
                    }
                }
                else if (stage == ShaderStage.Geometry)
                {
                    hlsl = NormalizeGeometryPerVertexInputs(hlsl, out int rewrittenInputs);
                    if (rewrittenInputs != 0)
                    {
                        Log($"SPIRV-Cross geometry input fixup: sequence={sequence}, rewritten={rewrittenInputs}");
                    }
                }
                Log($"SPIRV-Cross emit end: sequence={sequence}, chars={hlsl.Length}, elapsed={Environment.TickCount64 - startedAt} ms");
                byte[] result;
                try
                {
                    result = _compileHlsl(stage, hlsl)
                        ?? throw new InvalidOperationException("The host HLSL compiler returned null bytecode.");
                }
                catch (Exception exception)
                {
                    SaveFailure(stage, spirv, hlsl, bindingLayout, exception);
                    throw;
                }
                Log($"SPIRV-Cross complete: sequence={sequence}, bytecode={result.Length}, elapsed={Environment.TickCount64 - startedAt} ms");
                return result;
            }
            finally
            {
                pin.Free();
            }
        }
        finally
        {
            if (context != 0)
            {
                _contextDestroy(context);
            }
        }
    }

    private void Log(string message)
    {
        _diagnosticLog?.Invoke(message);
    }

    private void SaveFailure(ShaderStage stage, byte[] spirv, string hlsl, D3D11BindingLayout bindings, Exception failure)
    {
        try
        {
            string logs = Ryujinx.Common.Configuration.AppDataManager.LogsDirPath;
            if (string.IsNullOrEmpty(logs)) return;
            string hash = Convert.ToHexString(SHA256.HashData(spirv));
            string directory = Path.Combine(logs, "d3d11-shader-failures");
            Directory.CreateDirectory(directory);
            string prefix = Path.Combine(directory, stage + "-" + hash);
            if (File.Exists(prefix + ".diagnostics.txt")) return;
            // Bounded, deduplicated artifacts; never write a shader on every draw.
            if (Directory.GetFiles(directory, "*.diagnostics.txt").Length >= 32) return;
            File.WriteAllBytes(prefix + ".spv", spirv);
            File.WriteAllText(prefix + ".hlsl", hlsl);
            StringBuilder map = new();
            if (bindings != null)
                foreach (var binding in bindings.Entries) map.AppendLine($"{binding.Key} -> {binding.Value}");
            File.WriteAllText(prefix + ".bindings.txt", map.ToString());
            File.WriteAllText(prefix + ".diagnostics.txt", $"shaderHash={hash}\nstage={stage}\nIR=translated-SPIR-V (not guest ISA)\n{failure}");
            Log($"Shader failure artifacts saved: {prefix}");
        }
        catch (Exception exception)
        {
            Log($"Shader failure artifact unavailable: {exception.GetType().Name}");
        }
    }

    private string NormalizeUavRegisters(string source, ShaderStage stage, out int rewritten)
    {
        int rewrittenCount = 0;
        rewritten = 0;
        if (!source.Contains("register(u", StringComparison.Ordinal))
        {
            return source;
        }

        HashSet<int> used = new();
        Regex declaration = new(
            @"(?<prefix>\b(?:RWByteAddressBuffer|RWStructuredBuffer\s*<[^;\r\n>]+>|RWBuffer\s*<[^;\r\n>]+>|RWTexture(?:1D|2D|3D)(?:Array)?\s*<[^;\r\n>]+>|AppendStructuredBuffer\s*<[^;\r\n>]+>|ConsumeStructuredBuffer\s*<[^;\r\n>]+>|RasterizerOrdered(?:StructuredBuffer|ByteAddressBuffer|Buffer|Texture(?:1D|2D|3D)(?:Array)?)\s*<[^;\r\n>]*>)\s+[A-Za-z_]\w*(?:\s*\[\s*(?<count>\d+)\s*\])?\s*:\s*register\s*\(\s*u)(?<slot>\d+)(?<suffix>\s*\))",
            RegexOptions.CultureInvariant);

        string result = declaration.Replace(source, match =>
        {
            int slot = int.Parse(match.Groups["slot"].Value);
            int count = match.Groups["count"].Success ? Math.Max(1, int.Parse(match.Groups["count"].Value)) : 1;
            int mapped = slot;

            bool rangeFree(int candidate)
            {
                for (int index = 0; index < count; index++)
                {
                    if (used.Contains(candidate + index))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (!rangeFree(mapped))
            {
                mapped = 0;
                while (!rangeFree(mapped))
                {
                    mapped++;
                }
            }

            for (int index = 0; index < count; index++)
            {
                used.Add(mapped + index);
            }

            if (mapped == slot)
            {
                return match.Value;
            }

            rewrittenCount++;
            string replacement = match.Value.Substring(0, match.Groups["slot"].Index - match.Index) +
                                 mapped.ToString() +
                                 match.Value.Substring(match.Groups["slot"].Index - match.Index + match.Groups["slot"].Length);
            _diagnosticLog?.Invoke($"UAV register remap: stage={stage}, resource={GetResourceName(match.Value)}, from=u{slot}, to=u{mapped}, count={count}");
            return replacement;
        });

        rewritten = rewrittenCount;
        return rewrittenCount == 0 ? source : result;
    }

    private static string GetResourceName(string declaration)
    {
        int colon = declaration.IndexOf(':');
        if (colon < 0)
        {
            return "<unknown>";
        }

        string prefix = declaration[..colon].Trim();
        int separator = prefix.LastIndexOf(' ');
        return separator >= 0 ? prefix[(separator + 1)..].Trim() : prefix;
    }

    private static string RewriteVtgStorageBuffers(string hlsl)
    {
        // Only buffer resources are rewritten. Writable images/textures have
        // different semantics and must continue through the normal UAV path.
        hlsl = Regex.Replace(
            hlsl,
            @"\bRWByteAddressBuffer(\s+[A-Za-z_]\w*\s*:\s*register\s*)\(u(\d+)\)",
            "ByteAddressBuffer$1(t$2)",
            RegexOptions.CultureInvariant);
        hlsl = Regex.Replace(
            hlsl,
            @"\bRWStructuredBuffer(\s*<[^;\r\n>]+>\s+[A-Za-z_]\w*\s*:\s*register\s*)\(u(\d+)\)",
            "StructuredBuffer$1(t$2)",
            RegexOptions.CultureInvariant);
        hlsl = Regex.Replace(
            hlsl,
            @"\bRWBuffer(\s*<[^;\r\n>]+>\s+[A-Za-z_]\w*\s*:\s*register\s*)\(u(\d+)\)",
            "Buffer$1(t$2)",
            RegexOptions.CultureInvariant);
        return hlsl;
    }

    /// <summary>
    /// SPIRV-Cross can lower the SPIR-V gl_PerVertex input block to the HLSL
    /// global <c>gl_PositionIn[]</c>, yet leave accesses using the original GLSL
    /// spelling <c>gl_in[index].gl_Position</c>. FXC then rejects an otherwise
    /// valid geometry shader because HLSL has no gl_in declaration. Rewrite only
    /// this mismatched built-in when the generated global is actually present.
    /// </summary>
    private static string NormalizeGeometryPerVertexInputs(string source, out int rewrittenInputs)
    {
        const string Prefix = "gl_in[";
        const string Suffix = "].gl_Position";
        const string ReplacementPrefix = "gl_PositionIn[";

        rewrittenInputs = 0;
        if (!source.Contains("gl_PositionIn[", StringComparison.Ordinal) ||
            !source.Contains(Prefix, StringComparison.Ordinal))
        {
            return source;
        }

        StringBuilder result = new StringBuilder(source.Length);
        int copyFrom = 0;
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

    /// <summary>
    /// D3D11 links independently compiled stage signatures by both semantic
    /// and the hardware register selected by FXC. A sparse vertex output such
    /// as TEXCOORD0,1,5,6 and a sparse pixel input with a different declaration
    /// order can therefore receive different registers even though the Vulkan
    /// locations match. Build a stable 0..15 signature on both sides. Keeping
    /// the declarations in semantic order is essential; merely appending the
    /// missing locations still leaves the existing fields on different DXBC
    /// registers and causes draw-time ID 343 linkage failures.
    /// </summary>
    private static string NormalizeVertexStageInterface(
        string source,
        out int insertedOutputs,
        out bool reorderedOutputs)
    {
        const string OutputDeclaration = "SPIRV_Cross_Output stage_output;";

        string result = NormalizeTexCoordSignature(
            source,
            "struct SPIRV_Cross_Output",
            "ryubox_link_dummy",
            out int[] insertedLocations,
            out reorderedOutputs);
        insertedOutputs = insertedLocations.Length;
        if (insertedOutputs == 0)
        {
            return result;
        }

        StringBuilder initializers = new();
        foreach (int location in insertedLocations)
        {
            // Depend on the computed position rather than a compile-time
            // constant so FXC retains the output in the DXBC signature.
            initializers.Append("    stage_output.ryubox_link_dummy")
                .Append(location)
                .AppendLine(" = gl_Position;");
        }

        int declarationIndex = result.IndexOf(OutputDeclaration, StringComparison.Ordinal);
        if (declarationIndex < 0)
        {
            return result;
        }

        int initializerIndex = declarationIndex + OutputDeclaration.Length;
        return result.Insert(initializerIndex, Environment.NewLine + initializers);
    }

    private static string NormalizeFragmentStageInterface(
        string source,
        out int insertedInputs,
        out bool reorderedInputs)
    {
        string result = NormalizeTexCoordSignature(
            source,
            "struct SPIRV_Cross_Input",
            "ryubox_link_input_dummy",
            out int[] insertedLocations,
            out reorderedInputs);
        insertedInputs = insertedLocations.Length;
        return result;
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

        insertedLocations = Array.Empty<int>();
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
            declarations[i] = new List<string>();
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

        List<int> missing = new();
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

        // The normalization window is NOT a limit on guest varyings. Previously
        // declarations at TEXCOORD16+ were skipped above but removed here, leaving
        // main() accesses such as stage_output._26 with no declared member (X3018).
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
        normalizedBody.Append(remainingBody);

        return source[..(bodyStart + 1)] + normalizedBody + source[bodyEnd..];
    }

    private void ApplyResourceBindings(nint compiler, ShaderStage stage, D3D11BindingLayout layout, nint context)
    {
        if (layout is null)
        {
            return;
        }

        uint executionModel = ToExecutionModel(stage);
        foreach (var entry in layout.Entries)
        {
            if (entry.Key.Stage != stage ||
                _hlslIsResourceUsed(compiler, executionModel, (uint)entry.Key.Set, (uint)entry.Key.Binding) == 0)
            {
                continue;
            }

            HlslResourceBinding native = default;
            _hlslBindingInit(ref native);
            native.Stage = executionModel;
            native.DescriptorSet = (uint)entry.Key.Set;
            native.Binding = (uint)entry.Key.Binding;
            SetMapping(ref native.Cbv, entry.Value.Cbv);
            SetMapping(ref native.Srv, entry.Value.Srv);
            SetMapping(ref native.Uav, entry.Value.Uav);
            SetMapping(ref native.Sampler, entry.Value.Sampler);
            Check(_hlslAddResourceBinding(compiler, ref native), context, "spvc_compiler_hlsl_add_resource_binding");
        }
    }

    private static void SetMapping(ref HlslResourceBindingMapping mapping, int register)
    {
        if (register >= 0)
        {
            mapping.RegisterSpace = 0;
            mapping.RegisterBinding = (uint)register;
        }
    }

    private static uint ToExecutionModel(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => 0,
        ShaderStage.TessellationControl => 1,
        ShaderStage.TessellationEvaluation => 2,
        ShaderStage.Geometry => 3,
        ShaderStage.Fragment => 4,
        ShaderStage.Compute => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            NativeLibrary.Free(_library);
        }
    }

    private T Load<T>(string name) where T : Delegate
    {
        nint address = NativeLibrary.GetExport(_library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void Check(int result, nint context, string operation)
    {
        if (result == 0)
        {
            return;
        }

        string detail = context != 0 ? Marshal.PtrToStringAnsi(_contextGetLastError(context)) : null;
        throw new InvalidOperationException($"{operation} failed with SPVC result {result}: {detail}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HlslResourceBindingMapping
    {
        public uint RegisterSpace;
        public uint RegisterBinding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HlslResourceBinding
    {
        public uint Stage;
        public uint DescriptorSet;
        public uint Binding;
        public HlslResourceBindingMapping Cbv;
        public HlslResourceBindingMapping Uav;
        public HlslResourceBindingMapping Srv;
        public HlslResourceBindingMapping Sampler;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HlslBindingInitDelegate(ref HlslResourceBinding binding);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HlslAddResourceBindingDelegate(nint compiler, ref HlslResourceBinding binding);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HlslIsResourceUsedDelegate(nint compiler, uint executionModel, uint descriptorSet, uint binding);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ContextCreateDelegate(out nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContextDestroyDelegate(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ContextParseSpirvDelegate(nint context, nint spirv, nuint wordCount, out nint parsedIr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ContextCreateCompilerDelegate(nint context, uint backend, nint parsedIr, uint captureMode, out nint compiler);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompilerCreateOptionsDelegate(nint compiler, out nint options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompilerOptionsSetBoolDelegate(nint options, uint option, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompilerOptionsSetUintDelegate(nint options, uint option, uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompilerInstallOptionsDelegate(nint compiler, nint options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompilerCompileDelegate(nint compiler, out nint source);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ContextGetLastErrorDelegate(nint context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadPackagedLibrary(string libraryName, uint reserved);
}
