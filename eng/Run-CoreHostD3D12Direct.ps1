param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [ValidateRange(0, 2147483647)][int]$DurationSeconds = 600,
    [ValidateRange(1, 32)][int]$MemoryLimitGiB = 5,
    [string]$CapturePath = '',
    [string]$RuntimeDirectory = '',
    [ValidateRange(1, 100000)][int]$CaptureSequence = 1,
    [switch]$Hidden,
    [switch]$Fullscreen,
    [switch]$Keyboard,
    [switch]$Fps,
    [switch]$AutoPressA,
    [switch]$Ptc,
    [switch]$ShaderCache,
    [ValidateRange(0, 3600)][double]$AutoPressStartSeconds = 50.0,
    [ValidateRange(0.5, 300)][double]$AutoPressIntervalSeconds = 8.0,
    [string]$InputARequestPath = '',
    [switch]$Audio,
    [switch]$DebugLayer,
    [switch]$DeepTrace,
    [string]$StopSignalPath = ''
)

# Desktop diagnostic host only. Unlike run_pokemon_visible.ps1, this does not
# initialize the ImGui/UWP frontend or acquire its D3D12 lease. CoreHost owns
# the native Win32 D3D12 surface for the duration of this process.
$ErrorActionPreference = 'Stop'
$runtime = if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Ryujinx.Uwp.App\bin\x64\Release'))
} else { (Resolve-Path -LiteralPath $RuntimeDirectory).Path }
$keys = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\system'
$game = (Resolve-Path -LiteralPath $GamePath).Path

$env:RYUJINX_UWP_PACKAGE_DIR = $runtime
$env:RYUJINX_UWP_LOCAL_WINDOW_TEST = '1'
$env:RYUJINX_UWP_LOCAL_WINDOW_VISIBLE = if ($Hidden) { '0' } else { '1' }
$env:RYUJINX_UWP_LOCAL_WINDOW_KEYBOARD = if ($Keyboard) { '1' } else { '0' }
$env:RYUJINX_UWP_LOCAL_WINDOW_FPS = if ($Fps) { '1' } else { '0' }
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
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);
    public static IntPtr FindGameWindow(int processId)
    {
        // Keep a real null title inside C#: PowerShell can marshal $null to
        // an empty string, which would search only windows with empty titles.
        return FindWindow("RyujinxUwpLocalVulkan_" + processId, null);
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public static bool EnterFullscreen(IntPtr window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return false;
        int style = GetWindowLong(window, -16);
        SetWindowLong(window, -16, style & ~0x00CF0000); // Remove overlapped-window decorations.
        var r = info.Monitor;
        bool result = SetWindowPos(window, IntPtr.Zero, r.Left, r.Top,
            r.Right - r.Left, r.Bottom - r.Top, 0x0020 | 0x0040); // FRAMECHANGED | SHOWWINDOW
        SetForegroundWindow(window);
        return result;
    }

    public static bool PumpMessages()
    {
        Message message;
        while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1))
        {
            if (message.Id == 0x0012) return false; // WM_QUIT
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return true;
    }

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
    $debugValue = if ($DebugLayer) { 'true' } else { 'false' }
    $ptcValue = if ($Ptc) { 'true' } else { 'false' }
    $shaderCacheValue = if ($ShaderCache) { 'true' } else { 'false' }
    $settings = '{"schemaVersion":1,"vsync":true,"gpuThreading":false,"ptc":' + $ptcValue + ',"shaderCache":' + $shaderCacheValue + ',"audioEnabled":' + $audioValue + ',"graphicsDebugLogging":' + $debugValue + ',"fileLogging":true}'
    foreach ($value in @($game, $keys, '', 'D3D12', $settings)) {
        $allocated.Add([Runtime.InteropServices.Marshal]::StringToHGlobalUni($value))
    }

    $memoryLimit = [uint64]$MemoryLimitGiB * 1GB
    $hr = [DirectCoreHost]::Start($allocated[0], $stream.SafeFileHandle.DangerousGetHandle(), $allocated[1], $allocated[2], $allocated[3], $allocated[4], $memoryLimit, $memoryLimit)
    if ($hr -lt 0) {
        throw ('CoreHost direct start failed (0x{0:X8}): {1}' -f $hr, (Get-CoreError))
    }

    Write-Host 'CoreHost D3D12 direto iniciado; sem frontend ImGui/UWP.'
    if ($Keyboard) {
        Write-Host 'Teclado: setas=d-pad, Enter/Z=A, X=B, C=X, V=Y, WASD=analógico esquerdo, IJKL=analógico direito, Q/E=L/R, 1/3=gatilhos, O/P=-/+.'
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $gameWindow = [DirectCoreHost]::FindGameWindow($PID)
    if (-not $Hidden -and $gameWindow -ne [IntPtr]::Zero) {
        # The first ShowWindow inside CoreHost can inherit the hidden console's
        # STARTUPINFO. A second call explicitly shows only the game window.
        [void][DirectCoreHost]::ShowWindow($gameWindow, 5)
        if ($Fullscreen -and -not [DirectCoreHost]::EnterFullscreen($gameWindow)) {
            Write-Warning 'Nao foi possivel aplicar tela cheia sem bordas.'
        }
    }
    $nextAutoPressSeconds = $AutoPressStartSeconds
    $releaseAutoPressAt = -1.0
    # Zero disables the automatic timeout for interactive play.
    while ([DirectCoreHost]::IsStarted() -ne 0 -and ($DurationSeconds -eq 0 -or $clock.Elapsed.TotalSeconds -lt $DurationSeconds)) {
        if (-not [DirectCoreHost]::PumpMessages() -or
            ($gameWindow -ne [IntPtr]::Zero -and -not [DirectCoreHost]::IsWindow($gameWindow))) {
            Write-Host 'Janela fechada pelo usuario; liberando CoreHost.'
            break
        }
        if ($StopSignalPath -and (Test-Path -LiteralPath $StopSignalPath)) {
            Write-Host 'Parada solicitada pelo arquivo de controle; liberando CoreHost.'
            break
        }
        $requestedA = $InputARequestPath -and (Test-Path -LiteralPath $InputARequestPath)
        if ($requestedA) {
            Remove-Item -LiteralPath $InputARequestPath -Force
            Write-Host ('A solicitado externamente em t={0:N1}s' -f $clock.Elapsed.TotalSeconds)
        }
        if (($requestedA -or ($AutoPressA -and $clock.Elapsed.TotalSeconds -ge $nextAutoPressSeconds)) -and $releaseAutoPressAt -lt 0) {
            [DirectCoreHost]::SetInput(1, 0, 0, 0, 0, 0, 0)
            $releaseAutoPressAt = $clock.Elapsed.TotalSeconds + 0.20
            if ($AutoPressA) { $nextAutoPressSeconds = $clock.Elapsed.TotalSeconds + $AutoPressIntervalSeconds }
        }
        if ($releaseAutoPressAt -ge 0 -and $clock.Elapsed.TotalSeconds -ge $releaseAutoPressAt) {
            [DirectCoreHost]::SetInput(0, 0, 0, 0, 0, 0, 0)
            $releaseAutoPressAt = -1.0
        }
        Start-Sleep -Milliseconds 10
    }
    if ([DirectCoreHost]::IsStarted() -eq 0) {
        Write-Warning ('CoreHost encerrou: ' + (Get-CoreError))
    }
} finally {
    [DirectCoreHost]::Stop()
    if ($stream) { $stream.Dispose() }
    foreach ($pointer in $allocated) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) }
}
