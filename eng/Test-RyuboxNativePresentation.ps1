param(
    [string] $ToolchainRoot = (Join-Path $PSScriptRoot '..\artifacts\vs-buildtools'),
    [string] $NativeDll = (Join-Path $PSScriptRoot '..\src\Ryujinx.Uwp.Native\bin\x64\Release\Ryujinx.Uwp.Native.dll'),
    [string] $SdkRoot = 'C:\Program Files (x86)\Windows Kits\10',
    [string] $SdkVersion = '10.0.26100.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vcRoot = Get-ChildItem -LiteralPath (Join-Path $ToolchainRoot 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$compiler = Join-Path $vcRoot 'bin\Hostx64\x64\cl.exe'
$outputDirectory = Join-Path $repoRoot 'artifacts\presentation-smoke'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$testExe = Join-Path $outputDirectory 'RyuboxNativePresentationSmoke.exe'
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
$savedPath = $env:PATH
try {
    $env:PATH = "$(Split-Path $compiler);$savedPath"
    $env:INCLUDE = "$vcRoot\include;$SdkRoot\Include\$SdkVersion\ucrt;$SdkRoot\Include\$SdkVersion\shared;$SdkRoot\Include\$SdkVersion\um;$SdkRoot\Include\$SdkVersion\winrt"
    $env:LIB = "$vcRoot\lib\x64;$SdkRoot\Lib\$SdkVersion\ucrt\x64;$SdkRoot\Lib\$SdkVersion\um\x64"
    & $compiler /nologo /EHsc /std:c++17 /MT /DNOMINMAX `
        (Join-Path $repoRoot 'tests\RyuboxNativePresentationSmoke.cpp') `
        "/Fe:$testExe" "/Fo:$outputDirectory\RyuboxNativePresentationSmoke.obj" /link user32.lib
    if ($LASTEXITCODE -ne 0) { throw "Presentation test compilation failed: $LASTEXITCODE" }
    & $testExe ([IO.Path]::GetFullPath($NativeDll))
    if ($LASTEXITCODE -ne 0) { throw "Presentation smoke failed: $LASTEXITCODE" }
} finally {
    $env:INCLUDE = $savedInclude
    $env:LIB = $savedLib
    $env:PATH = $savedPath
}
