using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.D3D12;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;

if (args.Length < 2) throw new ArgumentException("native.dll spirv-cross.dll [--warp]");
nint native = NativeLibrary.Load(System.IO.Path.GetFullPath(args[0]));
NativeLibrary.SetDllImportResolver(typeof(D3D12NativeDevice).Assembly,
    (name, _, _) => name == "Ryujinx.Uwp.Native.dll" ? native : 0);
var flags = D3D12DeviceFlags.DebugLayer;
if (Array.IndexOf(args, "--warp") >= 0) flags |= D3D12DeviceFlags.Warp;
using var renderer = new D3D12Renderer(8, 8, args[1], flags: flags);
var info = new TextureCreateInfo(8, 8, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm,
    DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green,
    SwizzleComponent.Blue, SwizzleComponent.Alpha);
var texture = renderer.CreateTexture(info);
uint[] pattern = new uint[64];
for (int i = 0; i < pattern.Length; i++) pattern[i] = 0xff000000u | (uint)(i * 179);
texture.SetData(MemoryOwner<byte>.RentCopy(MemoryMarshal.AsBytes(pattern.AsSpan())));
byte[] capture = null;
renderer.ScreenCaptured += (_, e) => capture = e.Data;
int cases = 0;
// A CPU producer writes top-to-bottom rows. D3D12 preserves that order unless
// the GAL explicitly requests FlipY; verify every crop/flip combination.
foreach (bool crop in new[] { false, true })
foreach (bool x in new[] { false, true })
foreach (bool y in new[] { false, true })
{
    int left = crop ? 2 : 0, top = crop ? 1 : 0, size = crop ? 4 : 8;
    renderer.Window.SetSize(size, size);
    renderer.Window.Present(texture, new(left, left + size, top, top + size, x, y, true, 1, 1), null);
    renderer.Screenshot();
    var pixels = MemoryMarshal.Cast<byte, uint>(capture.AsSpan());
    for (int row = 0; row < size; row++)
    for (int col = 0; col < size; col++)
    {
        uint expected = pattern[(top + (y ? size - 1 - row : row)) * 8 + left + (x ? size - 1 - col : col)];
        if (pixels[row * size + col] != expected)
            throw new Exception($"crop={crop} flip={x}/{y} pixel={col}/{row}: {pixels[row * size + col]:X8} != {expected:X8}");
    }
    cases++;
}
// A black crop of a nonblack source must stay black in the screenshot. This
// catches the old fallback that substituted the untransformed guest texture.
pattern[0] = 0xff000000;
texture.SetData(MemoryOwner<byte>.RentCopy(MemoryMarshal.AsBytes(pattern.AsSpan())));
renderer.Window.SetSize(1, 1);
renderer.Window.Present(texture, new(0, 1, 0, 1, false, false, true, 1, 1), null);
renderer.Screenshot();
if (capture.Length != 4 || MemoryMarshal.Read<uint>(capture) != 0xff000000)
    throw new Exception("Black presentation was replaced with raw guest texture");
texture.Release();
if (renderer.Device.GetInfo().DebugErrors != 0) throw new Exception("Debug layer errors");
Console.WriteLine($"PASS {cases} GPU presentation cases: crop, all flips, resize, exact pixels; debug layer clean.");

var color = renderer.CreateTexture(info);
var depthInfo = new TextureCreateInfo(4, 4, 1, 1, 1, 1, 1, 4, Format.D32Float,
    DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green,
    SwizzleComponent.Blue, SwizzleComponent.Alpha);
var depth = renderer.CreateTexture(depthInfo);
using var near = renderer.Device.CreateProgram(
    new(ShaderStage.Vertex, "float4 main(uint id:SV_VertexID):SV_Position { float2 u=float2((id<<1)&2,id&2); return float4(u.x*2-1,1-u.y*2,0.25,1); }"),
    new(ShaderStage.Fragment, "float4 main():SV_Target { return float4(1,0,0,1); }"));
using var far = renderer.Device.CreateProgram(
    new(ShaderStage.Vertex, "float4 main(uint id:SV_VertexID):SV_Position { float2 u=float2((id<<1)&2,id&2); return float4(u.x*2-1,1-u.y*2,0.75,1); }"),
    new(ShaderStage.Fragment, "float4 main():SV_Target { return float4(0,1,0,1); }"));
var pipeline = renderer.Pipeline;
for (int pass = 0; pass < 3; pass++)
{
    pipeline.SetRenderTargets([color], depth);
    pipeline.ClearRenderTargetColor(0, 0, 1, 15, new(0, 0, 1, 1));
    pipeline.ClearRenderTargetDepthStencil(0, 1, 1, true, 0, 0);
    pipeline.SetDepthTest(new(true, true, CompareOp.Less));
    pipeline.SetProgram(near); pipeline.Draw(3, 1, 0, 0);
    pipeline.SetProgram(far); pipeline.Draw(3, 1, 0, 0);
    using var data = color.GetData();
    var pixels = MemoryMarshal.Cast<byte, uint>(data.Get());
    for (int row = 0; row < 8; row++)
    for (int col = 0; col < 8; col++)
        if (pixels[row * 8 + col] != (row < 4 && col < 4 ? 0xff0000ffu : 0xffff0000u))
            throw new Exception($"Mismatched depth pass={pass}, pixel={col}/{row}, value={pixels[row * 8 + col]:X8}");
    using var depthData = depth.GetData();
    foreach (float value in MemoryMarshal.Cast<byte, float>(depthData.Get()))
        if (value != 0.25f) throw new Exception("Original depth did not retain near draw");

    // Exercise the utility blit after guest depth draws and readbacks, not only
    // after uploads. Presentation must not inherit the guest PSO/depth state.
    renderer.Window.SetSize(8, 8);
    renderer.Window.Present(color, new(0, 8, 0, 8, false, false, true, 1, 1), null);
    renderer.Screenshot();
    var presented = MemoryMarshal.Cast<byte, uint>(capture.AsSpan());
    for (int row = 0; row < 8; row++)
    for (int col = 0; col < 8; col++)
        if (presented[row * 8 + col] != (row < 4 && col < 4 ? 0xff0000ffu : 0xffff0000u))
            throw new Exception($"Presentation after depth draw failed: pass={pass}, pixel={col}/{row}");
}
color.Release(); depth.Release();
if (renderer.Device.GetInfo().DebugErrors != 0) throw new Exception("Depth proxy debug-layer errors");
Console.WriteLine("PASS smaller depth: occlusion, original depth readback, untouched color border, proxy reuse; debug layer clean.");
