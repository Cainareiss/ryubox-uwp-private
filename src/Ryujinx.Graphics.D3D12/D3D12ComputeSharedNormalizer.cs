using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.D3D12;

/// <summary>
/// Makes SPIRV-Cross workgroup zero-initializers single-writer operations for
/// FXC. It deliberately touches only top-level assignments to declared
/// groupshared objects before the first group barrier. Guest stores and
/// barriers are never moved or removed.
/// </summary>
internal static partial class D3D12ComputeSharedNormalizer
{
    [GeneratedRegex(@"(?m)^\s*groupshared\s+.+?\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^;]+\])?\s*;\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SharedDeclarationRegex();

    [GeneratedRegex(@"(?m)^\s*RW(?:Buffer|Texture\w*)\s*<\s*(?:uint|int)\s*>\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UavDeclarationRegex();

    [GeneratedRegex(@"(?m)^(?<indent>\s*)(?<uav>[A-Za-z_]\w*)\s*\[(?<index>.+)\]\s*=\s*(?<value>[^;]+);(?<tail>\s*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UavStoreRegex();

    public static string Normalize(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        source = NormalizeShared(source);
        source = NormalizeUavStores(source);
        return source;
    }

    private static string NormalizeShared(string source)
    {
        if (!source.Contains("groupshared", StringComparison.Ordinal) ||
            !source.Contains("GroupMemoryBarrierWithGroupSync", StringComparison.Ordinal))
        {
            return source;
        }

        HashSet<string> shared = new(StringComparer.Ordinal);
        foreach (Match declaration in SharedDeclarationRegex().Matches(source))
            shared.Add(declaration.Groups["name"].Value);
        if (shared.Count == 0) return source;

        int main = source.IndexOf("void comp_main", StringComparison.Ordinal);
        if (main < 0) main = source.IndexOf("void main", StringComparison.Ordinal);
        int body = main < 0 ? -1 : source.IndexOf('{', main);

        // Do not invent an invocation index: some shaders have no such input.
        // The SV_GroupIndex builtin must be in scope inside the rewritten
        // function, either copied from stage_input into the SPIRV-Cross
        // static or declared directly as an entry-point parameter.
        if (body < 0 || !HasInvocationIndex(source, source[main..body]))
            return source;
        int barrier = body < 0 ? -1 : source.IndexOf("GroupMemoryBarrierWithGroupSync", body, StringComparison.Ordinal);
        if (body < 0 || barrier < 0) return source;

        string prefix = source[..(body + 1)];
        string initialization = source[(body + 1)..barrier];
        string suffix = source[barrier..];
        string[] lines = initialization.Split('\n');
        StringBuilder rewritten = new(initialization.Length + 128);
        int depth = 0;
        int replacements = 0;

        foreach (string line in lines)
        {
            Match assignment = AssignmentRegex().Match(line.TrimEnd('\r'));
            string target = assignment.Success ? assignment.Groups["target"].Value : string.Empty;
            int bracket = target.IndexOf('[');
            string name = (bracket < 0 ? target : target[..bracket]).Trim();
            string value = assignment.Success ? assignment.Groups["value"].Value.Trim() : string.Empty;
            // A guest-dependent index can be named anything (not necessarily
            // LocalInvocation). Electing lane zero would lose other lanes' stores.
            bool constantTarget = bracket < 0 || Regex.IsMatch(target[bracket..], @"^\[\s*\d+[uU]?\s*\]$");

            if (depth == 0 && assignment.Success && shared.Contains(name) && constantTarget && IsZeroInitializer(value))
            {
                string indent = assignment.Groups["indent"].Value;
                rewritten.Append(indent).AppendLine("if (gl_LocalInvocationIndex == 0u)");
                rewritten.Append(indent).AppendLine("{");
                rewritten.Append(indent).Append("    ").Append(line.TrimStart()).AppendLine();
                rewritten.Append(indent).AppendLine("}");
                replacements++;
            }
            else
            {
                rewritten.Append(line).AppendLine();
            }

            foreach (char character in line)
            {
                if (character == '{') depth++;
                else if (character == '}') depth--;
            }
        }

        return replacements == 0 ? source : prefix + rewritten + suffix;
    }

    private static bool HasInvocationIndex(string source, string signature)
    {
        const string GroupIndexInput = @"\bgl_LocalInvocationIndex\s*:\s*SV_GroupIndex\b";

        if (Regex.IsMatch(signature, GroupIndexInput))
            return true;

        return Regex.IsMatch(source, GroupIndexInput) &&
            Regex.IsMatch(source, @"\bgl_LocalInvocationIndex\s*=\s*stage_input\.gl_LocalInvocationIndex\s*;");
    }

    private static bool IsZeroInitializer(string value)
    {
        // Constructors and casts are permitted; identifiers other than scalar
        // type names make the expression guest-dependent and therefore unsafe
        // to rewrite.
        string compact = Regex.Replace(value, @"\b(?:bool|int|uint|half|float|double)(?:[1-4](?:x[1-4])?)?\b",
            string.Empty, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        compact = compact.Replace("false", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("0x0", string.Empty, StringComparison.OrdinalIgnoreCase);
        compact = Regex.Replace(compact, @"0+(?:\.0*)?(?:[eE][+-]?0+)?[uUlLfFhH]*", string.Empty,
            RegexOptions.CultureInvariant);
        // Arithmetic is not a constructor: 0/0 is NaN, not a zero initializer.
        compact = Regex.Replace(compact, @"[\s(),{}]", string.Empty, RegexOptions.CultureInvariant);
        return compact.Length == 0;
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?<target>[A-Za-z_]\w*(?:\s*\[[^\]]+\])?)\s*=\s*(?<value>[^;]+);(?<tail>\s*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();

    private static string NormalizeUavStores(string source)
    {
        if (!source.Contains("RWBuffer", StringComparison.Ordinal) &&
            !source.Contains("RWTexture", StringComparison.Ordinal))
        {
            return source;
        }

        HashSet<string> uavs = new(StringComparer.Ordinal);
        foreach (Match declaration in UavDeclarationRegex().Matches(source))
        {
            uavs.Add(declaration.Groups["name"].Value);
        }

        if (uavs.Count == 0)
        {
            return source;
        }

        int replacements = 0;
        string result = UavStoreRegex().Replace(source, match =>
        {
            string uav = match.Groups["uav"].Value;
            if (uavs.Contains(uav))
            {
                replacements++;
                return $"{match.Groups["indent"].Value}InterlockedExchange({uav}[{match.Groups["index"].Value}], {match.Groups["value"].Value}, _ryubox_uav_dummy);{match.Groups["tail"].Value}";
            }
            return match.Value;
        });

        if (replacements > 0 && !result.Contains("static uint _ryubox_uav_dummy;", StringComparison.Ordinal))
        {
            result = "static uint _ryubox_uav_dummy;\r\n" + result;
        }

        return result;
    }
}
