param(
    [Parameter(Mandatory = $true)]
    [string] $GamePath,
    [string] $GamePathBase64 = '',
    [string] $KeysDirectory = '',
    [string] $FirmwarePath = '',
    [string] $RuntimeDirectory = '',
    [string] $NativeDllPath = '',
    [ValidateSet('D3D11', 'D3D12')]
    [string] $Backend = 'D3D11',
    [string] $CaptureFramePath = '',
    [switch] $KeepOpenAfterCapture,
    [switch] $EnableD3D11Debug,
    [switch] $EnableD3D11Telemetry,
    [switch] $LegacyIntelCompatibility,
    [switch] $Background,
    [switch] $Child,
    [switch] $AutoStartMatch,
    [int] $AutoStartStopSeconds = 110,
    [switch] $PerformanceMode,
    [switch] $EnablePtc,
    [switch] $ForceFullscreenDiagnostic,
    [switch] $EnableGuestInternet,
    [switch] $EnableGuestLogs
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Child -and -not [string]::IsNullOrWhiteSpace($GamePathBase64)) {
    $GamePath = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($GamePathBase64))
} elseif ($Child -and $GamePath -eq '__RYUBOX_ENV_GAME__') {
    $GamePath = $env:RYUBOX_DIRECT_GAME_PATH
}
$game = (Resolve-Path -LiteralPath $GamePath).Path

if ([string]::IsNullOrWhiteSpace($KeysDirectory)) {
    $package = Get-AppxPackage -Name 'Ryujinx.Xbox.UWP' | Select-Object -First 1
    if ($null -eq $package) {
        throw 'Ryubox não está instalado e o diretório das keys não foi informado.'
    }

    $KeysDirectory = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\system"
}
$keys = (Resolve-Path -LiteralPath $KeysDirectory).Path

foreach ($keyName in @('prod.keys', 'title.keys')) {
    $keyPath = Join-Path $keys $keyName
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        throw "Chave ausente: $keyPath"
    }
}

if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    $RuntimeDirectory = Join-Path $repoRoot 'artifacts\direct-d3d11-host'
}
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)

if (-not $Child) {
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null

    $nativeDll = if ([string]::IsNullOrWhiteSpace($NativeDllPath)) {
        Join-Path $repoRoot 'src\Ryujinx.Uwp.Native\bin\x64\Release\Ryujinx.Uwp.Native.dll'
    } else {
        (Resolve-Path -LiteralPath $NativeDllPath).Path
    }

    $assets = @{
        # dotnet publish does not add the MSBuild platform segment to this
        # managed project's output path. Using bin\x64 silently selected an
        # obsolete CoreHost during direct-host validation.
        'Ryujinx.Uwp.CoreHost.dll' = Join-Path $repoRoot 'src\Ryujinx.Uwp.CoreHost\bin\Release\net10.0\win-x64\publish\Ryujinx.Uwp.CoreHost.dll'
        'Ryujinx.Uwp.Native.dll' = $nativeDll
        # Keep the direct host isolated from the installed package. These
        # managed assemblies contain the D3D11 texture-compatibility changes;
        # omitting them silently makes the host resolve stale copies from the
        # package and invalidates local rendering tests.
        'Ryujinx.Graphics.Gpu.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.Gpu\bin\Release\net10.0\Ryujinx.Graphics.Gpu.dll'
        'Ryujinx.Graphics.D3D11.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.D3D11\bin\Release\net10.0\Ryujinx.Graphics.D3D11.dll'
        'Ryujinx.Graphics.D3D12.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.D3D12\bin\Release\net10.0\Ryujinx.Graphics.D3D12.dll'
        'Ryujinx.Graphics.Texture.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.Texture\bin\Release\net10.0\Ryujinx.Graphics.Texture.dll'
        'Ryujinx.Graphics.GAL.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.GAL\bin\Release\net10.0\Ryujinx.Graphics.GAL.dll'
        'Ryujinx.Graphics.Shader.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.Shader\bin\Release\net10.0\Ryujinx.Graphics.Shader.dll'
        'Ryujinx.Common.dll' = Join-Path $repoRoot 'src\src\Ryujinx.Common\bin\Release\net10.0\Ryujinx.Common.dll'
        'spirv-cross-c-shared.dll' = Join-Path $repoRoot 'third_party\SPIRV-Cross\build-uwp-x64-msbuild\spirv-cross-c-shared.dll'
    }
    foreach ($entry in $assets.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
            throw "Runtime ausente: $($entry.Value)"
        }
        Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $runtime $entry.Key) -Force
    }

    $ffmpegDirectory = Join-Path $repoRoot 'third_party\ffmpeg-5.1.6-win64-lgpl-shared\package\ffmpeg-n5.1.6-16-g6e63e49496-win64-lgpl-shared-5.1\bin'
    foreach ($ffmpegDll in @('avcodec-59.dll', 'avutil-57.dll', 'swresample-4.dll', 'swscale-6.dll', 'avformat-59.dll')) {
        $ffmpegPath = Join-Path $ffmpegDirectory $ffmpegDll
        if (Test-Path -LiteralPath $ffmpegPath -PathType Leaf) {
            Copy-Item -LiteralPath $ffmpegPath -Destination (Join-Path $runtime $ffmpegDll) -Force
        }
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stdout = Join-Path $runtime "direct-host-$stamp.stdout.log"
    $stderr = Join-Path $runtime "direct-host-$stamp.stderr.log"
    $powershell = (Get-Command powershell.exe).Source
    # Passing a long NSP path containing nested parentheses and bracketed title
    # metadata through Start-Process's flattened command line can arrive as an
    # empty -GamePath on Windows PowerShell. The child inherits this process
    # environment, which preserves the path byte-for-byte.
    $env:RYUBOX_DIRECT_GAME_PATH = $game
    $encodedGamePath = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($game))
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-STA',
        '-File', ('"' + $PSCommandPath + '"'),
        '-Child',
        '-GamePath', '__RYUBOX_ENV_GAME__',
        '-GamePathBase64', $encodedGamePath,
        '-KeysDirectory', ('"' + $keys + '"'),
        '-RuntimeDirectory', ('"' + $runtime + '"'),
        '-Backend', $Backend
    ) -join ' '
    if ($EnableD3D11Debug) {
        $arguments += ' -EnableD3D11Debug'
    }
    if ($EnableD3D11Telemetry) {
        $arguments += ' -EnableD3D11Telemetry'
    }
    if ($LegacyIntelCompatibility) {
        $arguments += ' -LegacyIntelCompatibility'
    }
    if ($Background) {
        $arguments += ' -Background'
    }
    if ($AutoStartMatch) {
        $arguments += ' -AutoStartMatch'
        $arguments += ' -AutoStartStopSeconds ' + [Math]::Max(31, $AutoStartStopSeconds)
    }
    if ($PerformanceMode) {
        $arguments += ' -PerformanceMode'
    }
    if ($KeepOpenAfterCapture) {
        $arguments += ' -KeepOpenAfterCapture'
    }
    if ($EnablePtc) {
        $arguments += ' -EnablePtc'
    }
    if ($ForceFullscreenDiagnostic) {
        $arguments += ' -ForceFullscreenDiagnostic'
    }
    if ($EnableGuestInternet) {
        $arguments += ' -EnableGuestInternet'
    }
    if ($EnableGuestLogs) {
        $arguments += ' -EnableGuestLogs'
    }
    if (-not [string]::IsNullOrWhiteSpace($FirmwarePath)) {
        $resolvedFirmware = (Resolve-Path -LiteralPath $FirmwarePath).Path
        $arguments += ' -FirmwarePath ' + ('"' + $resolvedFirmware + '"')
    }
    if (-not [string]::IsNullOrWhiteSpace($CaptureFramePath)) {
        $arguments += ' -CaptureFramePath ' + ('"' + [IO.Path]::GetFullPath($CaptureFramePath) + '"')
    }

    # This is an interactive renderer validation. A hidden PowerShell startup
    # state also hides the WinForms HWND used by the D3D11 swap chain, leaving
    # the title running correctly but invisible to the tester.
    $windowStyle = if ($Background) { 'Hidden' } else { 'Normal' }
    $process = Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle $windowStyle -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Write-Output "DIRECT_HOST_PID=$($process.Id)"
    Write-Output "DIRECT_HOST_STDOUT=$stdout"
    Write-Output "DIRECT_HOST_STDERR=$stderr"
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$interop = @'
using System;
using System.Runtime.InteropServices;

public static class RyuboxDirectNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string path);

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_InitializeForWindow", CallingConvention = CallingConvention.StdCall)]
    public static extern int InitializeForWindow(IntPtr window, uint width, uint height);

    [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_Shutdown", CallingConvention = CallingConvention.StdCall)]
    public static extern void ShutdownGraphics();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_Start", CallingConvention = CallingConvention.StdCall)]
    public static extern int Start(IntPtr game, IntPtr keys, IntPtr firmware, IntPtr backend, IntPtr settingsJson, ulong currentMemoryLimit, ulong expectedMemoryLimit);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_Stop", CallingConvention = CallingConvention.StdCall)]
    public static extern void Stop();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_IsStarted", CallingConvention = CallingConvention.StdCall)]
    public static extern int IsStarted();

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_SetInput", CallingConvention = CallingConvention.StdCall)]
    public static extern void SetInput(uint buttons, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger);

    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint = "RyujinxUwpCoreHost_GetLastError", CallingConvention = CallingConvention.StdCall)]
    public static extern int GetLastError(IntPtr buffer, int capacity);
}
'@
Add-Type -TypeDefinition $interop

# Windows PowerShell is DPI-unaware by default on systems using 125/150/200%
# display scaling. In that mode an HWND requested at 1280x720 can have a
# 1920x1080 physical client while the D3D11 swap chain remains 1280x720,
# producing a small image in the upper-left during desktop validation. UWP is
# already DPI-aware; make the direct-host harness equivalent before creating
# the WinForms HWND. DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == -4.
try {
    [RyuboxDirectNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
} catch {
    # Older Windows builds do not expose the API. The renderer remains usable,
    # but the validation window can be DPI-virtualized on those systems.
}
# powershell.exe already owns a console HWND, so Windows can reject a late
# process-wide DPI mode change with ERROR_ACCESS_DENIED. A per-thread PMv2
# context is still legal and ensures that the subsequently-created WinForms
# client and the DXGI HWND swap chain use the same physical pixel dimensions.
try {
    [RyuboxDirectNative]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
} catch {
}

$env:RYUJINX_UWP_PACKAGE_DIR = $runtime
$env:RYUJINX_UWP_GPFIFO_DETAIL_TELEMETRY = if ($EnableD3D11Telemetry) { '1' } else { '0' }
$env:RYUJINX_UWP_CONTINUOUS_WATCHDOG = '1'
# Match the Xbox Safe-JIT profile by default. The faster unmanaged/high-CQ
# path is an explicit opt-in for desktop-only benchmarking.
if ($PerformanceMode) {
    $env:RYUJINX_UWP_LOCAL_PERFORMANCE_TEST = '1'
} else {
    Remove-Item Env:RYUJINX_UWP_LOCAL_PERFORMANCE_TEST -ErrorAction SilentlyContinue
}
$env:RYUJINX_UWP_FAST_SHADER_COMPILE = '1'
$env:RYUJINX_UWP_D3D11_DEBUG = if ($EnableD3D11Debug) { '1' } else { '0' }
$env:RYUJINX_UWP_D3D11_TELEMETRY = if ($EnableD3D11Telemetry) { '1' } else { '0' }
$env:RYUJINX_UWP_D3D11_PRESENT_MATCHING_RT = if ($env:RYUJINX_UWP_D3D11_PRESENT_MATCHING_RT -eq '0') { '0' } else { '1' }
$env:RYUJINX_UWP_D3D11_PRESENT_LAST_RT = if ($env:RYUJINX_UWP_D3D11_PRESENT_LAST_RT -eq '1') { '1' } else { '0' }
$env:RYUJINX_UWP_D3D11_FORCE_FULLSCREEN_DIAGNOSTIC = if ($ForceFullscreenDiagnostic) { '1' } else { '0' }
$env:RYUJINX_UWP_TEST_ENABLE_PTC = if ($EnablePtc) { '1' } else { '0' }
if ($Backend -eq 'D3D12') {
    # CoreHost D3D12 owns a separate native Win32 swap chain during this
    # desktop-only smoke path. The UWP SwapChainPanel route remains disabled.
    $env:RYUJINX_UWP_LOCAL_WINDOW_TEST = '1'
    # An off-screen validation must also hide the CoreHost-owned D3D12 HWND,
    # not only this WinForms lifecycle host. Interactive D3D12 runs retain a
    # visible native swap-chain window.
    $env:RYUJINX_UWP_LOCAL_WINDOW_VISIBLE = if ($Background) { '0' } else { '1' }
    # The D3D12 swap-chain HWND owns focus. Let CoreHost translate keyboard
    # messages on that window directly instead of relying on this hidden form.
    $env:RYUJINX_UWP_LOCAL_WINDOW_KEYBOARD = '1'
    # Diagnostic escape hatch for legacy Intel UMD crashes, NOT equivalent
    # rendering: identity SRVs lose guest component mappings and omitted
    # guest counters can change observable behavior. Never silently apply it
    # to every adapter or count this mode as a correctness validation.
    $env:RYUJINX_UWP_D3D12_DISABLE_COMPONENT_SWIZZLE = if ($LegacyIntelCompatibility) { '1' } else { '0' }
    $env:RYUJINX_UWP_D3D12_DISABLE_GUEST_COUNTERS = if ($LegacyIntelCompatibility) { '1' } else { '0' }
    Write-Output "D3D12_LEGACY_INTEL_COMPATIBILITY=$([bool]$LegacyIntelCompatibility)"
    if ($env:RYUBOX_D3D12_PROBE_TARGETS -eq '1') { Write-Output 'D3D12_TARGET_PROBE=enabled' }
    if ($LegacyIntelCompatibility) {
        Write-Warning 'Diagnostic mode: guest texture component mappings and counters are disabled; visual correctness is not guaranteed.'
    }
    $env:RYUJINX_UWP_D3D12_TRACE_PATH = Join-Path $runtime 'd3d12-native-trace.log'
    # Keep managed D3D12 diagnostics beside this run. NativeAOT can otherwise
    # report System32 as LocalApplicationData when launched by the harness.
    $env:RYUBOX_D3D12_LOG_DIR = Join-Path $runtime 'd3d12-logs'
    New-Item -ItemType Directory -Force -Path $env:RYUBOX_D3D12_LOG_DIR | Out-Null
    # This is the HWND which actually owns the D3D12 swap chain. Keep the
    # legacy Vulkan validation name out of an interactive D3D12 test.
    $env:RYUJINX_UWP_LOCAL_WINDOW_TITLE = 'Ryubox Direct D3D12 Host - ' + [IO.Path]::GetFileNameWithoutExtension($game)
}
if (-not [string]::IsNullOrWhiteSpace($CaptureFramePath)) {
    $env:RYUJINX_UWP_CAPTURE_FRAME_PATH = [IO.Path]::GetFullPath($CaptureFramePath)
}
[RyuboxDirectNative]::SetDllDirectory($runtime) | Out-Null

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Ryubox Direct ' + $Backend + ' Host - ' + [IO.Path]::GetFileNameWithoutExtension($game)
$form.ClientSize = New-Object System.Drawing.Size(1280, 720)
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.KeyPreview = $true
$form.BackColor = [System.Drawing.Color]::Black
if ($Background -or $Backend -eq 'D3D12') {
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.Location = [System.Drawing.Point]::new(-32000, -32000)
    $form.ShowInTaskbar = $false
}
if ($Backend -eq 'D3D12') {
    # The D3D12 CoreHost creates and owns the visible HWND/swap chain. This
    # WinForms instance remains only as a message/timer host for lifecycle and
    # controller forwarding, so it must never flash as a second game window.
    $form.Opacity = 0
}

$memoryLog = Join-Path $runtime ('direct-host-memory-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv')
$memoryWriter = New-Object IO.StreamWriter($memoryLog, $false, [Text.UTF8Encoding]::new($false))
$memoryWriter.WriteLine('Timestamp,WorkingSetMB,PrivateMB,CpuSeconds,CoreStarted')
$inputTracePath = Join-Path $runtime 'direct-input.log'
Set-Content -LiteralPath $inputTracePath -Value ("Timestamp,Buttons,Stage,Result") -Encoding utf8
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$script:autoStartStage = -1
$script:autoStartReleased = $false
$script:coreStartedAt = $null
$script:heldKeys = New-Object 'System.Collections.Generic.HashSet[System.Windows.Forms.Keys]'

function Send-KeyboardInput {
    if (-not $script:startedCore) {
        return
    }

    $buttons = [uint32]0
    $leftX = 0.0
    $leftY = 0.0
    $rightX = 0.0
    $rightY = 0.0
    $leftTrigger = 0.0
    $rightTrigger = 0.0

    $buttonMap = @{
        ([System.Windows.Forms.Keys]::Z) = [uint32]1
        ([System.Windows.Forms.Keys]::Enter) = [uint32]1
        ([System.Windows.Forms.Keys]::X) = [uint32]2
        ([System.Windows.Forms.Keys]::Escape) = [uint32]2
        # Keep the face-button keys separate from WASD, which is reserved for
        # the left analogue stick. This avoids one physical key producing two
        # unrelated controls during local validation.
        ([System.Windows.Forms.Keys]::C) = [uint32]4
        ([System.Windows.Forms.Keys]::V) = [uint32]8
        ([System.Windows.Forms.Keys]::Up) = [uint32]16
        ([System.Windows.Forms.Keys]::Down) = [uint32]32
        ([System.Windows.Forms.Keys]::Left) = [uint32]64
        ([System.Windows.Forms.Keys]::Right) = [uint32]128
        ([System.Windows.Forms.Keys]::P) = [uint32]256
        ([System.Windows.Forms.Keys]::O) = [uint32]512
        ([System.Windows.Forms.Keys]::Q) = [uint32]1024
        ([System.Windows.Forms.Keys]::E) = [uint32]2048
    }

    foreach ($entry in $buttonMap.GetEnumerator()) {
        if ($script:heldKeys.Contains($entry.Key)) {
            $buttons = $buttons -bor $entry.Value
        }
    }

    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::W)) { $leftY = 1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::D)) { $leftX = 1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::S)) { $leftY = -1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::A)) { $leftX = -1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::I)) { $rightY = 1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::L)) { $rightX = 1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::K)) { $rightY = -1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::J)) { $rightX = -1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::D1)) { $leftTrigger = 1.0 }
    if ($script:heldKeys.Contains([System.Windows.Forms.Keys]::D3)) { $rightTrigger = 1.0 }

    try {
        [RyuboxDirectNative]::SetInput($buttons, $leftX, $leftY, $rightX, $rightY, $leftTrigger, $rightTrigger)
        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x{1:X8},keyboard,ok" -f [DateTime]::UtcNow.ToString('o'), $buttons)
    } catch {
        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x{1:X8},keyboard,error:{2}" -f [DateTime]::UtcNow.ToString('o'), $buttons, $_.Exception.Message.Replace(',', ';'))
    }
}

$form.Add_KeyDown({
    param($sender, $event)
    if ($script:heldKeys.Add($event.KeyCode)) {
        Send-KeyboardInput
    }
    $event.Handled = $true
    $event.SuppressKeyPress = $true
})

$form.Add_KeyUp({
    param($sender, $event)
    if ($script:heldKeys.Remove($event.KeyCode)) {
        Send-KeyboardInput
    }
    $event.Handled = $true
    $event.SuppressKeyPress = $true
})

$timer.Add_Tick({
    try {
        $process = [Diagnostics.Process]::GetCurrentProcess()
        $started = [RyuboxDirectNative]::IsStarted()
        $memoryWriter.WriteLine([string]::Format([Globalization.CultureInfo]::InvariantCulture,
            '{0},{1:F1},{2:F1},{3:F2},{4}', [DateTime]::UtcNow.ToString('o'),
            ($process.WorkingSet64 / 1MB), ($process.PrivateMemorySize64 / 1MB),
            $process.TotalProcessorTime.TotalSeconds, $started))
        $memoryWriter.Flush()

        $elapsedSeconds = if ($script:coreStartedAt -ne $null) { ([DateTime]::UtcNow - $script:coreStartedAt).TotalSeconds } else { 0 }
        # Optional bounded, reproducible local benchmark. Does not affect the
        # installed package or normal interactive sessions.
        $benchmarkSeconds = 0
        if ([int]::TryParse($env:RYUBOX_DIRECT_SESSION_SECONDS, [ref]$benchmarkSeconds) -and
            $benchmarkSeconds -gt 0 -and $elapsedSeconds -ge $benchmarkSeconds) {
            [RyuboxDirectNative]::SetInput(0, 0, 0, 0, 0, 0, 0)
            Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BENCHMARK_TIMEOUT,ok" -f [DateTime]::UtcNow.ToString('o'))
            $form.Close()
            return
        }
        if ($AutoStartMatch -and $script:startedCore -and $elapsedSeconds -ge 30 -and $elapsedSeconds -lt $AutoStartStopSeconds) {
            # The first guest frame can be late on a cold shader cache. Send
            # an A/release pulse every second after the guest has booted, so
            # a pulse is not lost during the opening video or FIFO warm-up.
            # The timer ticks once per second. Using *2 here made every
            # pulse even, so the test held A forever and never emitted a
            # release state.
            $pulse = [int][math]::Floor($elapsedSeconds - 30)
            if ($pulse -ne $script:autoStartStage) {
                if (($pulse % 2) -eq 0) {
                    try {
                        # Cover the common title/registration screens without
                        # holding one button forever: face buttons, Plus, and
                        # the Nintendo L+R registration chord each get a
                        # released pulse during automated validation.
                        $pressIndex = [int]($pulse / 2)
                        $autoButtons = switch ($pressIndex % 6) {
                            0 { 0x00000001 } # A
                            1 { 0x00000002 } # B
                            2 { 0x00000004 } # X
                            3 { 0x00000008 } # Y
                            4 { 0x00000200 } # Plus
                            default { 0x00000C00 } # L+R
                        }
                        # Sonic benchmark must not alternate confirm/cancel.
                        # Opt-in only: other game automation remains unchanged.
                        if ($env:RYUBOX_DIRECT_CONFIRM_ONLY -eq '1') { $autoButtons = 0x00000001 }
                        $autoStage = switch ($autoButtons) {
                            0x00000001 { 'A' }
                            0x00000002 { 'B' }
                            0x00000004 { 'X' }
                            0x00000008 { 'Y' }
                            0x00000200 { 'PLUS' }
                            default { 'LR' }
                        }
                        [RyuboxDirectNative]::SetInput($autoButtons, 0, 0, 0, 0, 0, 0)
                        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x{1:X8},{2}_PULSE_{3},ok" -f [DateTime]::UtcNow.ToString('o'), $autoButtons, $autoStage, $pulse)
                        Write-Output ('DIRECT_AUTO_START_MATCH={0}_PULSE_{1}' -f $autoStage, $pulse)
                    } catch {
                        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000001,A_PULSE_{1},error:{2}" -f [DateTime]::UtcNow.ToString('o'), $pulse, $_.Exception.Message.Replace(',', ';'))
                    }
                } else {
                    try {
                        [RyuboxDirectNative]::SetInput(0, 0, 0, 0, 0, 0, 0)
                        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,A_RELEASE_{1},ok" -f [DateTime]::UtcNow.ToString('o'), $pulse)
                    } catch {
                        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,A_RELEASE_{1},error:{2}" -f [DateTime]::UtcNow.ToString('o'), $pulse, $_.Exception.Message.Replace(',', ';'))
                    }
                }

                $script:autoStartStage = $pulse
            }
        } elseif ($AutoStartMatch -and $script:startedCore -and $elapsedSeconds -ge $AutoStartStopSeconds -and -not $script:autoStartReleased) {
            # Leave the emulated controller in a deterministic released state.
            # Continuing to pulse A while a stage is loading can turn a renderer
            # stall test into an input/re-entry test and obscure the real cause.
            try {
                [RyuboxDirectNative]::SetInput(0, 0, 0, 0, 0, 0, 0)
                Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,A_AUTOSTART_COMPLETE,ok" -f [DateTime]::UtcNow.ToString('o'))
            } catch {
                Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,A_AUTOSTART_COMPLETE,error:{1}" -f [DateTime]::UtcNow.ToString('o'), $_.Exception.Message.Replace(',', ';'))
            }
            $script:autoStartReleased = $true
        }

        if (-not $KeepOpenAfterCapture -and -not [string]::IsNullOrWhiteSpace($CaptureFramePath) -and (Test-Path -LiteralPath $CaptureFramePath -PathType Leaf)) {
            if ($script:captureDetectedAt -eq $null) {
                $script:captureDetectedAt = [DateTime]::UtcNow
            } elseif (([DateTime]::UtcNow - $script:captureDetectedAt).TotalSeconds -ge 2) {
                $form.Close()
            }
        }
    } catch {
        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,TIMER,exception:{1}" -f [DateTime]::UtcNow.ToString('o'), $_.Exception.Message.Replace(',', ';'))
    }
})

$gamePointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($game)
$keysPointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($keys)
$resolvedFirmwarePath = if ([string]::IsNullOrWhiteSpace($FirmwarePath)) { '' } else { (Resolve-Path -LiteralPath $FirmwarePath).Path }
$firmwarePointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($resolvedFirmwarePath)
$backendPointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($Backend)
# DataContractJsonSerializer does not apply field initializers for omitted
# members. Explicit defaults keep optional test switches (including mute)
# from silently disabling VSync, logging or missing-service handling.
$settingsParts = @('"ignoreMissingServices":true', '"fsIntegrityChecks":true',
    '"vsync":true', '"audioEnabled":true', '"fileLogging":true')
# Keep the audio emulation running but mute output for unattended tests.
if ($Background) { $settingsParts += '"audioVolume":0' }
else { $settingsParts += '"audioVolume":1' }
if ($EnablePtc) { $settingsParts += '"ptc":true' }
if ($EnableGuestInternet) { $settingsParts += '"guestInternetAccess":true' }
if ($EnableGuestLogs) { $settingsParts += '"guestLogs":true' }
# The switch predates the D3D12 direct host, but it is the existing explicit
# diagnostics opt-in for this harness. Feed it through CoreHost settings so
# D3D12 enables its debug layer and surfaces API validation failures instead
# of leaving an opaque vendor-driver crash as the only evidence.
if ($EnableD3D11Debug -and $Backend -eq 'D3D12') { $settingsParts += '"graphicsDebugLogging":true' }
$settingsJson = if ($settingsParts.Count -gt 0) { '{' + ($settingsParts -join ',') + '}' } else { '' }
$settingsPointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($settingsJson)
$startedCore = $false

Add-Content -LiteralPath $inputTracePath -Value (
    "{0},0x00000000,SETTINGS,{1}" -f [DateTime]::UtcNow.ToString('o'),
    $(if ([string]::IsNullOrWhiteSpace($settingsJson)) { '<defaults>' } else { $settingsJson.Replace(',', ';') }))

$form.Add_Shown({
    try {
        if ($Backend -eq 'D3D11') {
            Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BOOTSTRAP_BEFORE_D3D11,ok" -f [DateTime]::UtcNow.ToString('o'))
            $graphicsResult = [RyuboxDirectNative]::InitializeForWindow($form.Handle, 1280, 720)
            Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BOOTSTRAP_AFTER_D3D11,0x{1:X8}" -f [DateTime]::UtcNow.ToString('o'), ($graphicsResult -band 0xffffffff))
            Write-Output ('DIRECT_D3D11_INIT=0x{0:X8}' -f ($graphicsResult -band 0xffffffff))
            if ($graphicsResult -lt 0) {
                throw ('Falha ao inicializar a janela D3D11: 0x{0:X8}' -f ($graphicsResult -band 0xffffffff))
            }
        } else {
            Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BOOTSTRAP_D3D12_LOCAL_WINDOW,ok" -f [DateTime]::UtcNow.ToString('o'))
        }

        $fiveGiB = [uint64](5GB)
        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BOOTSTRAP_BEFORE_CORE_START,ok" -f [DateTime]::UtcNow.ToString('o'))
        $startResult = [RyuboxDirectNative]::Start($gamePointer, $keysPointer, $firmwarePointer, $backendPointer, $settingsPointer, $fiveGiB, $fiveGiB)
        Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BOOTSTRAP_AFTER_CORE_START,0x{1:X8}" -f [DateTime]::UtcNow.ToString('o'), ($startResult -band 0xffffffff))
        Write-Output ('DIRECT_CORE_START=0x{0:X8}' -f ($startResult -band 0xffffffff))
        if ($startResult -ne 0) {
            $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal(32768)
            try {
                [void][RyuboxDirectNative]::GetLastError($buffer, 16384)
                $message = [Runtime.InteropServices.Marshal]::PtrToStringUni($buffer)
                $traceMessage = if ($null -eq $message) { '' } else { $message }
                Add-Content -LiteralPath $inputTracePath -Value ("{0},0x00000000,BOOTSTRAP_CORE_ERROR,{1}" -f [DateTime]::UtcNow.ToString('o'), ($traceMessage.Replace("`r", ' ').Replace("`n", ' ').Replace(',', ';')))
                throw "CoreHost falhou: $message"
            } finally {
                [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
            }
        }

        $script:startedCore = $true
        $script:coreStartedAt = [DateTime]::UtcNow
        $timer.Start()
        if ($Backend -eq 'D3D11') {
            $form.Activate()
        } else {
            # The actual game window is created by CoreHost D3D12. Keep this
            # controller window out of the way but alive for session cleanup.
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Minimized
        }
    } catch {
        Write-Error $_
        $form.Close()
    }
})

$form.Add_FormClosed({
    $timer.Stop()
    if ($script:startedCore) {
        try { [RyuboxDirectNative]::Stop() } catch { }
    }
    if ($Backend -eq 'D3D11') {
        try { [RyuboxDirectNative]::ShutdownGraphics() } catch { }
    }
    $memoryWriter.Dispose()
})

try {
    [System.Windows.Forms.Application]::Run($form)
} finally {
    [Runtime.InteropServices.Marshal]::FreeHGlobal($gamePointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($keysPointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($firmwarePointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($backendPointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($settingsPointer)
}
