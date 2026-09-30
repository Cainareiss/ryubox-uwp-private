using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.D3D12;

internal static class D3D12PixelInputNormalizer
{
    public static string Normalize(string source)
    {
        Match block = Regex.Match(source, @"\bstruct\s+SPIRV_Cross_Input\s*\{(?<body>[^{}]*)\};",
            RegexOptions.CultureInvariant);
        if (!block.Success) return source;
        Group body = block.Groups["body"];
        var fields = Regex.Matches(body.Value,
            @"(?m)^[ \t]*[^\r\n;:{}]+:\s*(?<semantic>\w+)\s*;[ \t]*(?:\r?\n|$)",
            RegexOptions.CultureInvariant);
        // Only reorder a plain generated signature, not macros or comments
        // whose position can affect the meaning of declarations.
        string rest = body.Value;
        foreach (Match field in fields.Cast<Match>().Reverse()) rest = rest.Remove(field.Index, field.Length);
        if (!string.IsNullOrWhiteSpace(rest)) return source;
        static int Rank(Match field)
        {
            string semantic = field.Groups["semantic"].Value.ToUpperInvariant();
            return semantic switch
            {
                // SV_Position must always be at hardware register 0, matching
                // the vertex/geometry shader output signature.
                "SV_POSITION" => -1,
                // FXC ps_5_1 includes PrimitiveID in the non-SGV portion:
                // it must precede IsFrontFace, just like Position. DXIL's
                // semantic classification is not a substitute for FXC here.
                "SV_ISFRONTFACE" or "SV_SAMPLEINDEX" or "SV_COVERAGE" => 2,
                _ when semantic.StartsWith("SV_", StringComparison.Ordinal) => 1,
                _ => 0,
            };
        }
        var ordered = fields.Cast<Match>().OrderBy(Rank).ToArray();
        if (ordered.Select(m => m.Index).SequenceEqual(fields.Cast<Match>().Select(m => m.Index))) return source;
        string result = "\n" + string.Concat(ordered.Select(m => m.Value.TrimEnd('\r', '\n') + "\n"));
        return source[..body.Index] + result + source[(body.Index + body.Length)..];
    }
}
