param(
    [Parameter(Mandatory=$true)][string]$NativeDll,
    [switch]$Warp,
    [string]$CapturePath = ''
)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class D12FrontendTest {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr LoadLibrary(string path);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Initialize")] public static extern int Initialize(IntPtr w,IntPtr p,uint x,uint y,uint flags);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Render")] public static extern int Render();
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Resize")] public static extern int Resize(uint w,uint h);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Shutdown")] public static extern int Shutdown();
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Acquire")] public static extern int Acquire(ulong session,out IntPtr context);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Release")] public static extern int Release(ulong session);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12_BeginFrame")] public static extern int BeginFrame(IntPtr context,float r,float g,float b,float a);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12_EndFrame")] public static extern int EndFrame(IntPtr context,uint interval,out ulong fence);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_SetInput")] public static extern int Input(uint buttons,float lx,float ly,float rx,float ry,float lt,float rt);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_SetCatalogue",CharSet=CharSet.Unicode)] public static extern int Catalogue(string titles);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_SetCover")] public static extern int Cover(int i,byte[] pixels,uint w,uint h,uint pitch);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_ConsumeAction")] public static extern int Action(out int action);
 [StructLayout(LayoutKind.Sequential)] public struct Info {
 public uint Size,Abi,Vendor,Device,Level,Tier,Width,Height;
 public ulong Submitted,Completed,LiveBytes,UploadBytes,Waits;
 public int Removed; public uint DebugErrors;
 }
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Capture")] public static extern int Capture(byte[] pixels,ulong bytes,ref Info info);
}
'@
function Check([int]$result,[string]$operation) { if($result -lt 0){throw ('{0}: HRESULT 0x{1:X8}' -f $operation,$result)} }
if([D12FrontendTest]::LoadLibrary((Resolve-Path -LiteralPath $NativeDll).Path) -eq [IntPtr]::Zero){throw 'Native DLL load failed'}
$flags=1
if($Warp){$flags=$flags -bor 2}
$leased=$false
Check ([D12FrontendTest]::Initialize([IntPtr]::Zero,[IntPtr]::Zero,640,360,$flags)) 'initialize'
try {
 Check ([D12FrontendTest]::Catalogue("Sonic Mania`nRivals of Aether")) 'catalogue'
 $cover=New-Object byte[] (16*16*4)
 for($i=0;$i -lt $cover.Length;$i+=4){$cover[$i]=255;$cover[$i+3]=255}
 Check ([D12FrontendTest]::Cover(0,$cover,16,16,64)) 'cover'
 Check ([D12FrontendTest]::Input(0,0,0,0,0,0,0)) 'neutral'
 for($i=0;$i -lt 12;$i++){Check ([D12FrontendTest]::Render()) 'render'}
 $pixels=New-Object byte[] (640*360*4)
 $info=New-Object D12FrontendTest+Info
 $info.Size=[Runtime.InteropServices.Marshal]::SizeOf($info)
 Check ([D12FrontendTest]::Capture($pixels,$pixels.Length,[ref]$info)) 'capture'
 if($info.DebugErrors -ne 0 -or $info.Removed -lt 0){throw 'D3D12 debug/device error'}
 $bright=0; $red=0
 for($i=0;$i -lt $pixels.Length;$i+=4){
  if($pixels[$i] -gt 120 -or $pixels[$i+1] -gt 120 -or $pixels[$i+2] -gt 120){$bright++}
  if($pixels[$i] -gt 180 -and $pixels[$i+1] -lt 50 -and $pixels[$i+2] -lt 50){$red++}
 }
 if($bright -lt 100 -or $red -lt 30){throw "Missing UI/cover pixels: bright=$bright red=$red"}
 if($CapturePath){
  Add-Type -AssemblyName System.Drawing
  $bmp=New-Object Drawing.Bitmap 640,360
  $rect=New-Object Drawing.Rectangle 0,0,640,360
  $bits=$bmp.LockBits($rect,[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  try {
   for($i=0;$i -lt $pixels.Length;$i+=4){$r=$pixels[$i];$pixels[$i]=$pixels[$i+2];$pixels[$i+2]=$r}
   [Runtime.InteropServices.Marshal]::Copy($pixels,0,$bits.Scan0,$pixels.Length)
  } finally {$bmp.UnlockBits($bits)}
  try {$bmp.Save([IO.Path]::GetFullPath($CapturePath),[Drawing.Imaging.ImageFormat]::Png)} finally {$bmp.Dispose()}
 }
 $context=[IntPtr]::Zero
 Check ([D12FrontendTest]::Acquire(42,[ref]$context)) 'acquire'
 $leased=$true
 if($context -eq [IntPtr]::Zero){throw 'Null lease'}
 if([D12FrontendTest]::Render() -ge 0){throw 'Frontend rendered during guest lease'}
 if([D12FrontendTest]::Shutdown() -ge 0){throw 'Shutdown invalidated guest lease'}
 if([D12FrontendTest]::Release(43) -ge 0){throw 'Stale session returned ownership'}
 Check ([D12FrontendTest]::Resize(800,450)) 'deferred resize during guest'
 Check ([D12FrontendTest]::Input(16,0,0,0,0,0,0)) 'input during guest'
 Check ([D12FrontendTest]::BeginFrame($context,0,0,1,1)) 'guest-owned frame'
 $fence=[uint64]0
 Check ([D12FrontendTest]::EndFrame($context,0,[ref]$fence)) 'guest present'
 Check ([D12FrontendTest]::Release(42)) 'release'; $leased=$false
 $action=-1
 Check ([D12FrontendTest]::Action([ref]$action)) 'action'
 if($action -ne -1){throw 'Guest input leaked'}
 if([D12FrontendTest]::Input(16,0,0,0,0,0,0) -ne 1){throw 'Missing release barrier'}
 Check ([D12FrontendTest]::Input(0,0,0,0,0,0,0)) 'release buttons'
 Check ([D12FrontendTest]::Render()) 'return to frontend'
 $resized=New-Object byte[] (800*450*4)
 Check ([D12FrontendTest]::Capture($resized,$resized.Length,[ref]$info)) 'resized capture'
 if($info.Width -ne 800 -or $info.Height -ne 450 -or $info.DebugErrors -ne 0){throw 'Deferred resize/debug validation failed'}
 "PASS D3D12 ImGui: UI pixels=$bright cover pixels=$red; lease isolation, stale release, resize, return; debugErrors=$($info.DebugErrors)"
} finally {
 if($leased){[D12FrontendTest]::Release(42) | Out-Null}
 Check ([D12FrontendTest]::Shutdown()) 'shutdown'
}
Check ([D12FrontendTest]::Initialize([IntPtr]::Zero,[IntPtr]::Zero,640,360,$flags)) 'second initialize'
try { Check ([D12FrontendTest]::Render()) 'second session render' }
finally { Check ([D12FrontendTest]::Shutdown()) 'second shutdown' }
'PASS repeated frontend lifecycle and explicit worker shutdown'
