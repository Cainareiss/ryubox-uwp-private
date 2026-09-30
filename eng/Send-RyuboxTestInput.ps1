param(
    [Parameter(Mandatory=$true)][int] $ProcessId,
    [Parameter(Mandatory=$true)][int[]] $VirtualKeys,
    [ValidateRange(50,10000)][int] $HoldMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RyuboxScopedInput {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
'@
$testProcess = Get-Process -Id $ProcessId
$window = $testProcess.MainWindowHandle
if ($window -eq [IntPtr]::Zero -or $testProcess.MainWindowTitle -notlike 'Ryubox Direct D3D11 Host -*') {
    throw 'Input target is not the running Ryubox validation window.'
}
try {
    foreach ($key in $VirtualKeys) {
        if (-not [RyuboxScopedInput]::PostMessage($window, 0x100, [IntPtr]$key, [IntPtr]1)) {
            throw 'Failed to post test key down.'
        }
    }
    Start-Sleep -Milliseconds $HoldMilliseconds
} finally {
    foreach ($key in $VirtualKeys) {
        [void][RyuboxScopedInput]::PostMessage($window, 0x101, [IntPtr]$key, [IntPtr]1)
    }
}
