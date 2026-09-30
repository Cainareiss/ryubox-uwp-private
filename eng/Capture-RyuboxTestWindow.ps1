param(
    [Parameter(Mandatory = $true)][int] $ProcessId,
    [Parameter(Mandatory = $true)][string] $OutputPath,
    [switch] $PressA,
    [switch] $PrintWindow,
    [switch] $Background
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RyuboxWindowTest {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@

$testProcess = Get-Process -Id $ProcessId
$window = $testProcess.MainWindowHandle
if ($Background) {
    $window = [RyuboxWindowTest]::FindWindow("RyujinxUwpLocalVulkan_$ProcessId", [System.Management.Automation.Language.NullString]::Value)
    $PrintWindow = $true
}
if ($window -eq [IntPtr]::Zero) { throw 'The test process has no matching capture window.' }
[RyuboxWindowTest]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
if (-not $Background) { [RyuboxWindowTest]::SetForegroundWindow($window) | Out-Null }
if ($PressA) {
    # Hold the mapped A key across several guest input polls. SendKeys emits
    # down/up within a few milliseconds and can be missed by the guest.
    [RyuboxWindowTest]::PostMessage($window, 0x100, [IntPtr]0x5A, [IntPtr]1) | Out-Null
    Start-Sleep -Milliseconds 250
    [RyuboxWindowTest]::PostMessage($window, 0x101, [IntPtr]0x5A, [IntPtr]1) | Out-Null
}
Start-Sleep -Milliseconds 350
$rect = New-Object RyuboxWindowTest+RECT
if (-not [RyuboxWindowTest]::GetWindowRect($window, [ref]$rect)) { throw 'GetWindowRect failed.' }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    if ($PrintWindow) {
        $dc = $graphics.GetHdc()
        try {
            if (-not [RyuboxWindowTest]::PrintWindow($window, $dc, 2)) { throw 'PrintWindow failed.' }
        } finally { $graphics.ReleaseHdc($dc) }
    } else {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [System.Drawing.Size]::new($width, $height))
    }
    $bitmap.Save([IO.Path]::GetFullPath($OutputPath), [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length, LastWriteTime
