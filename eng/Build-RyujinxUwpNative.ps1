[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputRoot,
    [string] $IntermediateRoot,
    [string] $ToolchainRoot,
    [string] $SdkRoot = 'C:\Program Files (x86)\Windows Kits\10',
    [string] $SdkVersion = '10.0.26100.0',
    [switch] $SkipIfUnavailable
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $repoRoot 'src\Ryujinx.Uwp.Native\bin\x64' } else { $OutputRoot }
$nativeSourceDirectory = Join-Path $repoRoot 'src\Ryujinx.Uwp.Native'
$nativeSources = @(
    (Join-Path $nativeSourceDirectory 'Ryujinx.Uwp.Native.cpp'),
    (Join-Path $nativeSourceDirectory 'D3D12Backend.cpp'),
    (Join-Path $nativeSourceDirectory 'ImGuiD3D11Shell.cpp'),
    (Join-Path $nativeSourceDirectory 'ImGuiD3D12Renderer.cpp'),
    (Join-Path $nativeSourceDirectory 'D3D12FrontendHost.cpp'),
    (Join-Path $nativeSourceDirectory 'RyuboxFrontendHost.cpp'),
    (Join-Path $nativeSourceDirectory 'DsuCompanionStub.cpp'),
    (Join-Path $nativeSourceDirectory 'third_party\imgui\imgui.cpp'),
    (Join-Path $nativeSourceDirectory 'third_party\imgui\imgui_draw.cpp'),
    (Join-Path $nativeSourceDirectory 'third_party\imgui\imgui_tables.cpp'),
    (Join-Path $nativeSourceDirectory 'third_party\imgui\imgui_widgets.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ControllerProfileStore.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\CredentialStore.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Frontend.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\GraphicsView.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ImGuiShell.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\InputMapper.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\InputMapping.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\LibraryView.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\MetadataEditor.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\NativeTextInput.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Notifications.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Pickers.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\RyuboxSettings.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ScreenScraperService.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ScreenScraperTypes.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ServicePanels.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Settings.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ShellRenderer.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\ShellState.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\StorageLocations.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Text.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Types.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Views.cpp'),
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\src\Widgets.cpp')
)
$configurationOutput = Join-Path $OutputRoot $Configuration
$intermediate = if ([string]::IsNullOrWhiteSpace($IntermediateRoot)) {
    Join-Path $repoRoot ".cache\native\$Configuration-v2"
} else {
    $IntermediateRoot
}
$ToolchainRoot = if ([string]::IsNullOrWhiteSpace($ToolchainRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($env:RYUJINX_UWP_TOOLCHAIN_ROOT)) {
        $env:RYUJINX_UWP_TOOLCHAIN_ROOT
    } else {
        $null
    }
} else {
    $ToolchainRoot
}

$vcToolsRoot = if ($ToolchainRoot) {
    Join-Path $ToolchainRoot 'VC\Tools\MSVC'
} else {
    'C:\Program Files\Microsoft Visual Studio\2022'
}

$vcToolRoot = if ($ToolchainRoot) {
    Get-ChildItem -LiteralPath $vcToolsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe')) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'include\excpt.h'))
        } |
        Select-Object -First 1
} else {
    Get-ChildItem -LiteralPath 'C:\Program Files\Microsoft Visual Studio\2022' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'VC\Tools\MSVC' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Directory } |
        Sort-Object FullName -Descending |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe')) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'include\excpt.h'))
        } |
        Select-Object -First 1
}

$cl = if ($vcToolRoot) { Join-Path $vcToolRoot.FullName 'bin\Hostx64\x64\cl.exe' } else { $null }
$requiredFiles = @(
    ($nativeSources | ForEach-Object { "`"$_`"" }),
    (Join-Path $SdkRoot "Include\$SdkVersion\um\d3d11.h"),
    (Join-Path $SdkRoot "Include\$SdkVersion\um\d3d12.h"),
    (Join-Path $SdkRoot "Lib\$SdkVersion\um\x64\d3d11.lib"),
    (Join-Path $SdkRoot "Lib\$SdkVersion\um\x64\d3dcompiler.lib"),
    (Join-Path $SdkRoot "Lib\$SdkVersion\um\x64\d3d12.lib"),
    (Join-Path $SdkRoot "Include\$SdkVersion\um\xaudio2.h"),
    (Join-Path $SdkRoot "Lib\$SdkVersion\um\x64\xaudio2.lib")
)

$missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_) })
if (-not $cl) {
    $missing += 'MSVC cl.exe + include\excpt.h'
}
if ($missing) {
    $message = 'Toolchain C++ UWP incompleto; o host D3D12 não foi compilado. Faltando: ' + ($missing -join ', ')
    if ($SkipIfUnavailable) {
        Write-Warning $message
        exit 0
    }

    throw $message
}

New-Item -ItemType Directory -Force -Path $configurationOutput, $intermediate | Out-Null
$env:Path = "$(Split-Path $cl);$env:Path"

$vcInclude = Join-Path $vcToolRoot.FullName 'include'
$vcLib = Join-Path $vcToolRoot.FullName 'lib\x64'
$sdkUcrtInclude = Join-Path $SdkRoot "Include\$SdkVersion\ucrt"
$sdkSharedInclude = Join-Path $SdkRoot "Include\$SdkVersion\shared"
$sdkUmInclude = Join-Path $SdkRoot "Include\$SdkVersion\um"
$sdkWinrtInclude = Join-Path $SdkRoot "Include\$SdkVersion\winrt"
$sdkCppWinrtInclude = Join-Path $SdkRoot "Include\$SdkVersion\cppwinrt"
$sdkUcrtLib = Join-Path $SdkRoot "Lib\$SdkVersion\ucrt\x64"
$sdkUmLib = Join-Path $SdkRoot "Lib\$SdkVersion\um\x64"
$dllPath = Join-Path $configurationOutput 'Ryujinx.Uwp.Native.dll'

$compileArguments = @(
    '/nologo',
    '/c',
    '/EHsc',
    '/std:c++20',
    '/utf-8',
    '/MT',
    '/DWIN32',
    '/D_WINDOWS',
    '/DWINAPI_FAMILY=WINAPI_FAMILY_APP',
    '/DNOMINMAX',
    '/DFMT_HEADER_ONLY',
    '/DRAPIDJSON_HAS_STDSTRING=1',
    "/I`"$vcInclude`"",
    "/I`"$nativeSourceDirectory`"",
    "/I`"$(Join-Path $nativeSourceDirectory 'third_party\imgui')`"",
    "/I`"$(Join-Path $repoRoot 'src\UWP-ImGuiFrontend\include')`"",
    "/I`"$(Join-Path $nativeSourceDirectory 'shims')`"",
    "/I`"$(Join-Path $nativeSourceDirectory 'shims\rapidjson-repo\include')`"",
    "/I`"$(Join-Path $nativeSourceDirectory 'shims\fmt-repo\include')`"",
    "/I`"$sdkUcrtInclude`"",
    "/I`"$sdkSharedInclude`"",
    "/I`"$sdkUmInclude`"",
    "/I`"$sdkWinrtInclude`"",
    "/I`"$sdkCppWinrtInclude`""
)

# A directory named Release does not enable optimizer flags when invoking
# cl.exe directly. Keep symbols for CPU profiles without disabling /O2.
$compileArguments += if ($Configuration -eq 'Release') {
    @('/O2', '/DNDEBUG', '/Z7', '/fp:precise')
} else {
    @('/Od', '/Z7', '/fp:precise')
}
$buildScriptWriteTime = (Get-Item -LiteralPath $PSCommandPath).LastWriteTime
Write-Output "Native configuration=$Configuration; optimization=$(if ($Configuration -eq 'Release') {'/O2'} else {'/Od'}); symbols=/Z7"

Write-Verbose ('Using MSVC tool root: ' + $vcToolRoot.FullName)
Write-Verbose ('Using compiler: ' + $cl)
Write-Verbose ('Using SDK root: ' + $SdkRoot)
Write-Verbose ('Using SDK version: ' + $SdkVersion)

$objectFiles = @()
$headerRoots = @(
    $nativeSourceDirectory,
    (Join-Path $repoRoot 'src\UWP-ImGuiFrontend\include')
)
$latestHeaderWriteTime = $headerRoots |
    # Windows PowerShell can ignore -Include with -LiteralPath here, making
    # a changed cpp/obj look like a changed header and rebuilding every file.
    ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -ErrorAction SilentlyContinue } |
    Where-Object { $_.Extension -in '.h', '.hpp', '.inl' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty LastWriteTime
foreach ($source in $nativeSources) {
    $objectFile = Join-Path $intermediate (([IO.Path]::GetFileNameWithoutExtension($source)) + '.obj')
    $sourceWriteTime = (Get-Item -LiteralPath $source).LastWriteTime
    $existingObject = Get-Item -LiteralPath $objectFile -ErrorAction SilentlyContinue
    if ($existingObject -and
        $existingObject.LastWriteTime -ge $buildScriptWriteTime -and
        $existingObject.LastWriteTime -ge $sourceWriteTime -and
        (!$latestHeaderWriteTime -or $existingObject.LastWriteTime -ge $latestHeaderWriteTime)) {
        Write-Output ("Reusing current UWP object: " + $existingObject.Name)
        $objectFiles += $objectFile
        continue
    }
    # Do not wrap $compileArguments as a nested array. PowerShell would pass
    # the complete argument list as one object, silently dropping the include
    # paths and language standard from cl.exe.
    $sourceArguments = @($compileArguments + "/Fo`"$objectFile`"" + "`"$source`"")
    Write-Verbose ('Native compiler arguments: ' + ($sourceArguments -join ' | '))
    & $cl @sourceArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $objectFiles += $objectFile
}

$link = Join-Path (Split-Path $cl -Parent) 'link.exe'
$linkArguments = @(
    '/NOLOGO',
    '/DLL',
    '/MACHINE:X64',
    '/DEBUG:FULL',
    '/OPT:REF',
    '/OPT:ICF',
    "/PDB:`"$(Join-Path $configurationOutput 'Ryujinx.Uwp.Native.pdb')`"",
    '/APPCONTAINER',
    '/SUBSYSTEM:WINDOWS',
    "/OUT:`"$dllPath`"",
    "/LIBPATH:`"$vcLib`"",
    "/LIBPATH:`"$sdkUcrtLib`"",
    "/LIBPATH:`"$sdkUmLib`"",
    'd3d11.lib',
    'd3d12.lib',
    'd3dcompiler.lib',
    'dxgi.lib',
    'dxguid.lib',
    'xaudio2.lib',
    'windowsapp.lib',
    ($objectFiles | ForEach-Object { "`"$_`"" })
)

Write-Verbose ('Native linker arguments: ' + ($linkArguments -join ' | '))
& $link @linkArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$builtDll = Join-Path $configurationOutput 'Ryujinx.Uwp.Native.dll'
if (-not (Test-Path $builtDll)) {
    throw "Compilação terminou sem gerar $builtDll"
}

Write-Output "Host nativo D3D11/D3D12 gerado: $builtDll"
