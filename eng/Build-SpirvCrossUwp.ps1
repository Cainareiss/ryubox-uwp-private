[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputRoot,
    [string] $IntermediateRoot,
    [string] $ToolchainRoot,
    [string] $SdkRoot = 'C:\Program Files (x86)\Windows Kits\10',
    [string] $SdkVersion = '10.0.26100.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'third_party\SPIRV-Cross'
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $sourceRoot 'build-uwp-x64-msbuild'
} else {
    $OutputRoot
}
$IntermediateRoot = if ([string]::IsNullOrWhiteSpace($IntermediateRoot)) {
    Join-Path $repoRoot ".cache\spirv-cross-uwp\$Configuration"
} else {
    $IntermediateRoot
}
$ToolchainRoot = if ([string]::IsNullOrWhiteSpace($ToolchainRoot)) {
    Join-Path $repoRoot 'artifacts\vs-buildtools'
} else {
    $ToolchainRoot
}

$vcToolsRoot = Join-Path $ToolchainRoot 'VC\Tools\MSVC'
$vcToolRoot = Get-ChildItem -LiteralPath $vcToolsRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe')) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'include\vector'))
    } |
    Select-Object -First 1

if (-not $vcToolRoot) {
    throw "Toolchain MSVC x64 nao encontrado em $vcToolsRoot"
}

$cl = Join-Path $vcToolRoot.FullName 'bin\Hostx64\x64\cl.exe'
$vcInclude = Join-Path $vcToolRoot.FullName 'include'
$vcLib = Join-Path $vcToolRoot.FullName 'lib\x64'
$sdkUcrtInclude = Join-Path $SdkRoot "Include\$SdkVersion\ucrt"
$sdkSharedInclude = Join-Path $SdkRoot "Include\$SdkVersion\shared"
$sdkUmInclude = Join-Path $SdkRoot "Include\$SdkVersion\um"
$sdkWinrtInclude = Join-Path $SdkRoot "Include\$SdkVersion\winrt"
$sdkUcrtLib = Join-Path $SdkRoot "Lib\$SdkVersion\ucrt\x64"
$sdkUmLib = Join-Path $SdkRoot "Lib\$SdkVersion\um\x64"

$sources = @(
    'spirv_cross.cpp',
    'spirv_parser.cpp',
    'spirv_cross_parsed_ir.cpp',
    'spirv_cfg.cpp',
    'spirv_glsl.cpp',
    'spirv_hlsl.cpp',
    'spirv_cross_c.cpp'
) | ForEach-Object { Join-Path $sourceRoot $_ }

$required = @(
    $cl,
    (Join-Path $vcInclude 'vector'),
    (Join-Path $sdkUmLib 'windowsapp.lib')
) + $sources
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing) {
    throw 'Arquivos necessarios para SPIRV-Cross UWP ausentes: ' + ($missing -join ', ')
}

New-Item -ItemType Directory -Force -Path $OutputRoot, $IntermediateRoot | Out-Null
$dllPath = Join-Path $OutputRoot 'spirv-cross-c-shared.dll'
$runtime = if ($Configuration -eq 'Debug') { '/MTd' } else { '/MT' }
$optimization = if ($Configuration -eq 'Debug') { @('/Od', '/Zi') } else { @('/O2', '/Ob2', '/DNDEBUG') }

$compileArguments = @(
    '/nologo',
    '/LD',
    '/EHsc',
    '/std:c++17',
    $runtime,
    '/DWIN32',
    '/D_WINDOWS',
    '/DWINAPI_FAMILY=WINAPI_FAMILY_APP',
    '/DNOMINMAX',
    '/DSPVC_EXPORT_SYMBOLS',
    '/DSPIRV_CROSS_C_API_GLSL=1',
    '/DSPIRV_CROSS_C_API_HLSL=1',
    "/I$sourceRoot",
    "/I$vcInclude",
    "/I$sdkUcrtInclude",
    "/I$sdkSharedInclude",
    "/I$sdkUmInclude",
    "/I$sdkWinrtInclude",
    "/Fe:$dllPath"
) + $optimization + $sources + @(
    '/link',
    '/SUBSYSTEM:WINDOWS',
    '/APPCONTAINER',
    "/LIBPATH:$vcLib",
    "/LIBPATH:$sdkUcrtLib",
    "/LIBPATH:$sdkUmLib",
    'windowsapp.lib'
)

Push-Location -LiteralPath $IntermediateRoot
try {
    & $cl @compileArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Compilacao terminou sem gerar $dllPath"
}

Write-Output "SPIRV-Cross UWP x64 gerado: $dllPath"
