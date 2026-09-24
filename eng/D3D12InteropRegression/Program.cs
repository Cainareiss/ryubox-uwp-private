using Ryujinx.Graphics.D3D12;
using System;
using System.Runtime.InteropServices;

const string FailingShape = """
groupshared uint sharedCounter;
groupshared uint sharedValues[32];
RWStructuredBuffer<uint> output : register(u0);

[numthreads(32, 1, 1)]
void comp_main(uint gl_LocalInvocationIndex : SV_GroupIndex)
{
    sharedCounter = 0u;
    sharedValues[gl_LocalInvocationIndex] = 0u;
    GroupMemoryBarrierWithGroupSync();
    sharedValues[gl_LocalInvocationIndex] = sharedCounter;
    output[gl_LocalInvocationIndex] = sharedValues[gl_LocalInvocationIndex];
}
""";

string normalized = D3D12ComputeSharedNormalizer.Normalize(FailingShape);
string normalizedLf = normalized.Replace("\r\n", "\n", StringComparison.Ordinal);
int predicate = normalizedLf.IndexOf("if (gl_LocalInvocationIndex == 0u)", StringComparison.Ordinal);
int predicatedWrite = predicate < 0 ? -1 : normalizedLf.IndexOf("sharedCounter = 0u;", predicate, StringComparison.Ordinal);
int firstBarrier = normalizedLf.IndexOf("GroupMemoryBarrierWithGroupSync()", StringComparison.Ordinal);
Require(predicate >= 0 && predicatedWrite > predicate && predicatedWrite < firstBarrier,
    "scalar groupshared initializer was not predicated before the first barrier");
Require(normalizedLf.Contains("sharedValues[gl_LocalInvocationIndex] = 0u;", StringComparison.Ordinal),
    "lane-private initializer was changed");
Require(CountOccurrences(normalizedLf, "GroupMemoryBarrierWithGroupSync()") == 1,
    "group barrier was removed or duplicated");
Require(normalizedLf.Contains("sharedValues[gl_LocalInvocationIndex] = sharedCounter;", StringComparison.Ordinal),
    "guest shared-memory write was changed");
Require(normalizedLf.Contains("output[gl_LocalInvocationIndex] = sharedValues[gl_LocalInvocationIndex];", StringComparison.Ordinal),
    "compute result write was changed");
Require(D3D12ComputeSharedNormalizer.Normalize(normalized) == normalized,
    "normalization is not idempotent");

Console.WriteLine("D3D12 compute shared-memory normalization regression passed.");

if (OperatingSystem.IsWindows())
{
    byte[] source = System.Text.Encoding.UTF8.GetBytes(normalized + "\0");
    int result = NativeMethods.D3DCompile(source, (nuint)(source.Length - 1), "x3694-regression.hlsl",
        0, 0, "comp_main", "cs_5_1", 1u << 11, 0, out nint shader, out nint errors);
    if (errors != 0) Marshal.Release(errors);
    if (shader != 0) Marshal.Release(shader);
    Require(result >= 0, $"normalized compute shader failed FXC: 0x{result:X8}");
    Console.WriteLine("Normalized X3694 regression shader compiled with FXC.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static int CountOccurrences(string source, string value)
{
    int count = 0;
    for (int offset = 0; (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0; offset += value.Length)
        count++;
    return count;
}

static class NativeMethods
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    internal static extern int D3DCompile(byte[] source, nuint sourceLength, string sourceName,
        nint defines, nint include, string entryPoint, string target, uint flags1, uint flags2,
        out nint code, out nint errors);
}
