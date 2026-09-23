using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.D3D12;

// Links the structured interfaces emitted by our SPIRV-Cross backend. HLSL
// requires identical HS/DS control-point and patch signatures, including fields
// the DS does not read. This operates on generated declarations, never rewrites
// shader instructions or substitutes zero for a missing producer.
internal static class D3D12TessellationLinker
{
    private readonly record struct Field(string Declaration, string Type, string Name, string Array, string Semantic);
    private static readonly Regex Fields = new(
        @"^\s*(?<qual>(?:(?:precise|nointerpolation|noperspective|centroid|sample|linear)\s+)*)" +
        @"(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*(?<array>(?:\[\s*\d+\s*\])*)\s*:\s*(?<semantic>[A-Za-z_]\w*)\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static void Link(D3D12ShaderSource[] shaders, uint outputVertices)
    {
        int hull = Array.FindIndex(shaders, s => s.Stage == ShaderStage.TessellationControl);
        int domain = Array.FindIndex(shaders, s => s.Stage == ShaderStage.TessellationEvaluation);
        if (hull < 0 && domain < 0) return;
        if (hull < 0 || domain < 0 || outputVertices is < 1 or > 32)
            throw new ArgumentException("Incomplete tessellation program.");
        string hs = shaders[hull].Hlsl, ds = shaders[domain].Hlsl;
        ds = LinkStructure(hs, "SPIRV_Cross_Output", ds, "SPIRV_Cross_Input");
        ds = LinkStructure(hs, "SPIRV_Cross_Patch", ds, "SPIRV_Cross_Patch");
        const string signature = "SPIRV_Cross_Output main(";
        int begin = ds.IndexOf(signature, StringComparison.Ordinal);
        int end = begin < 0 ? -1 : ds.IndexOf(")\n{", begin, StringComparison.Ordinal);
        if (end < 0) throw new NotSupportedException("Unrecognized generated domain entry signature.");
        if (!ds.AsSpan(begin, end - begin).Contains("OutputPatch<", StringComparison.Ordinal))
            ds = ds.Insert(end, $", const OutputPatch<SPIRV_Cross_Input, {outputVertices}> stage_input");
        shaders[domain] = shaders[domain] with { Hlsl = ds };
    }

    private static (int Start, int End, string Body) Structure(string source, string name, bool required)
    {
        string prefix = "struct " + name + "\n{";
        int start = source.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            if (required) throw new NotSupportedException($"Missing generated tessellation structure {name}.");
            return (-1, -1, "");
        }
        int body = start + prefix.Length, end = source.IndexOf("};", body, StringComparison.Ordinal);
        if (end < 0 || source.AsSpan(body, end - body).Contains('{'))
            throw new NotSupportedException($"Unrecognized generated tessellation structure {name}.");
        return (start, end + 2, source[body..end]);
    }
    private static List<Field> ReadFields(string body)
    {
        var result = new List<Field>();
        var semantics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Fields.Match(field);
            if (!match.Success) throw new NotSupportedException($"Unsupported tessellation interface declaration: {field}");
            string semantic = match.Groups["semantic"].Value;
            if (!semantics.Add(semantic)) throw new ArgumentException("Duplicate tessellation interface semantic.");
            result.Add(new(field, match.Groups["type"].Value, match.Groups["name"].Value,
                match.Groups["array"].Value.Replace(" ", "", StringComparison.Ordinal), semantic));
        }
        return result;
    }
    private static string LinkStructure(string producer, string outputName, string consumer, string inputName)
    {
        var output = ReadFields(Structure(producer, outputName, true).Body);
        var current = Structure(consumer, inputName, false);
        var input = ReadFields(current.Body);
        foreach (var field in input)
            if (!output.Exists(o => o.Semantic.Equals(field.Semantic, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Domain input {field.Semantic} has no hull producer.");
        var text = new StringBuilder("struct " + inputName + "\n{\n");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in output)
        {
            int existing = input.FindIndex(f => f.Semantic.Equals(field.Semantic, StringComparison.OrdinalIgnoreCase));
            Field linked = existing < 0 ? field : input[existing];
            if (field.Type != linked.Type || field.Array != linked.Array)
                throw new ArgumentException($"Hull/domain type mismatch at {field.Semantic}.");
            if (!names.Add(linked.Name)) throw new ArgumentException("Tessellation interface field-name collision.");
            text.Append("    ").Append(linked.Declaration).Append(";\n");
        }
        text.Append("};");
        return current.Start < 0 ? text + "\n\n" + consumer :
            consumer[..current.Start] + text + consumer[current.End..];
    }
}
