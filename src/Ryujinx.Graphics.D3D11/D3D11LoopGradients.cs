using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.D3D11;

// Conservative lowering for SPIRV-Cross' scalarized 2D kernels. Only an additive
// quad-uniform offset may be removed from the derivative expression. In
// particular texture-dependent offsets and divergent assignments are rejected.
// This is not a SampleLevel(0) fallback and does not impose an unroll limit.
internal static class D3D11LoopGradients
{
    private sealed record Definition(string Name, string Expression, int Line, List<string> Guards);
    private sealed record Block(string Guard, bool Loop, int FirstDefinition);
    private static readonly Regex Assignment = new(
        @"^(?:(?:precise\s+)?(?:float[1-4]?|uint[1-4]?|int[1-4]?|bool[1-4]?)\s+)?(?<name>\w+)\s*=\s*(?<expression>.+);$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sample = new(
        @"(?<texture>\w+)\.Sample\((?<sampler>\w+),\s*float2\((?<x>\w+),\s*(?<y>\w+)\)\)",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Pure = new(
        "float float2 float3 float4 int int2 int3 int4 uint uint2 uint3 uint4 bool true false asint asuint asfloat mad min max abs clamp saturate spvPackHalf2x16 spvUnpackHalf2x16".Split(' '));

    internal static string Lower(string source, out int count)
    {
        count = 0;
        int begin = source.IndexOf("void main_inner()", StringComparison.Ordinal);
        if (begin < 0 || source.Contains("ryubox_gradient_", StringComparison.Ordinal)) return source;
        string[] lines = source.Split('\n');
        List<Definition> definitions = new();
        Dictionary<int, List<string>> lineGuards = new();
        Stack<Block> blocks = new();
        HashSet<int> loopLines = new();
        string pendingGuard = "";
        bool pendingLoop = false, inside = false;
        int firstLoop = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line == "void main_inner()") { inside = true; continue; }
            if (!inside) continue;
            if (line.StartsWith("while", StringComparison.Ordinal)) return source;
            // Unsupported control flow is deliberately left to FXC/diagnostics.
            if (Regex.IsMatch(line, @"\b(for|else|switch|break|continue|discard)\b")) return source;
            if (line == "do")
            {
                if (firstLoop < 0 && blocks.Any(b => b.Guard.Length != 0)) return source;
                pendingLoop = true;
                if (firstLoop < 0) firstLoop = i;
                continue;
            }
            if (line.StartsWith("if (", StringComparison.Ordinal))
            {
                pendingGuard = line[4..^1];
                continue;
            }
            if (line == "{")
            {
                blocks.Push(new(pendingGuard, pendingLoop, definitions.Count));
                pendingGuard = "";
                pendingLoop = false;
                continue;
            }
            if (line.StartsWith('}'))
            {
                if (blocks.Count == 0) return source;
                Block block = blocks.Pop();
                if (block.Loop)
                {
                    Match condition = Regex.Match(line, @"^}\s*while\s*\((.+)\);$");
                    if (!condition.Success) return source;
                    for (int d = block.FirstDefinition; d < definitions.Count; d++)
                        definitions[d].Guards.Add(condition.Groups[1].Value);
                }
                if (blocks.Count == 0) break;
                continue;
            }
            List<string> guards = blocks.Where(b => b.Guard.Length > 0).Select(b => b.Guard).ToList();
            lineGuards[i] = guards;
            if (blocks.Any(b => b.Loop)) loopLines.Add(i);
            Match assignment = Assignment.Match(line);
            if (assignment.Success)
                definitions.Add(new(assignment.Groups["name"].Value, assignment.Groups["expression"].Value, i, guards));
            else if (Regex.IsMatch(line, @"\+\+|--|[+*/&|^-]=|\w+\.[xyzwrgba]+\s*="))
            {
                // Output stores don't feed coordinates; any other unparsed
                // mutation prevents a sound uniformity proof.
                Match outputStore = Regex.Match(line, @"^(_\w+)\.[xyzwrgba]\s*=.+;$");
                if (!outputStore.Success || loopLines.Contains(i) || definitions.Any(d => d.Name == outputStore.Groups[1].Value)) return source;
            }
            else if (line.Length != 0 && !line.StartsWith("//", StringComparison.Ordinal) &&
                !Regex.IsMatch(line, @"^(?:float[1-4]?|int[1-4]?|uint[1-4]?|bool)\s+\w+;$")) return source;
        }
        if (firstLoop < 0) return source;
        Dictionary<string, List<Definition>> byName = definitions.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.ToList());
        HashSet<string> constants = Regex.Matches(source[..begin], @"\b(\w+)(?:\[\d+\])?\s*:\s*packoffset\(")
            .Select(m => m.Groups[1].Value).ToHashSet();
        HashSet<string> uniform = byName.Keys.ToHashSet();
        bool TokensUniform(string expression)
        {
            foreach (Match token in Regex.Matches(expression, @"\b[A-Za-z_]\w*"))
            {
                if (token.Index > 0 && expression[token.Index - 1] == '.') continue; // swizzle
                if (!uniform.Contains(token.Value) && !constants.Contains(token.Value) && !Pure.Contains(token.Value)) return false;
            }
            return true;
        }
        bool UniformHalf(string expression, int component, HashSet<(string, int)> visited)
        {
            expression = expression.Trim();
            if (TokensUniform(expression)) return true;
            Match cast = Regex.Match(expression, @"^(?:uint|int)\((.+)\)$");
            if (cast.Success) return UniformHalf(cast.Groups[1].Value, component, visited);
            Match shift = Regex.Match(expression, @"^(.+)\s*<<\s*16$");
            if (shift.Success) return component == 0 || UniformHalf(shift.Groups[1].Value, 0, visited);
            Match packed = Regex.Match(expression, @"^spvPackHalf2x16\(float2\(([^,()]+),\s*([^,()]+)\)\)$");
            if (packed.Success) return IsUniform(packed.Groups[component + 1].Value);
            if (byName.TryGetValue(expression, out var defs))
            {
                if (!visited.Add((expression, component))) return true; // loop phi; all incoming definitions still checked
                return defs.All(d => d.Guards.All(IsUniform) && UniformHalf(d.Expression, component, visited));
            }
            return false;
        }
        bool IsUniform(string expression)
        {
            Match unpacked = Regex.Match(expression, @"^spvUnpackHalf2x16\((.+)\)\.([xy])$");
            return unpacked.Success
                ? UniformHalf(unpacked.Groups[1].Value, unpacked.Groups[2].Value == "x" ? 0 : 1, new())
                : TokensUniform(expression);
        }
        bool changed;
        do
        {
            changed = false;
            foreach (string name in uniform.ToArray())
                if (byName[name].Any(d => !IsUniform(d.Expression) || d.Guards.Any(g => !IsUniform(g))))
                { uniform.Remove(name); changed = true; }
        } while (changed);

        string DerivativeBase(string expression, HashSet<string> visited)
        {
            expression = expression.Trim();
            if (IsUniform(expression)) return "0.0f";
            if (byName.TryGetValue(expression, out var defs) && defs.Count == 1 && visited.Add(expression))
            {
                Definition def = defs[0];
                if (def.Line < firstLoop && def.Guards.Count == 0) return expression;
                if (def.Guards.Any(g => !IsUniform(g))) return null;
                return DerivativeBase(def.Expression, visited);
            }
            Match mad = Regex.Match(expression, @"^mad\(([^,()]+),\s*([^,()]+),\s*([^,()]+)\)$");
            if (mad.Success && IsUniform(mad.Groups[1].Value) && IsUniform(mad.Groups[2].Value))
                return DerivativeBase(mad.Groups[3].Value, visited);
            Match add = Regex.Match(expression, @"^(\w+)\s*\+\s*(\w+)$");
            if (add.Success)
            {
                if (IsUniform(add.Groups[1].Value)) return DerivativeBase(add.Groups[2].Value, visited);
                if (IsUniform(add.Groups[2].Value)) return DerivativeBase(add.Groups[1].Value, visited);
            }
            return null;
        }
        List<string> hoisted = new();
        int lowered = 0;
        foreach (int i in loopLines)
        {
            if (lineGuards[i].Any(g => !IsUniform(g))) continue;
            lines[i] = Sample.Replace(lines[i], match =>
            {
                // Do not match Texture2DArray/Cube overloads or custom methods.
                string texture = match.Groups["texture"].Value;
                if (!Regex.IsMatch(source[..begin], @"\bTexture2D<float4>\s+" + Regex.Escape(texture) + @"\s*:")) return match.Value;
                string x = DerivativeBase(match.Groups["x"].Value, new());
                string y = DerivativeBase(match.Groups["y"].Value, new());
                if (x == null || y == null) return match.Value;
                string name = "ryubox_gradient_" + lowered++;
                hoisted.Add($"    float2 {name}_dx = ddx(float2({x}, {y}));\n    float2 {name}_dy = ddy(float2({x}, {y}));");
                return $"{texture}.SampleGrad({match.Groups["sampler"].Value}, float2({match.Groups["x"].Value}, {match.Groups["y"].Value}), {name}_dx, {name}_dy)";
            });
        }
        count = lowered;
        if (lowered == 0) return source;
        lines[firstLoop] = string.Join('\n', hoisted) + "\n" + lines[firstLoop];
        return string.Join('\n', lines);
    }
}
