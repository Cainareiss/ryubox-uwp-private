param(
 [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
 [Parameter(Mandatory=$true)][string]$GamePath,
 [Parameter(Mandatory=$true)][string]$KeysDirectory,
 [Parameter(Mandatory=$true)][string]$CapturePath,
 [int]$CaptureSequence=30,
 [int]$Seconds=90,
 [ValidateRange(1,8)][int]$MemoryLimitGiB = $(if ($env:RYUJINX_UWP_MEMORY_6GIB -eq '1') { 6 } else { 5 }),
 [switch]$NoInput
)
$ErrorActionPreference='Stop'
$runtime=(Resolve-Path -LiteralPath $RuntimeDirectory).Path
$game=(Resolve-Path -LiteralPath $GamePath).Path
$keys=(Resolve-Path -LiteralPath $KeysDirectory).Path
$env:RYUJINX_UWP_PACKAGE_DIR=$runtime
$env:RYUJINX_UWP_LOCAL_WINDOW_TEST='0'
$env:RYUJINX_UWP_CAPTURE_FRAME_PATH=[IO.Path]::GetFullPath($CapturePath)
$env:RYUJINX_UWP_D3D12_CAPTURE_FRAME_MIN_SEQUENCE=[string][Math]::Max(1,$CaptureSequence)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FrontendCoreTest {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern bool SetDllDirectory(string path);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr LoadLibrary(string path);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Initialize")] public static extern int Initialize(IntPtr window,IntPtr panel,uint w,uint h,uint flags);
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Render")] public static extern int Render();
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_GetDeviceStatus")] public static extern int Status();
 [DllImport("Ryujinx.Uwp.Native.dll",EntryPoint="RyuboxD3D12Frontend_Shutdown")] public static extern int Shutdown();
 [DllImport("Ryujinx.Uwp.CoreHost.dll",EntryPoint="RyujinxUwpCoreHost_StartWithHandle")]
 public static extern int Start(IntPtr game,IntPtr handle,IntPtr keys,IntPtr firmware,IntPtr backend,IntPtr settings,ulong limit,ulong expected);
 [DllImport("Ryujinx.Uwp.CoreHost.dll",EntryPoint="RyujinxUwpCoreHost_Stop")] public static extern void Stop();
 [DllImport("Ryujinx.Uwp.CoreHost.dll",EntryPoint="RyujinxUwpCoreHost_IsStarted")] public static extern int Started();
 [DllImport("Ryujinx.Uwp.CoreHost.dll",EntryPoint="RyujinxUwpCoreHost_GetLastError")] public static extern int Error(IntPtr buffer,int count);
 [DllImport("Ryujinx.Uwp.CoreHost.dll",EntryPoint="RyujinxUwpCoreHost_SetInput")] public static extern void SetInput(uint buttons, float lx, float ly, float rx, float ry, float lt, float rt);
}
'@
function Check([int]$hr,[string]$stage){if($hr -lt 0){throw ('{0} HRESULT=0x{1:X8}' -f $stage,$hr)}}
function CoreError {
 $buffer=[Runtime.InteropServices.Marshal]::AllocHGlobal(32768)
 try { [void][FrontendCoreTest]::Error($buffer,16384); [Runtime.InteropServices.Marshal]::PtrToStringUni($buffer) }
 finally {[Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)}
}
[void][FrontendCoreTest]::SetDllDirectory($runtime)
if([FrontendCoreTest]::LoadLibrary((Join-Path $runtime 'Ryujinx.Uwp.Native.dll')) -eq [IntPtr]::Zero){throw 'Cannot load native DLL'}
Check ([FrontendCoreTest]::Initialize([IntPtr]::Zero,[IntPtr]::Zero,1280,720,1)) 'frontend initialize'
$allocated=New-Object 'System.Collections.Generic.List[IntPtr]'
$stream=$null
try {
 Check ([FrontendCoreTest]::Render()) 'frontend before game'
 # Production handoff, no LOCAL_WINDOW_TEST, using an owned file handle.
 $stream=[IO.File]::Open($game,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 foreach($value in @($game,$keys,'','D3D12','{"schemaVersion":1,"vsync":true,"gpuThreading":false,"audioEnabled":false,"fileLogging":true}')) {
  $allocated.Add([Runtime.InteropServices.Marshal]::StringToHGlobalUni($value))
 }
 $clock=[Diagnostics.Stopwatch]::StartNew()
 $memoryLimit=[uint64]$MemoryLimitGiB * 1GB
 $hr=[FrontendCoreTest]::Start($allocated[0],$stream.SafeFileHandle.DangerousGetHandle(),$allocated[1],$allocated[2],$allocated[3],$allocated[4],$memoryLimit,$memoryLimit)
 if($hr -lt 0){throw ('CoreHost start 0x{0:X8}: {1}' -f $hr,(CoreError))}
 'CoreHost start succeeded in {0:N2}s; backend=D3D12; frontend context leased' -f $clock.Elapsed.TotalSeconds
 if([FrontendCoreTest]::Render() -ne [int]-2147024726){throw 'Frontend submitted during CoreHost lease'}
 $deadline=$clock.Elapsed.TotalSeconds+$Seconds
 $captureSeenAt=-1
 while($clock.Elapsed.TotalSeconds -lt $deadline){
  if([FrontendCoreTest]::Started() -eq 0){throw ('CoreHost stopped: '+(CoreError))}
  if(-not $NoInput -and $clock.Elapsed.TotalSeconds -ge 15){
   $pulse = if (([int]($clock.Elapsed.TotalSeconds * 2)) % 2 -eq 1) { [uint32]1 } else { [uint32]0 }
   [FrontendCoreTest]::SetInput($pulse, 0, 0, 0, 0, 0, 0)
  }
  if(Test-Path -LiteralPath $CapturePath -PathType Leaf){
   if($captureSeenAt -lt 0){$captureSeenAt=$clock.Elapsed.TotalSeconds; 'Guest capture appeared at {0:N2}s' -f $captureSeenAt}
   if($clock.Elapsed.TotalSeconds -gt $captureSeenAt+15){break}
  }
  Start-Sleep -Milliseconds 500
 }
 [FrontendCoreTest]::Stop()
 Check ([FrontendCoreTest]::Status()) 'device after CoreHost cleanup'
 Check ([FrontendCoreTest]::Render()) 'frontend restored'
 if(-not (Test-Path -LiteralPath $CapturePath -PathType Leaf)){throw 'No guest capture produced; not a visual PASS'}
 'PASS CoreHost frontend lease, guest capture, cleanup and return to menu (offscreen; not UWP/Xbox validation).'
} finally {
 try {
  [FrontendCoreTest]::Stop()
  if($stream){$stream.Dispose()}
  foreach($pointer in $allocated){[Runtime.InteropServices.Marshal]::FreeHGlobal($pointer)}
  for($retry=0; $retry -lt 10; $retry++){
   $hr=[FrontendCoreTest]::Shutdown()
   if($hr -ge 0 -or $hr -ne [int]-2147024726){ break }
   Start-Sleep -Milliseconds 500
  }
  Check $hr 'frontend shutdown'
 } catch {
  Write-Warning ("Cleanup error in finally: " + $_.Exception.Message)
 }
}
