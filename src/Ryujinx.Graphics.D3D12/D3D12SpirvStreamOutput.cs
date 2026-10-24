using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.D3D12;

// SPIR-V's Xfb decorations are metadata, not HLSL instructions. Preserve them
// alongside the translated source so native SO doesn't lose offset/stride.
internal static class D3D12SpirvStreamOutput
{
    internal static D3D12StreamOutput[] Read(byte[] bytes)
    {
        var words = MemoryMarshal.Cast<byte, uint>(bytes);
        if (words.Length < 5 || words[3] == 0 || words[3] > 1024 * 1024)
            throw new ArgumentException("Invalid or excessive SPIR-V ID bound.");
        uint bound = words[3];
        void Id(uint id)
        { if (id == 0 || id >= bound) throw new ArgumentException("SPIR-V ID exceeds the module bound."); }
        var decorations = new Dictionary<(uint, uint), Dictionary<uint, uint>>();
        var decorationGroups = new HashSet<uint>();
        var groupDecorations = new List<(uint Group, uint[] Targets)>();
        var groupMemberDecorations = new List<(uint Group, (uint Target, uint Member)[] Targets)>();
        var types = new Dictionary<uint, (uint Op, uint[] Args)>();
        var constants = new Dictionary<uint, uint>();
        var outputs = new List<(uint Type, uint Id)>();
        bool hasXfb = false;
        for (int i = 5; i < words.Length;)
        {
            int count = (int)(words[i] >> 16); uint op = words[i] & 65535;
            if (count == 0 || count > words.Length - i) throw new ArgumentException("Malformed SPIR-V instruction.");
            var w = words.Slice(i, count);
            if (op is 71 or 72)
            {
                int keyIndex = op == 71 ? 2 : 3;
                if (count <= keyIndex) throw new ArgumentException("Malformed decoration.");
                Id(w[1]);
                var key = (w[1], op == 71 ? uint.MaxValue : w[2]);
                if (!decorations.TryGetValue(key, out var values)) decorations.Add(key, values = new());
                uint decoration = w[keyIndex];
                uint value = count > keyIndex + 1 ? w[keyIndex + 1] : 1;
                if (!values.TryAdd(decoration, value) && values[decoration] != value) throw new ArgumentException("Conflicting SPIR-V decoration.");
                hasXfb |= decoration is 36 or 37;
            }
            else if (op == 73)
            {
                if (count < 3) throw new ArgumentException("Malformed grouped decoration.");
                Id(w[1]);
                uint[] targets = w.Slice(2).ToArray();
                foreach (uint target in targets) Id(target);
                groupDecorations.Add((w[1], targets));
            }
            else if (op == 74)
            {
                if (count < 4 || (count & 1) != 0) throw new ArgumentException("Malformed grouped member decoration.");
                Id(w[1]);
                var targets = new (uint Target, uint Member)[(count - 2) / 2];
                for (int t = 0, source = 2; source < count; source += 2, ++t)
                {
                    Id(w[source]);
                    targets[t] = (w[source], w[source + 1]);
                }
                groupMemberDecorations.Add((w[1], targets));
            }
            else if (op == 75)
            {
                if (count != 2) throw new ArgumentException("Malformed decoration group.");
                Id(w[1]);
                decorationGroups.Add(w[1]);
            }
            else if (op is >= 19 and <= 33)
            {
                if (count < 2) throw new ArgumentException("Malformed SPIR-V type.");
                Id(w[1]);
                types.Add(w[1], (op, w.Slice(2).ToArray()));
            }
            else if (op == 43 && count >= 4) { Id(w[1]); Id(w[2]); constants[w[2]] = w[3]; }
            else if (op == 59 && count >= 4)
            {
                Id(w[1]); Id(w[2]);
                if (w[3] == 3) outputs.Add((w[1], w[2]));
            }
            i += count;
        }
        if (!hasXfb) return [];
        void Apply(uint group, uint target, uint member)
        {
            if (!decorationGroups.Contains(group)) throw new ArgumentException("Grouped decoration references an unknown decoration group.");
            if (!decorations.TryGetValue((group, uint.MaxValue), out var groupValues)) return;
            var key = (target, member);
            if (!decorations.TryGetValue(key, out var values)) decorations.Add(key, values = new());
            foreach (var pair in groupValues)
                if (!values.TryAdd(pair.Key, pair.Value) && values[pair.Key] != pair.Value)
                    throw new ArgumentException("Conflicting grouped SPIR-V decoration.");
        }
        foreach (var group in groupDecorations)
            foreach (uint target in group.Targets) Apply(group.Group, target, uint.MaxValue);
        foreach (var group in groupMemberDecorations)
            foreach (var target in group.Targets) Apply(group.Group, target.Target, target.Member);

        var result = new List<D3D12StreamOutput>();
        Dictionary<uint, uint> Merge(Dictionary<uint, uint> inherited, uint id, uint member)
        {
            var merged = new Dictionary<uint, uint>(inherited);
            if (decorations.TryGetValue((id, member), out var d)) foreach (var pair in d) merged[pair.Key] = pair.Value;
            return merged;
        }
        void Emit(uint type, Dictionary<uint, uint> d, int depth = 0)
        {
            if (depth > 8 || !types.TryGetValue(type, out var t)) throw new ArgumentException("Invalid Xfb output type.");
            if (t.Op == 30)
            {
                for (uint m = 0; m < t.Args.Length; ++m) Emit(t.Args[m], Merge(d, type, m), depth + 1);
                return;
            }
            if (!d.TryGetValue(36, out uint buffer)) return; // Non-captured output.
            if (!d.TryGetValue(37, out uint stride) || !d.TryGetValue(35, out uint offset))
                throw new ArgumentException("Captured output lacks XfbStride/Offset.");
            if (t.Op == 28)
            {
                if (t.Args.Length != 2 || !constants.TryGetValue(t.Args[1], out uint length) || length > 32)
                    throw new NotSupportedException("Unbounded Xfb output array.");
                var element = types[t.Args[0]];
                uint components = element.Op == 23 ? element.Args[1] : 1;
                if (element.Op is not (21 or 22 or 23)) throw new NotSupportedException("Nested Xfb output arrays.");
                for (uint a = 0; a < length; ++a)
                {
                    var item = new Dictionary<uint, uint>(d); item[35] = checked(offset + a * components * 4);
                    if (d.TryGetValue(30, out uint location)) item[30] = checked(location + a);
                    else item[31] = a * components;
                    Emit(t.Args[0], item, depth + 1);
                }
                return;
            }
            uint n = t.Op == 23 ? t.Args[1] : 1;
            var scalar = t.Op == 23 ? types[t.Args[0]] : t;
            if (scalar.Op is not (21 or 22) || scalar.Args[0] != 32 || n is < 1 or > 4)
                throw new NotSupportedException("Xfb currently requires 32-bit scalar/vector outputs.");
            uint component = d.GetValueOrDefault(31u), index;
            string semantic;
            if (d.TryGetValue(11, out uint builtin))
            {
                semantic = builtin switch { 0 => "SV_Position", 3 => "SV_ClipDistance", 4 => "SV_CullDistance", 7 => "SV_PrimitiveID",
                    _ => throw new NotSupportedException($"Xfb builtin {builtin} has no implemented HLSL mapping.") };
                index = component / 4; component %= 4;
            }
            else if (d.TryGetValue(30, out index)) semantic = "TEXCOORD";
            else throw new ArgumentException("Captured output lacks a semantic.");
            if (component + n > 4 || result.Count >= 128) throw new NotSupportedException("Xfb output exceeds semantic/layout limits.");
            result.Add(new(semantic, index, component, n, buffer, d.GetValueOrDefault(29u), offset, stride));
        }
        foreach (var output in outputs)
        {
            var pointer = types[output.Type];
            if (pointer.Op != 32 || pointer.Args.Length != 2) throw new ArgumentException("Invalid output pointer.");
            Emit(pointer.Args[1], Merge(new(), output.Id, uint.MaxValue));
        }
        if (result.Count == 0) throw new ArgumentException("Xfb declarations exist but no captured output was reflected.");
        return result.OrderBy(x => x.Buffer).ThenBy(x => x.Offset).ToArray();
    }
}
