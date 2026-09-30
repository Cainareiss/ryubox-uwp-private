param([Parameter(Mandatory=$true)][string] $NativeDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FpsOverlayTest {
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool SetDllDirectory(string path);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_InitializeForWindow")] public static extern int Init(IntPtr hwnd,uint w,uint h);
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_BeginCoreSession")] public static extern int Begin();
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_BindGraphicsThread")] public static extern int Bind();
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_EndCoreSession")] public static extern void End();
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_Shutdown")] public static extern void Shutdown();
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpFrontend_SetFpsEnabled")] public static extern void Enabled(int value);
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_UploadFrame")] public static extern int Upload(byte[] data,uint w,uint h,uint pitch);
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_CaptureBackBuffer")] public static extern int Capture(byte[] data,uint capacity,out uint w,out uint h,out uint pitch);
 [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint="RyujinxUwpGraphicsD3D11_ImGuiConsumeAction")] public static extern int Action();
 public static byte[] Background(int count) {
   byte[] data=new byte[count]; for(int i=0;i<count;i+=4) { data[i]=32; data[i+1]=64; data[i+2]=96; data[i+3]=255; } return data;
 }
 public static int[] Inspect(byte[] data,int w,int h) {
   int outside=0, text=0, panel=0;
   for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
     int i=(y*w+x)*4;
     bool box=x>=18 && x<169 && y>=18 && y<61;
     if(!box && (data[i]!=32 || data[i+1]!=64 || data[i+2]!=96)) outside++;
     if(box && data[i]>180 && data[i+1]>180 && data[i+2]>180) text++;
     if(box && data[i]<25 && data[i+1]<40 && data[i+2]<60) panel++;
   } return new int[]{outside,text,panel};
 }
}
'@
[void][FpsOverlayTest]::SetDllDirectory((Resolve-Path -LiteralPath $NativeDirectory).Path)
[void][FpsOverlayTest]::SetThreadDpiAwarenessContext([IntPtr](-4))
$form = New-Object Windows.Forms.Form
$form.Text = 'Ryubox FPS overlay validation - synthetic background, not gameplay'
$form.ClientSize = New-Object Drawing.Size(640,360)
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point(-32000,-32000)
$form.ShowInTaskbar = $false
$data=[FpsOverlayTest]::Background(640*360*4)
$capture=New-Object byte[] (640*360*4)
function Check-Hr([int]$hr) { if($hr -lt 0) { throw ('Native HRESULT 0x{0:X8}' -f $hr) } }
try {
 $form.Show()
 Check-Hr ([FpsOverlayTest]::Init($form.Handle,640,360))
 Check-Hr ([FpsOverlayTest]::Begin())
 Check-Hr ([FpsOverlayTest]::Bind())
 foreach($enabled in @(1,0,1)) {
   [FpsOverlayTest]::Enabled($enabled)
   for($i=0;$i -lt 35;$i++) {
     Check-Hr ([FpsOverlayTest]::Upload($data,640,360,2560))
     [Windows.Forms.Application]::DoEvents()
     Start-Sleep -Milliseconds 17
   }
   [uint32]$w=0; [uint32]$h=0; [uint32]$pitch=0
   Check-Hr ([FpsOverlayTest]::Capture($capture,$capture.Length,[ref]$w,[ref]$h,[ref]$pitch))
   $counts=[FpsOverlayTest]::Inspect($capture,$w,$h)
   if($counts[0] -ne 0) { throw "Overlay changed $($counts[0]) pixels outside its rectangle" }
   if($enabled -eq 1 -and ($counts[1] -lt 15 -or $counts[2] -lt 500)) { throw "FPS text/panel missing: $counts" }
   if($enabled -eq 0 -and ($counts[1] -ne 0 -or $counts[2] -ne 0)) { throw 'Disabled overlay still draws' }
   if([FpsOverlayTest]::Action() -ne -1) { throw 'Overlay generated a frontend action' }
   Write-Output "PASS enabled=$enabled whiteTextPixels=$($counts[1]) panelPixels=$($counts[2]) outsideChanges=0 noFrontendAction"
 }
} finally {
 [FpsOverlayTest]::End()
 [FpsOverlayTest]::Shutdown()
 $form.Close()
 $form.Dispose()
}
