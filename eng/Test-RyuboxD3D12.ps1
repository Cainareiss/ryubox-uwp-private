[CmdletBinding()]
param(
    [switch] $DebugLayer,
    [switch] $Warp,
    [switch] $Window,
    [string] $OutputDirectory = '',
    [string] $NativeDll = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\d3d12-native-validation' }
$vc = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022' -Directory |
    ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $_.FullName 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue } |
    Sort-Object Name -Descending | Select-Object -First 1
if (!$vc) { throw 'MSVC x64 toolchain not found.' }
$sdk = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVersion = '10.0.26100.0'
$cl = Join-Path $vc.FullName 'bin\Hostx64\x64\cl.exe'
$link = Join-Path $vc.FullName 'bin\Hostx64\x64\link.exe'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$includes = @((Join-Path $vc.FullName 'include')) + @('ucrt','shared','um','winrt') |
    ForEach-Object { if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $sdk "Include\$sdkVersion\$_" } }
$compile = @('/nologo','/c','/std:c++20','/EHsc','/W4','/O2','/MT','/DUNICODE','/D_UNICODE','/DNOMINMAX','/utf-8') +
    @($includes | ForEach-Object { '/I' + $_ })
$libs = @(('/LIBPATH:' + (Join-Path $vc.FullName 'lib\x64')),
    ('/LIBPATH:' + (Join-Path $sdk "Lib\$sdkVersion\ucrt\x64")),
    ('/LIBPATH:' + (Join-Path $sdk "Lib\$sdkVersion\um\x64")))
if (!$NativeDll)
{
    $obj = Join-Path $OutputDirectory 'D3D12Backend.obj'
    & $cl @compile '/DWINAPI_FAMILY=WINAPI_FAMILY_APP' ('/Fo' + $obj) (Join-Path $repo 'src\Ryujinx.Uwp.Native\D3D12Backend.cpp')
    if ($LASTEXITCODE) { throw 'D3D12 backend compilation failed.' }
    $NativeDll = Join-Path $OutputDirectory 'Ryujinx.D3D12.Validation.dll'
    & $link /nologo /DLL /APPCONTAINER /SUBSYSTEM:WINDOWS ('/OUT:' + $NativeDll) @libs $obj d3d12.lib dxgi.lib dxguid.lib d3dcompiler.lib windowsapp.lib
    if ($LASTEXITCODE) { throw 'D3D12 backend link failed.' }
}
$testObj = Join-Path $OutputDirectory 'D3D12Smoke.obj'
& $cl @compile ('/Fo' + $testObj) (Join-Path $repo 'tests\RyuboxD3D12Smoke.cpp')
if ($LASTEXITCODE) { throw 'D3D12 test compilation failed.' }
$test = Join-Path $OutputDirectory 'D3D12Smoke.exe'
& $link /nologo /SUBSYSTEM:CONSOLE ('/OUT:' + $test) @libs $testObj kernel32.lib user32.lib
if ($LASTEXITCODE) { throw 'D3D12 test link failed.' }
$flags = 0
if ($DebugLayer) { $flags = $flags -bor 1 }
if ($Warp) { $flags = $flags -bor 2 }
$mode = if ($Window) { 'window' } else { 'offscreen' }
& $test $NativeDll $flags $mode
if ($LASTEXITCODE) { throw "D3D12 validation failed: exit code $LASTEXITCODE" }
