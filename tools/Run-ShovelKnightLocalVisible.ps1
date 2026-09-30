param(
    [string] $KeysDirectory = 'E:\switchkeys.io-v22.5.0',
    [string] $GamePath = '',
    [string] $FirmwarePath = '',
    [ValidateSet('D3D11', 'Vulkan')]
    [string] $Backend = 'D3D11',
    [int] $AutoStopSeconds = 0
)

$ErrorActionPreference = 'Stop'

$workspace = Split-Path -Parent $PSScriptRoot
$game = if ([string]::IsNullOrWhiteSpace($GamePath)) {
    Get-ChildItem -LiteralPath $KeysDirectory -Filter '*.nsp' -File |
        Where-Object { $_.Name -like 'Shovel Knight*' } |
        Select-Object -First 1
}
else {
    Get-Item -LiteralPath $GamePath
}

if ($null -eq $game) {
    throw "Shovel Knight NSP não foi encontrado em $keysDirectory."
}

$appDirectory = Join-Path $workspace 'src\Ryujinx.Uwp.App\bin\x64\Release'
$coreHostDirectory = Join-Path $workspace 'src\Ryujinx.Uwp.CoreHost\bin\Release\net10.0\win-x64\publish'
$coreHostDll = Join-Path $coreHostDirectory 'Ryujinx.Uwp.CoreHost.dll'
$firmware = if ([string]::IsNullOrWhiteSpace($FirmwarePath)) {
    Get-ChildItem -LiteralPath $KeysDirectory -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Firmware*.zip' -or $_.Extension -in '.xci' } |
        Select-Object -First 1
}
else {
    Get-Item -LiteralPath $FirmwarePath
}

$env:RYUJINX_UWP_LOCAL_WINDOW_TEST = '1'
$env:RYUJINX_UWP_LOCAL_WINDOW_VISIBLE = '1'
$env:RYUJINX_UWP_PACKAGE_DIR = $appDirectory
$env:PATH = "$appDirectory;$coreHostDirectory;$env:PATH"

$dllLiteral = $coreHostDll.Replace('\', '\\').Replace('"', '\"')
$probeSource = @"
using System;
using System.Runtime.InteropServices;

public static class RyujinxVisibleProbe
{
    [DllImport("$dllLiteral", EntryPoint = "RyujinxUwpCoreHost_Start", CallingConvention = CallingConvention.StdCall)]
    public static extern int Start(IntPtr game, IntPtr keys, IntPtr firmware, IntPtr backend, ulong currentMemoryLimit, ulong expectedMemoryLimit);

    [DllImport("$dllLiteral", EntryPoint = "RyujinxUwpCoreHost_Stop", CallingConvention = CallingConvention.StdCall)]
    public static extern void Stop();

    [DllImport("$dllLiteral", EntryPoint = "RyujinxUwpCoreHost_IsStarted", CallingConvention = CallingConvention.StdCall)]
    public static extern int IsStarted();

    [DllImport("$dllLiteral", EntryPoint = "RyujinxUwpCoreHost_IsBound", CallingConvention = CallingConvention.StdCall)]
    public static extern int IsBound();

    [DllImport("$dllLiteral", EntryPoint = "RyujinxUwpCoreHost_GetLastError", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetLastErrorNative(IntPtr buffer, int capacity);

    public static string GetLastError()
    {
        const int capacity = 32768;
        IntPtr buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            GetLastErrorNative(buffer, capacity);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
"@

Add-Type $probeSource

$gamePtr = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($game.FullName)
$keysPtr = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($KeysDirectory)
$firmwareValue = if ($null -eq $firmware) { '' } else { $firmware.FullName }
$firmwarePtr = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($firmwareValue)
$backendPtr = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($Backend)

try {
    Write-Host "Abrindo janela visível do Shovel Knight..." -ForegroundColor Cyan
    $hr = [RyujinxVisibleProbe]::Start($gamePtr, $keysPtr, $firmwarePtr, $backendPtr, 0, 0)
    Write-Host ("CoreHost retornou {0}; Started={1}; Bound={2}" -f $hr, [RyujinxVisibleProbe]::IsStarted(), [RyujinxVisibleProbe]::IsBound())

    if ($hr -ne 0) {
        throw [InvalidOperationException]::new([RyujinxVisibleProbe]::GetLastError())
    }

    Write-Host 'A janela do jogo está aberta. Pressione Enter nesta janela para encerrar.' -ForegroundColor Green
    if ($AutoStopSeconds -gt 0) {
        Write-Host "Executando validação automática por $AutoStopSeconds segundos..." -ForegroundColor Green
        for ($elapsed = 0; $elapsed -lt $AutoStopSeconds; $elapsed++) {
            Start-Sleep -Seconds 1
            if ([RyujinxVisibleProbe]::IsStarted() -eq 0) {
                throw [InvalidOperationException]::new(
                    "CoreHost encerrou durante a validação: " + [RyujinxVisibleProbe]::GetLastError())
            }
        }
    }
    else {
        Read-Host | Out-Null
    }
}
finally {
    [RyujinxVisibleProbe]::Stop()
    [Runtime.InteropServices.Marshal]::FreeHGlobal($gamePtr)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($keysPtr)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($firmwarePtr)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($backendPtr)
}
