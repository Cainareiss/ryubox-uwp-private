param(
    [string] $GamePath = 'E:\switchkeys.io-v22.5.0\Shovel Knight Treasure Trove [010057D0021E8000][v0].nsp',
    [string] $KeysDirectory = (Join-Path $PSScriptRoot '..\artifacts\corehost-local-xbox-phase1\system'),
    [string] $SandboxDirectory = (Join-Path $PSScriptRoot '..\artifacts\corehost-local-xbox-phase1'),
    [switch] $RunCoreHost,
    [switch] $Child
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$game = [IO.Path]::GetFullPath($GamePath)
$keys = [IO.Path]::GetFullPath($KeysDirectory)
$sandbox = [IO.Path]::GetFullPath($SandboxDirectory)

$requiredFiles = @(
    (Join-Path $sandbox 'vulkan-1.dll'),
    (Join-Path $sandbox 'vulkan_dzn.dll'),
    (Join-Path $sandbox 'spirv_to_dxil.dll'),
    (Join-Path $sandbox 'dzn_icd.x86_64.json'),
    (Join-Path $sandbox 'Ryujinx.Uwp.CoreHost.dll'),
    (Join-Path $keys 'prod.keys'),
    (Join-Path $keys 'title.keys')
)

if (-not (Test-Path -LiteralPath $game -PathType Leaf)) {
    throw "NSP não encontrado: $game"
}

foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Arquivo necessário não encontrado: $file"
    }
}

Write-Host "PHASE1_PREFLIGHT=PASS"
Write-Host "GAME=$game"
Write-Host "KEYS=$keys"
Write-Host "SANDBOX=$sandbox"

if (-not $RunCoreHost -and -not $Child) {
    Write-Host 'Use -RunCoreHost para executar o bootstrap isolado.'
    exit 0
}

if (-not $Child) {
    $logDirectory = Join-Path $repoRoot 'artifacts\phase1'
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $stdout = Join-Path $logDirectory 'corehost.stdout.log'
    $stderr = Join-Path $logDirectory 'corehost.stderr.log'
    $powershell = (Get-Command powershell.exe).Source
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Child -GamePath `"$game`" -KeysDirectory `"$keys`" -SandboxDirectory `"$sandbox`""
    $process = Start-Process -FilePath $powershell -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Get-Content -LiteralPath $stdout
    if (Test-Path -LiteralPath $stderr) {
        Get-Content -LiteralPath $stderr
    }

    if ($process.ExitCode -eq -1073741819) {
        Write-Warning 'LOCAL_XBOX_RUNTIME_REQUIRED: o PC chegou ao bootstrap Vulkan, mas não possui D3D12GetInterface do Xbox.'
        exit 0
    }

    if ($process.ExitCode -ne 0) {
        throw "CoreHost terminou com código $($process.ExitCode)."
    }

    Write-Host 'PHASE1_COREHOST=PASS'
    exit 0
}

$interop = @'
using System;
using System.Runtime.InteropServices;
public static class RyujinxXboxPhase1Native {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool SetDllDirectory(string path);
    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint="RyujinxUwpCoreHost_Start", CallingConvention=CallingConvention.StdCall)]
    public static extern int Start(IntPtr game, IntPtr keys, IntPtr firmware, IntPtr backend, ulong currentMemoryLimit, ulong expectedMemoryLimit);
    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint="RyujinxUwpCoreHost_Stop", CallingConvention=CallingConvention.StdCall)]
    public static extern void Stop();
    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint="RyujinxUwpCoreHost_IsStarted", CallingConvention=CallingConvention.StdCall)]
    public static extern int IsStarted();
    [DllImport("Ryujinx.Uwp.CoreHost.dll", EntryPoint="RyujinxUwpCoreHost_IsBound", CallingConvention=CallingConvention.StdCall)]
    public static extern int IsBound();
}
'@
Add-Type -TypeDefinition $interop

$env:VK_DRIVER_FILES = (Resolve-Path (Join-Path $sandbox 'dzn_icd.x86_64.json')).Path
$env:RYUJINX_UWP_PACKAGE_DIR = $sandbox
[RyujinxXboxPhase1Native]::SetDllDirectory($sandbox) | Out-Null

$gamePointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($game)
$keysPointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($keys)
$firmwarePointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni('')
$backendPointer = [Runtime.InteropServices.Marshal]::StringToHGlobalUni('d3d12/dozen')
try {
    $result = [RyujinxXboxPhase1Native]::Start($gamePointer, $keysPointer, $firmwarePointer, $backendPointer, 0, 0)
    Write-Host ('PHASE1_START_HRESULT=0x{0:X8}' -f ($result -band 0xffffffff))
    Write-Host ('PHASE1_STARTED=' + [RyujinxXboxPhase1Native]::IsStarted())
    Write-Host ('PHASE1_BOUND=' + [RyujinxXboxPhase1Native]::IsBound())
    if ($result -ne 0) {
        exit 2
    }
    [RyujinxXboxPhase1Native]::Stop()
} finally {
    [Runtime.InteropServices.Marshal]::FreeHGlobal($gamePointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($keysPointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($firmwarePointer)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($backendPointer)
}
