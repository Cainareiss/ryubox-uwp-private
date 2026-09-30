[CmdletBinding()]
param(
    [string] $MesaDir = (Join-Path $PSScriptRoot '..\artifacts\mesa'),
    [string] $BuildDir = (Join-Path $PSScriptRoot '..\artifacts\mesa-build'),
    [string] $InstallDir = (Join-Path $PSScriptRoot '..\artifacts\mesa-install'),
    [string] $StageDir = (Join-Path $PSScriptRoot '..\artifacts\xbox\MesaDozen'),
    [string] $MesaRepo = 'https://gitlab.freedesktop.org/mesa/mesa.git',
    [string] $MesaRef = 'main',
    [string] $VulkanDrivers = 'microsoft-experimental',
    [string] $VulkanLoaderDll,
    [ValidateSet('debug', 'debugoptimized', 'release', 'plain', 'minsize')]
    [string] $BuildType = 'release',
    [switch] $SkipClone,
    [switch] $SetupOnly,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sharedMesaScript = Join-Path (Split-Path -Parent $repoRoot) 'ps3 uwp\rpcs3\buildfiles\uwp\build_mesa_dzn_uwp.ps1'

if (-not (Test-Path -LiteralPath $sharedMesaScript)) {
    throw "Script Mesa/Dozen compartilhado não encontrado: $sharedMesaScript"
}

$arguments = @{
    MesaDir = $MesaDir
    BuildDir = $BuildDir
    InstallDir = $InstallDir
    StageDir = $StageDir
    MesaRepo = $MesaRepo
    MesaRef = $MesaRef
    VulkanDrivers = $VulkanDrivers
    BuildType = $BuildType
}

if (-not [string]::IsNullOrWhiteSpace($VulkanLoaderDll)) {
    $arguments.VulkanLoaderDll = $VulkanLoaderDll
}
if ($SkipClone) { $arguments.SkipClone = $true }
if ($SetupOnly) { $arguments.SetupOnly = $true }
if ($Clean) { $arguments.Clean = $true }

& $sharedMesaScript @arguments
$sharedExitCode = $LASTEXITCODE
if ($sharedExitCode -ne 0) {
    throw "Mesa/Dozen helper falhou com o código de saída $sharedExitCode"
}

if (-not (Test-Path -LiteralPath (Join-Path $BuildDir 'build.ninja'))) {
    throw "Meson não gerou build.ninja em $BuildDir; consulte meson-logs\meson-log.txt"
}

if (-not $SetupOnly) {
    # The helper stages the ICD with a path relative to its install tree.
    # The UWP package keeps vulkan_dzn.dll beside the manifest, so normalize
    # the manifest after staging.
    foreach ($manifest in Get-ChildItem -LiteralPath $StageDir -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        $manifestText = Get-Content -LiteralPath $manifest.FullName -Raw
        $normalizedText = $manifestText.Replace('../../../bin/vulkan_dzn.dll', 'vulkan_dzn.dll')
        if ($normalizedText -ne $manifestText) {
            Set-Content -LiteralPath $manifest.FullName -Value $normalizedText -Encoding utf8NoBOM
        }
    }
}

if (-not $SetupOnly -and -not (Test-Path -LiteralPath (Join-Path $StageDir 'vulkan_dzn.dll'))) {
    throw "Mesa terminou sem gerar vulkan_dzn.dll em $StageDir"
}

exit 0
