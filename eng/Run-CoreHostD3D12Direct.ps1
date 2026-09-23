param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [int]$DurationSeconds = 600,
    [ValidateRange(1, 32)][int]$MemoryLimitGiB = 5,
    [string]$CapturePath = '',
    [ValidateRange(1, 100000)][int]$CaptureSequence = 1,
    [switch]$Hidden,
    [switch]$AutoPressA,
    [switch]$Audio,
    [switch]$DeepTrace
)

# Desktop diagnostic host only. Unlike run_pokemon_visible.ps1, this does not
# initialize the ImGui/UWP frontend or acquire its D3D12 lease. CoreHost owns
# the native Win32 D3D12 surface for the duration of this process.
$ErrorActionPreference = 'Stop'
$runtime = 'E:\ports\ryujinx uwp\src\Ryujinx.Uwp.App\bin\x64\Release'
$keys = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\system'
$game = (Resolve-Path -LiteralPath $GamePath).Path

$env:RYUJINX_UWP_PACKAGE_DIR = $runtime
$env:RYUJINX_UWP_LOCAL_WINDOW_TEST = '1'
$env:RYUJINX_UWP_LOCAL_WINDOW_VISIBLE = if ($Hidden) { '0' } else { '1' }
$env:RYUJINX_UWP_LOCAL_WINDOW_TITLE = "Ryubox Direct D3D12 Host - $([IO.Path]::GetFileNameWithoutExtension($game))"
$env:RYUJINX_UWP_DEEP_TRACE = if ($DeepTrace) { '1' } else { '0' }
if ([string]::IsNullOrWhiteSpace($CapturePath)) {
    Remove-Item Env:RYUJINX_UWP_CAPTURE_FRAME_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:RYUJINX_UWP_D3D12_CAPTURE_FRAME_MIN_SEQUENCE -ErrorAction SilentlyContinue
} else {
    $env:RYUJINX_UWP_CAPTURE_FRAME_PATH = [IO.Path]::GetFullPath($CapturePath)
    $env:RYUJINX_UWP_D3D12_CAPTURE_FRAME_MIN_SEQUENCE = [string]$CaptureSequence
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DirectCoreHost
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_StartWithHandle")]
    public static extern int Start(IntPtr game, IntPtr handle, IntPtr keys, IntPtr firmware, IntPtr backend, IntPtr settings, ulong limit, ulong expected);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_Stop")]
    public static extern void Stop();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_IsStarted")]
    public static extern int IsStarted();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_GetLastError")]
    public static extern int GetLastError(IntPtr buffer, int count);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_SetInput")]
    public static extern void SetInput(uint buttons, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger);
}
'@

function Get-CoreError {
    $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal(32768)
    try {
        [void][DirectCoreHost]::GetLastError($buffer, 16384)
        return [Runtime.InteropServices.Marshal]::PtrToStringUni($buffer)
    } finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
    }
}

[void][DirectCoreHost]::SetDllDirectory($runtime)
if ([DirectCoreHost]::LoadLibrary((Join-Path $runtime 'Ryujinx.Uwp.CoreHost.dll')) -eq [IntPtr]::Zero) {
    throw 'Nao foi possivel carregar Ryujinx.Uwp.CoreHost.dll do runtime Release.'
}

$allocated = [System.Collections.Generic.List[IntPtr]]::new()
$stream = $null
try {
    $stream = [IO.File]::Open($game, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $audioValue = if ($Audio) { 'true' } else { 'false' }
    $settings = '{"schemaVersion":1,"vsync":true,"gpuThreading":false,"audioEnabled":' + $audioValue + ',"fileLogging":true}'
    foreach ($value in @($game, $keys, '', 'D3D12', $settings)) {
        $allocated.Add([Runtime.InteropServices.Marshal]::StringToHGlobalUni($value))
    }

    $memoryLimit = [uint64]$MemoryLimitGiB * 1GB
    $hr = [DirectCoreHost]::Start($allocated[0], $stream.SafeFileHandle.DangerousGetHandle(), $allocated[1], $allocated[2], $allocated[3], $allocated[4], $memoryLimit, $memoryLimit)
    if ($hr -lt 0) {
        throw ('CoreHost direct start failed (0x{0:X8}): {1}' -f $hr, (Get-CoreError))
    }

    Write-Host 'CoreHost D3D12 directo iniciado; sem frontend ImGui/UWP.'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $nextAutoPressSeconds = 50.0
    $releaseAutoPressAt = -1.0
    while ([DirectCoreHost]::IsStarted() -ne 0 -and $clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        if ($AutoPressA -and $clock.Elapsed.TotalSeconds -ge $nextAutoPressSeconds) {
            [DirectCoreHost]::SetInput(1, 0, 0, 0, 0, 0, 0)
            $releaseAutoPressAt = $clock.Elapsed.TotalSeconds + 0.20
            $nextAutoPressSeconds += 8.0
        }
        if ($releaseAutoPressAt -ge 0 -and $clock.Elapsed.TotalSeconds -ge $releaseAutoPressAt) {
            [DirectCoreHost]::SetInput(0, 0, 0, 0, 0, 0, 0)
            $releaseAutoPressAt = -1.0
        }
        Start-Sleep -Milliseconds 250
    }
    if ([DirectCoreHost]::IsStarted() -eq 0) {
        Write-Warning ('CoreHost encerrou: ' + (Get-CoreError))
    }
} finally {
    [DirectCoreHost]::Stop()
    if ($stream) { $stream.Dispose() }
    foreach ($pointer in $allocated) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) }
}
