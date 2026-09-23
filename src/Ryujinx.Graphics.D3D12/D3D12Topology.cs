using System;
using System.Collections.Generic;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D12;

internal static class D3D12Topology
{
    internal static bool NeedsExpansion(PrimitiveTopology topology) => topology is PrimitiveTopology.LineLoop or
        PrimitiveTopology.TriangleFan or PrimitiveTopology.Quads or PrimitiveTopology.QuadStrip or PrimitiveTopology.Polygon;

    internal static (PrimitiveTopology Topology, uint[] Indices) Expand(PrimitiveTopology topology, ReadOnlySpan<uint> source,
        bool restart, uint restartIndex, int patchVertices = 3)
    {
        if (topology == PrimitiveTopology.Patches && patchVertices is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(patchVertices));
        var output = new List<uint>();
        var resultTopology = topology switch
        {
            PrimitiveTopology.LineLoop or PrimitiveTopology.LineStrip => PrimitiveTopology.Lines,
            PrimitiveTopology.LineStripAdjacency => PrimitiveTopology.LinesAdjacency,
            PrimitiveTopology.TriangleStripAdjacency => PrimitiveTopology.TrianglesAdjacency,
            PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon or PrimitiveTopology.Quads or
                PrimitiveTopology.QuadStrip or PrimitiveTopology.TriangleStrip => PrimitiveTopology.Triangles,
            _ => topology,
        };
        void Segment(ReadOnlySpan<uint> indices)
        {
            void Tri(uint a, uint b, uint c) { output.Add(a); output.Add(b); output.Add(c); }
            switch (topology)
            {
                case PrimitiveTopology.TriangleFan:
                case PrimitiveTopology.Polygon:
                    for (int i = 2; i < indices.Length; ++i) Tri(indices[0], indices[i - 1], indices[i]);
                    break;
                case PrimitiveTopology.Quads:
                    for (int i = 0; i + 3 < indices.Length; i += 4)
                    { Tri(indices[i], indices[i + 1], indices[i + 2]); Tri(indices[i], indices[i + 2], indices[i + 3]); }
                    break;
                case PrimitiveTopology.QuadStrip:
                    for (int i = 0; i + 3 < indices.Length; i += 2)
                    { Tri(indices[i], indices[i + 1], indices[i + 2]); Tri(indices[i + 2], indices[i + 1], indices[i + 3]); }
                    break;
                case PrimitiveTopology.TriangleStrip:
                    for (int i = 2; i < indices.Length; ++i)
                        if ((i & 1) == 0) Tri(indices[i - 2], indices[i - 1], indices[i]);
                        else Tri(indices[i - 1], indices[i - 2], indices[i]);
                    break;
                case PrimitiveTopology.LineLoop:
                case PrimitiveTopology.LineStrip:
                    for (int i = 1; i < indices.Length; ++i) { output.Add(indices[i - 1]); output.Add(indices[i]); }
                    if (topology == PrimitiveTopology.LineLoop && indices.Length > 1) { output.Add(indices[^1]); output.Add(indices[0]); }
                    break;
                case PrimitiveTopology.Points:
                case PrimitiveTopology.Lines:
                case PrimitiveTopology.Triangles:
                case PrimitiveTopology.LinesAdjacency:
                case PrimitiveTopology.TrianglesAdjacency:
                case PrimitiveTopology.Patches:
                    int size = topology switch { PrimitiveTopology.Lines => 2, PrimitiveTopology.Triangles => 3,
                        PrimitiveTopology.LinesAdjacency => 4, PrimitiveTopology.TrianglesAdjacency => 6,
                        PrimitiveTopology.Patches => patchVertices, _ => 1 };
                    for (int i = 0; i < indices.Length - indices.Length % size; ++i) output.Add(indices[i]);
                    break;
                case PrimitiveTopology.LineStripAdjacency:
                    for (int i = 0; i + 3 < indices.Length; ++i)
                        for (int j = 0; j < 4; ++j) output.Add(indices[i + j]);
                    break;
                case PrimitiveTopology.TriangleStripAdjacency:
                    // Adjacency is not a sliding window of six vertices. The
                    // next main vertex closes an interior edge; only the last
                    // triangle uses the final supplied adjacency vertex.
                    // Preserve the leading vertex (OpenGL 4.6 table 10.2 /
                    // DirectX ordering), including odd triangles after restart.
                    int triangles = Math.Max(0, indices.Length / 2 - 2);
                    for (int triangle = 0; triangle < triangles; ++triangle)
                    {
                        int b = triangle * 2;
                        int next = triangle == triangles - 1 ? b + 5 : b + 6;
                        if (triangle == 0)
                        {
                            output.Add(indices[0]); output.Add(indices[1]); output.Add(indices[2]);
                            output.Add(indices[next]); output.Add(indices[4]); output.Add(indices[3]);
                        }
                        else if ((triangle & 1) == 0)
                        {
                            output.Add(indices[b]); output.Add(indices[b - 2]); output.Add(indices[b + 2]);
                            output.Add(indices[next]); output.Add(indices[b + 4]); output.Add(indices[b + 3]);
                        }
                        else
                        {
                            output.Add(indices[b]); output.Add(indices[b + 3]); output.Add(indices[b + 4]);
                            output.Add(indices[next]); output.Add(indices[b + 2]); output.Add(indices[b - 2]);
                        }
                    }
                    break;
                default: throw new NotSupportedException($"D3D12 restart expansion for {topology} is not implemented.");
            }
        }
        int start = 0;
        for (int i = 0; i <= source.Length; ++i)
            if (i == source.Length || restart && source[i] == restartIndex)
            { Segment(source.Slice(start, i - start)); start = i + 1; }
        return (resultTopology, output.ToArray());
    }
}
