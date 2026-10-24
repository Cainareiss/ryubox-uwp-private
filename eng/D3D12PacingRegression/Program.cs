using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.D3D12;
using Ryujinx.Graphics.GAL;

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
var window = (D3D12Window)renderer.Window;
window.SetPresentationRateLimit(1);
uint[] pattern = new uint[64];
Array.Fill(pattern, 0xff0088ffu);
texture.SetData(MemoryOwner<byte>.RentCopy(MemoryMarshal.AsBytes(pattern.AsSpan())));
int callbacks = 0;
window.Present(texture, new(0, 8, 0, 8, false, false, true, 1, 1), () => callbacks++);
// Queued uploads must complete even when the rate gate skips the final blit.
for (int i = 0; i < 20; i++)
{
    Array.Fill(pattern, 0xff440000u + (uint)i);
    texture.SetData(MemoryOwner<byte>.RentCopy(MemoryMarshal.AsBytes(pattern.AsSpan())));
    window.Present(texture, new(0, 8, 0, 8, false, false, true, 1, 1), () => callbacks++);
}
if (callbacks != 21 || window.RateLimitedFrames == 0)
    throw new Exception($"Rate-limited callbacks: callbacks={callbacks}; skips={window.RateLimitedFrames}");
using (var data = texture.GetData())
    foreach (uint pixel in MemoryMarshal.Cast<byte, uint>(data.Get()))
        if (pixel != pattern[0]) throw new Exception("Skipped present did not preserve latest guest upload");
long skipped = window.RateLimitedFrames;
window.SetPresentationRateLimit(0);
if (window.RateLimitedFrames != 0) throw new Exception("Rate limit state not reset");
window.Present(texture, new(0, 8, 0, 8, false, false, true, 1, 1), () => callbacks++);
byte[] capture = null;
renderer.ScreenCaptured += (_, e) => capture = e.Data;
renderer.Screenshot();
if (callbacks != 22 || capture == null) throw new Exception("Unlimited presentation did not recover");
foreach (uint pixel in MemoryMarshal.Cast<byte, uint>(capture.AsSpan()))
    if (pixel != pattern[0]) throw new Exception("Unlimited presentation retained a stale image");
texture.Release();
if (renderer.Device.GetInfo().DebugErrors != 0) throw new Exception("D3D12 debug-layer errors");
Console.WriteLine($"PASS native D3D12: {skipped} rate-limited frames, every callback, latest guest upload, unlimited recovery, exact screenshot; debug layer clean.");
