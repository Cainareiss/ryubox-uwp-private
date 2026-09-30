using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.D3D12;

/// <summary>
/// Avoid FXC's unbounded unroll attempt for implicit-gradient sampling loops
/// only when the iteration limit is a cbuffer scalar and the sample coordinate
/// does not depend on the induction variable. That keeps all lanes in the quad
/// on the same iterations and leaves the gradient expression unchanged.
/// </summary>
internal static class D3D12FragmentLoopNormalizer
{
    public static string Normalize(string source)
    {
        HashSet<string> uniformNames = Regex.Matches(source,
                @"\bcbuffer\b[^{}]*\{(?<fields>[^{}]*)\}", RegexOptions.CultureInvariant)
            .Cast<Match>().SelectMany(block => Regex.Matches(block.Groups["fields"].Value,
                    @"(?m)(?:^|;)\s*(?:row_major\s+|column_major\s+)?(?:bool|int|uint|float|half|double)(?:[1-4](?:x[1-4])?)?\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^]]+\])?\s*;",
                    RegexOptions.CultureInvariant).Cast<Match>().Select(field => field.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
        if (uniformNames.Count == 0) return source;

        List<int> insertions = [];
        for (int index = 0; index < source.Length - 3; index++)
        {
            if (!IsWordAt(source, index, "for")) continue;
            int open = SkipWhitespace(source, index + 3);
            if (open >= source.Length || source[open] != '(') continue;
            int close = FindMatching(source, open, '(', ')');
            if (close < 0) continue;
            string header = source[(open + 1)..close];
            string[] clauses = header.Split(';');
            if (clauses.Length != 3) continue;
            Match counter = Regex.Match(clauses[0], @"(?:int|uint)\s+(?<name>[A-Za-z_]\w*)\s*=", RegexOptions.CultureInvariant);
            if (!counter.Success) continue;
            string counterName = counter.Groups["name"].Value;
            Match limit = Regex.Match(clauses[1], @"^\s*" + Regex.Escape(counterName) + @"\s*<\s*(?<name>[A-Za-z_]\w*)\s*$", RegexOptions.CultureInvariant);
            if (!limit.Success || !uniformNames.Contains(limit.Groups["name"].Value)) continue;

            int bodyOpen = SkipWhitespace(source, close + 1);
            if (bodyOpen >= source.Length || source[bodyOpen] != '{') continue;
            int bodyClose = FindMatching(source, bodyOpen, '{', '}');
            if (bodyClose < 0) continue;
            string body = source[(bodyOpen + 1)..bodyClose];
            if (!TryGetImplicitSamples(body, out string[] coordinates) || coordinates.Any(coord =>
                    Regex.IsMatch(coord, @"\b" + Regex.Escape(counterName) + @"\b", RegexOptions.CultureInvariant)))
                continue;

            string before = source[..index].TrimEnd();
            if (before.EndsWith("[loop]", StringComparison.Ordinal) || before.EndsWith("[unroll]", StringComparison.Ordinal)) continue;
            insertions.Add(index);
            index = bodyClose;
        }

        if (insertions.Count == 0) return source;
        System.Text.StringBuilder result = new(source);
        foreach (int index in insertions.AsEnumerable().Reverse()) result.Insert(index, "[loop]\n");
        return result.ToString();
    }

    private static bool TryGetImplicitSamples(string body, out string[] coordinates)
    {
        List<string> found = [];
        for (int index = 0; index < body.Length; index++)
        {
            if (body[index] != '.') continue;
            Match method = Regex.Match(body[index..], @"^\.(?<name>Sample|SampleBias)\s*\(", RegexOptions.CultureInvariant);
            if (!method.Success) continue;
            int open = index + method.Length - 1;
            int close = FindMatching(body, open, '(', ')');
            if (close < 0) continue;
            string[] args = SplitArguments(body[(open + 1)..close]);
            if (args.Length >= 2) found.Add(args[1]);
            index = close;
        }
        coordinates = found.ToArray();
        return coordinates.Length != 0;
    }

    private static string[] SplitArguments(string text)
    {
        List<string> result = [];
        int start = 0, depth = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is '(' or '[') depth++;
            else if (text[index] is ')' or ']') depth--;
            else if (text[index] == ',' && depth == 0) { result.Add(text[start..index].Trim()); start = index + 1; }
        }
        result.Add(text[start..].Trim());
        return result.ToArray();
    }

    private static bool IsWordAt(string text, int index, string word) =>
        text.AsSpan(index, word.Length).SequenceEqual(word) &&
        (index == 0 || !char.IsLetterOrDigit(text[index - 1]) && text[index - 1] != '_') &&
        (index + word.Length == text.Length || !char.IsLetterOrDigit(text[index + word.Length]) && text[index + word.Length] != '_');

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private static int FindMatching(string text, int open, char left, char right)
    {
        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            if (text[index] == left) depth++;
            else if (text[index] == right && --depth == 0) return index;
        }
        return -1;
    }
}
