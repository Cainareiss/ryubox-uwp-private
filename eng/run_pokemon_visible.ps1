param(
    [string]$GamePath = '',
    [int]$DurationSeconds = 0,
    [switch]$Audio
)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $gameItem = Get-ChildItem -Path 'E:\jogos para testar ryuboxz\*Legends*Z-A*.nsp' | Select-Object -First 1
    if ($null -eq $gameItem) {
        throw "Arquivo NSP do Pokémon Legends: Z-A não foi encontrado."
    }
    $GamePath = $gameItem.FullName
}

$runtime = 'E:\ports\ryujinx uwp\src\Ryujinx.Uwp.App\bin\x64\Release'
$keys = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\system'

Write-Host "=================================================="
Write-Host "Ryubox Live D3D12 Window Host"
Write-Host "Game: $GamePath"
Write-Host "Runtime: $runtime"
Write-Host "Keys: $keys"
Write-Host "=================================================="

$env:RYUJINX_UWP_PACKAGE_DIR = $runtime
$env:RYUJINX_UWP_LOCAL_WINDOW_TEST = '0'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class FrontendCoreWindow {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12Frontend_Initialize")]
    public static extern int Initialize(IntPtr window, IntPtr panel, uint w, uint h, uint flags);

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12Frontend_Render")]
    public static extern int Render();

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12Frontend_GetDeviceStatus")]
    public static extern int Status();

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12Frontend_Shutdown")]
    public static extern int Shutdown();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_StartWithHandle")]
    public static extern int Start(IntPtr game, IntPtr handle, IntPtr keys, IntPtr firmware, IntPtr backend, IntPtr settings, ulong limit, ulong expected);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_Stop")]
    public static extern void Stop();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_IsStarted")]
    public static extern int Started();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_GetLastError")]
    public static extern int Error(IntPtr buffer, int count);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_SetInput")]
    public static extern void SetInput(uint buttons, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger);
}
'@

function Check([int]$hr, [string]$stage) {
    if ($hr -lt 0) {
        throw ('{0} HRESULT=0x{1:X8}' -f $stage, $hr)
    }
}

function CoreError {
    $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal(32768)
    try {
        [void][FrontendCoreWindow]::Error($buffer, 16384)
        return [Runtime.InteropServices.Marshal]::PtrToStringUni($buffer)
    } finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
    }
}

# Ensure per-monitor DPI awareness so the 1280x720 window matches physical pixels 1:1.
try {
    [void][FrontendCoreWindow]::SetProcessDpiAwarenessContext([IntPtr](-4))
} catch { }

[void][FrontendCoreWindow]::SetDllDirectory($runtime)
if ([FrontendCoreWindow]::LoadLibrary((Join-Path $runtime 'Ryujinx.Uwp.Native.dll')) -eq [IntPtr]::Zero) {
    throw 'Não foi possível carregar Ryujinx.Uwp.Native.dll'
}

# Create the visible host Form
$form = New-Object System.Windows.Forms.Form
$form.Text = "Ryubox D3D12 - Pokémon Legends: Z-A"
$form.ClientSize = New-Object System.Drawing.Size(1280, 720)
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.BackColor = [System.Drawing.Color]::Black
$form.KeyPreview = $true
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

Write-Host "Initializing D3D12 frontend on visible HWND 0x$($form.Handle.ToString('X'))..."
Check ([FrontendCoreWindow]::Initialize($form.Handle, [IntPtr]::Zero, 1280, 720, 1)) 'frontend initialize'
Check ([FrontendCoreWindow]::Render()) 'frontend initial render'

# Keyboard input forwarding
$heldKeys = New-Object 'System.Collections.Generic.HashSet[System.Windows.Forms.Keys]'

$updateInput = {
    $buttons = [uint32]0
    $lx = 0.0; $ly = 0.0
    $rx = 0.0; $ry = 0.0
    $lt = 0.0; $rt = 0.0

    if ($heldKeys.Contains([System.Windows.Forms.Keys]::Z) -or $heldKeys.Contains([System.Windows.Forms.Keys]::Enter)) { $buttons = $buttons -bor 0x01 } # A
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::X) -or $heldKeys.Contains([System.Windows.Forms.Keys]::Escape)) { $buttons = $buttons -bor 0x02 } # B
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::C)) { $buttons = $buttons -bor 0x04 } # X
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::V)) { $buttons = $buttons -bor 0x08 } # Y
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::Up)) { $buttons = $buttons -bor 0x10 } # Up
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::Down)) { $buttons = $buttons -bor 0x20 } # Down
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::Left)) { $buttons = $buttons -bor 0x40 } # Left
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::Right)) { $buttons = $buttons -bor 0x80 } # Right
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::O)) { $buttons = $buttons -bor 0x100 } # Minus
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::P)) { $buttons = $buttons -bor 0x200 } # Plus
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::Q)) { $buttons = $buttons -bor 0x400 } # L
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::E)) { $buttons = $buttons -bor 0x800 } # R

    if ($heldKeys.Contains([System.Windows.Forms.Keys]::W)) { $ly += 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::S)) { $ly -= 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::A)) { $lx -= 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::D)) { $lx += 1.0 }

    if ($heldKeys.Contains([System.Windows.Forms.Keys]::I)) { $ry += 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::K)) { $ry -= 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::J)) { $rx -= 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::L)) { $rx += 1.0 }

    if ($heldKeys.Contains([System.Windows.Forms.Keys]::D1)) { $lt = 1.0 }
    if ($heldKeys.Contains([System.Windows.Forms.Keys]::D3)) { $rt = 1.0 }

    [FrontendCoreWindow]::SetInput($buttons, $lx, $ly, $rx, $ry, $lt, $rt)
}

$form.Add_KeyDown({
    param($s, $e)
    if ($heldKeys.Add($e.KeyCode)) { & $updateInput }
    $e.Handled = $true
})
$form.Add_KeyUp({
    param($s, $e)
    if ($heldKeys.Remove($e.KeyCode)) { & $updateInput }
    $e.Handled = $true
})

$allocated = New-Object 'System.Collections.Generic.List[IntPtr]'
$stream = $null

try {
    $stream = [IO.File]::Open($GamePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $audioFlag = if ($Audio) { 'true' } else { 'false' }
    $settingsJson = '{"schemaVersion":1,"vsync":true,"gpuThreading":false,"audioEnabled":' + $audioFlag + ',"fileLogging":true}'

    foreach ($value in @($GamePath, $keys, '', 'D3D12', $settingsJson)) {
        $allocated.Add([Runtime.InteropServices.Marshal]::StringToHGlobalUni($value))
    }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $memoryLimit = [uint64](5GB)

    Write-Host "Starting CoreHost with leased D3D12 context on visible window..."
    $hr = [FrontendCoreWindow]::Start(
        $allocated[0],
        $stream.SafeFileHandle.DangerousGetHandle(),
        $allocated[1],
        $allocated[2],
        $allocated[3],
        $allocated[4],
        $memoryLimit,
        $memoryLimit
    )

    if ($hr -lt 0) {
        throw ('CoreHost start falhou (0x{0:X8}): {1}' -f $hr, (CoreError))
    }

    Write-Host "CoreHost started successfully! Window is active on desktop."
    Write-Host "Controls: WASD (Left Stick), Enter/Z (A), Esc/X (B), C (X), V (Y), Arrows (D-Pad), P (+), O (-)"
    Write-Host "Close the window or press Ctrl+C in this console to stop."

    $form.Activate()

    while ($form.Created -and [FrontendCoreWindow]::Started() -ne 0) {
        [System.Windows.Forms.Application]::DoEvents()
        if ($DurationSeconds -gt 0 -and $clock.Elapsed.TotalSeconds -ge $DurationSeconds) {
            Write-Host "Duration limit of $DurationSeconds seconds reached."
            break
        }
        Start-Sleep -Milliseconds 16
    }
} finally {
    Write-Host "Stopping and cleaning up..."
    try {
        [FrontendCoreWindow]::Stop()
        if ($stream) { $stream.Dispose() }
        foreach ($ptr in $allocated) { [Runtime.InteropServices.Marshal]::FreeHGlobal($ptr) }
        for ($retry = 0; $retry -lt 10; $retry++) {
            $hr = [FrontendCoreWindow]::Shutdown()
            if ($hr -ge 0 -or $hr -ne [int]-2147024726) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($form -and $form.Created) {
            $form.Close()
            $form.Dispose()
        }
        Write-Host "Cleanup completed."
    } catch {
        Write-Warning ("Cleanup error: " + $_.Exception.Message)
    }
}
