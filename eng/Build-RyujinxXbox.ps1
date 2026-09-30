param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $CacheRoot = (Join-Path $PSScriptRoot '..\.cache'),
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\xbox'),
    [string] $IntermediatePackageDirectory,
    [string] $CertificateDirectory = (Join-Path $PSScriptRoot '..\.cache\certificates'),
    [string] $CertificatePassword = 'RyujinxXboxDev',
    [string] $MesaStageDirectory = (Join-Path $PSScriptRoot '..\artifacts\xbox\MesaDozenHeadlessXbox'),
    [string] $NativeToolchainRoot = (Join-Path $PSScriptRoot '..\artifacts\vs-buildtools'),
    [string] $NativeSdkRoot = 'C:\Program Files (x86)\Windows Kits\10',
    [string] $NativeSdkVersion = '10.0.26100.0',
    [string] $MesaSourceDirectory = (Join-Path $PSScriptRoot '..\artifacts\mesa'),
    [string] $MesaBuildDirectory = (Join-Path $PSScriptRoot '..\artifacts\mesa-build'),
    [string] $MesaInstallDirectory = (Join-Path $PSScriptRoot '..\artifacts\mesa-install'),
    [string] $SpirvCrossBinary = (Join-Path $PSScriptRoot '..\third_party\SPIRV-Cross\build-uwp-x64-msbuild\spirv-cross-c-shared.dll'),
    [string] $FfmpegStageDirectory = (Join-Path $PSScriptRoot '..\third_party\ffmpeg-5.1.6-win64-lgpl-shared\package\ffmpeg-n5.1.6-16-g6e63e49496-win64-lgpl-shared-5.1\bin'),
    [string] $CoreHostBinary,
    [string] $NativeHostBinary,
    # DZN first looks for the Xbox compiler and then falls back to DXIL.dll
    # beside the driver. Keep the fallback inside the package.
    [string] $DxilRuntimeDirectory = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64',
    [string] $VCLibsPackage = 'C:\Program Files (x86)\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs\14.0\Appx\Retail\x64\Microsoft.VCLibs.x64.14.00.appx',
    [string] $VulkanLoaderDll,
    [switch] $BuildMesaDozen,
    [switch] $IncludeMesaDozen,
    [switch] $SkipSpirvCrossBuild,
    [switch] $SkipCoreHostBuild,
    [switch] $SkipNativeHostBuild,
    [switch] $SkipRestore
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path $repoRoot 'src\Ryujinx.Uwp.App'
$projectFile = Join-Path $projectDirectory 'Ryujinx.Uwp.App.csproj'
$coreHostProject = Join-Path $repoRoot 'src\Ryujinx.Uwp.CoreHost\Ryujinx.Uwp.CoreHost.csproj'
if ([string]::IsNullOrWhiteSpace($CoreHostBinary)) {
    # dotnet publish does not include the MSBuild Platform folder in this
    # project's output path. The old bin\x64 path could retain a stale DLL
    # indefinitely while each build successfully compiled a newer CoreHost
    # into bin\Release and then packaged the stale binary.
    $CoreHostBinary = Join-Path $repoRoot "src\Ryujinx.Uwp.CoreHost\bin\$Configuration\net10.0\win-x64\publish\Ryujinx.Uwp.CoreHost.dll"
}
$coreHostBinary = [IO.Path]::GetFullPath($CoreHostBinary)
$skiaSharpNativeBinary = Join-Path (Split-Path -Parent $coreHostBinary) 'libSkiaSharp.dll'
if ([string]::IsNullOrWhiteSpace($NativeHostBinary)) {
    $NativeHostBinary = Join-Path $repoRoot "src\Ryujinx.Uwp.Native\bin\x64\$Configuration\Ryujinx.Uwp.Native.dll"
}
$nativeHostBinary = [IO.Path]::GetFullPath($NativeHostBinary)
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'
$nugetPackages = Join-Path $CacheRoot 'nuget\packages'
$nugetHttp = Join-Path $CacheRoot 'nuget\http'
$nugetPlugins = Join-Path $CacheRoot 'nuget\plugins'
$tempRoot = Join-Path $CacheRoot 'temp'
$packageOutput = Join-Path $OutputRoot "$Configuration\AppPackages"
if ([string]::IsNullOrWhiteSpace($IntermediatePackageDirectory)) {
    $IntermediatePackageDirectory = Join-Path $OutputRoot 'msbuild\AppPackages'
}
$IntermediatePackageDirectory = [IO.Path]::GetFullPath($IntermediatePackageDirectory)

$ffmpegRequiredFiles = @('avcodec-59.dll', 'avutil-57.dll', 'swresample-4.dll')
foreach ($ffmpegFile in $ffmpegRequiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $FfmpegStageDirectory $ffmpegFile))) {
        throw "FFmpeg compartilhado compatível não encontrado: $(Join-Path $FfmpegStageDirectory $ffmpegFile)"
    }
}
$certificateFile = Join-Path $CertificateDirectory 'RyujinxXboxDevCodeSign.pfx'
$publicCertificateFile = Join-Path $CertificateDirectory 'RyujinxXboxDevCodeSign.cer'

if (-not (Test-Path $msbuild)) { throw "MSBuild não encontrado: $msbuild" }

New-Item -ItemType Directory -Force -Path $nugetPackages, $nugetHttp, $nugetPlugins, $tempRoot, $packageOutput, $CertificateDirectory | Out-Null
$env:NUGET_PACKAGES = $nugetPackages
$env:NUGET_HTTP_CACHE_PATH = $nugetHttp
$env:NUGET_PLUGINS_CACHE_PATH = $nugetPlugins
$env:TEMP = $tempRoot
$env:TMP = $tempRoot

# NativeAOT's Windows linker target invokes vswhere.exe to resolve the
# installed MSVC toolset. Visual Studio can be installed without its
# installer directory being present in the inherited PATH.
$vsInstallerDirectory = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path -LiteralPath (Join-Path $vsInstallerDirectory 'vswhere.exe')) -and
    (($env:PATH -split ';') -notcontains $vsInstallerDirectory)) {
    $env:PATH = "$vsInstallerDirectory;$env:PATH"
}

if (-not $SkipNativeHostBuild) {
    & (Join-Path $PSScriptRoot 'Build-RyujinxUwpNative.ps1') -Configuration $Configuration -ToolchainRoot $NativeToolchainRoot -SdkRoot $NativeSdkRoot -SdkVersion $NativeSdkVersion -SkipIfUnavailable
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $SkipSpirvCrossBuild) {
    $spirvCrossBuild = Join-Path $repoRoot 'tools\build-spirv-cross-uwp.cmd'
    if (-not (Test-Path -LiteralPath $spirvCrossBuild)) {
        throw "SPIRV-Cross D3D11 ausente e script de build nao encontrado: $SpirvCrossBinary"
    }

    & cmd.exe /d /c $spirvCrossBuild
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $SpirvCrossBinary)) {
    throw "A compilacao terminou sem gerar o SPIRV-Cross UWP: $SpirvCrossBinary"
}

# Prevent a desktop SPIRV-Cross build from silently entering an Xbox package.
# UWP builds use the static CRT and carry the PE AppContainer flag.
$dumpbin = Get-ChildItem -LiteralPath (Join-Path $NativeToolchainRoot 'VC\Tools\MSVC') -Recurse -File -Filter 'dumpbin.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Hostx64\\x64\\dumpbin\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $dumpbin) {
    throw "dumpbin x64 nao encontrado no toolchain: $NativeToolchainRoot"
}

if (-not (Test-Path -LiteralPath $nativeHostBinary)) {
    throw "Host nativo UWP ausente: $nativeHostBinary"
}

$nativeDependencies = & $dumpbin.FullName /dependents $nativeHostBinary
if ($nativeDependencies -match '(?im)^\s+(?:MSVCP|VCRUNTIME)\d[^\s]*\.dll\s*$') {
    throw "Host nativo usa o CRT desktop e nao pode ser empacotado para Xbox UWP: $nativeHostBinary"
}
if (($nativeDependencies -join "`n") -notmatch '(?im)^\s+XAudio2_9\.dll\s*$') {
    throw "Host nativo sem a dependencia XAudio2 UWP do sistema: $nativeHostBinary"
}

$nativeExports = & $dumpbin.FullName /exports $nativeHostBinary
foreach ($requiredAudioExport in @(
    'RyujinxUwpAudio_CreateSession',
    'RyujinxUwpAudio_Submit',
    'RyujinxUwpAudio_IsConsumed',
    'RyujinxUwpAudio_GetPlayedFrameCount',
    'RyujinxUwpAudio_GetBufferStats',
    'RyujinxUwpFrontend_SetPtcEnabled',
    'RyujinxUwpFrontend_ConsumePtcChange',
    'RyujinxUwpFrontend_SetFpsEnabled',
    'RyujinxUwpFrontend_ConsumeFpsChange',
    'RyuboxD3D12_Create',
    'RyuboxD3D12Frontend_Initialize',
    'RyuboxD3D12Frontend_Acquire',
    'RyuboxD3D12Frontend_Release',
    'RyuboxD3D12Frontend_GetDeviceStatus',
    'RyujinxUwpGraphicsD3D11_CopyRendererTextureV2',
    'RyujinxUwpGraphicsD3D11_PresentRendererFrameWithSyncInterval'
)) {
    if (($nativeExports -join "`n") -notmatch [Regex]::Escape($requiredAudioExport)) {
        throw "Host nativo sem export obrigatorio de audio: $requiredAudioExport"
    }
}

$nativeHeaders = & $dumpbin.FullName /headers $nativeHostBinary
if (($nativeHeaders -join "`n") -notmatch '(?im)^\s+App Container\s*$') {
    throw "Host nativo nao possui o marcador PE AppContainer: $nativeHostBinary"
}

$spirvDependencies = & $dumpbin.FullName /dependents $SpirvCrossBinary
if ($spirvDependencies -match '(?im)^\s+(?:MSVCP|VCRUNTIME)\d[^\s]*\.dll\s*$') {
    throw "SPIRV-Cross usa o CRT desktop e nao pode ser empacotado para Xbox UWP: $SpirvCrossBinary"
}

$spirvHeaders = & $dumpbin.FullName /headers $SpirvCrossBinary
if (($spirvHeaders -join "`n") -notmatch '(?im)^\s+App Container\s*$') {
    throw "SPIRV-Cross nao possui o marcador PE AppContainer: $SpirvCrossBinary"
}

if (-not $SkipCoreHostBuild) {
    & dotnet publish $coreHostProject -c $Configuration -r win-x64 --self-contained true --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $skiaSharpNativeBinary)) {
    throw "Dependencia nativa do CoreHost ausente: $skiaSharpNativeBinary"
}

if ($BuildMesaDozen) {
    $mesaArguments = @(
        '-MesaDir', $MesaSourceDirectory,
        '-BuildDir', $MesaBuildDirectory,
        '-InstallDir', $MesaInstallDirectory,
        '-StageDir', $MesaStageDirectory
    )
    if (-not [string]::IsNullOrWhiteSpace($VulkanLoaderDll)) {
        $mesaArguments += @('-VulkanLoaderDll', $VulkanLoaderDll)
    }

    & (Join-Path $PSScriptRoot 'Build-RyujinxMesaDozen.ps1') @mesaArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not [string]::IsNullOrWhiteSpace($VulkanLoaderDll)) {
    if (-not (Test-Path -LiteralPath $VulkanLoaderDll)) {
        throw "Loader Vulkan UWP nÃ£o encontrado: $VulkanLoaderDll"
    }

    New-Item -ItemType Directory -Force -Path $MesaStageDirectory | Out-Null
    Copy-Item -LiteralPath $VulkanLoaderDll -Destination (Join-Path $MesaStageDirectory 'vulkan-1.dll') -Force
}

# Mesa's DZN validator is loaded during vkCreateInstance. Without it the ICD
# reports only the generic VK_ERROR_OUT_OF_HOST_MEMORY result.
New-Item -ItemType Directory -Force -Path $MesaStageDirectory | Out-Null
$dxilPackaged = Join-Path $MesaStageDirectory 'DXIL.dll'
$dxilCandidates = @(
    $dxilPackaged,
    (Join-Path $DxilRuntimeDirectory 'DXIL.dll'),
    (Join-Path $DxilRuntimeDirectory 'dxil.dll'),
    (Join-Path (Join-Path $repoRoot 'artifacts\corehost-local-xbox-phase3') 'DXIL.dll')
)
$dxilSource = $dxilCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$xboxCompilerSource = @(
    (Join-Path $MesaStageDirectory 'dxcompiler_x.dll'),
    (Join-Path $MesaStageDirectory 'dxcompiler_xs.dll'),
    (Join-Path $DxilRuntimeDirectory 'dxcompiler_x.dll'),
    (Join-Path $DxilRuntimeDirectory 'dxcompiler_xs.dll')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($dxilSource) {
    $resolvedPackaged = (Resolve-Path -LiteralPath $dxilPackaged -ErrorAction SilentlyContinue).Path
    $resolvedSource = (Resolve-Path -LiteralPath $dxilSource).Path
    if (-not [string]::Equals($resolvedSource, $resolvedPackaged, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $dxilSource -Destination $dxilPackaged -Force
    }
} elseif (-not $xboxCompilerSource) {
    throw "Mesa Dozen precisa de DXIL.dll ou de dxcompiler_x.dll/dxcompiler_xs.dll. Nenhum runtime foi encontrado em '$DxilRuntimeDirectory'."
}

# Preserve Xbox-specific names if a GDK/runtime folder supplies them. DXIL.dll
# remains the portable fallback for this UWP build.
foreach ($compilerName in @('dxcompiler_x.dll', 'dxcompiler_xs.dll', 'dxcompiler.dll')) {
    $destination = Join-Path $MesaStageDirectory $compilerName
    if (Test-Path -LiteralPath $destination) {
        continue
    }

    $source = Join-Path $DxilRuntimeDirectory $compilerName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}

if (Test-Path -LiteralPath $dxilPackaged) {
    $stagedDxil = Get-Item -LiteralPath $dxilPackaged
    Write-Output "Mesa Dozen DXIL runtime staged: $($stagedDxil.FullName) ($($stagedDxil.Length) bytes)"
} else {
    Write-Output "Mesa Dozen Xbox compiler staged: $xboxCompilerSource"
}

if (-not (Test-Path $certificateFile)) {
    $securePassword = ConvertTo-SecureString $CertificatePassword -AsPlainText -Force
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=Ryujinx Xbox Dev' -FriendlyName 'Ryujinx Xbox Dev Code Signing' -KeyExportPolicy Exportable -KeySpec Signature -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') -CertStoreLocation 'Cert:\CurrentUser\My'
    Export-PfxCertificate -Cert $certificate -FilePath $certificateFile -Password $securePassword | Out-Null
    Export-Certificate -Cert $certificate -FilePath $publicCertificateFile | Out-Null
}

if (-not $SkipRestore) {
    & dotnet restore $projectFile --packages $nugetPackages --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$properties = @(
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    '/p:AppxBundle=Never',
    '/p:AppxBundlePlatforms=x64',
    '/p:UapAppxPackageBuildMode=SideLoadOnly',
    "/p:AppxPackageDir=$($IntermediatePackageDirectory.TrimEnd('\'))/",
    '/p:AppxPackageSigningEnabled=false',
    '/p:GenerateAppInstallerFile=false',
    "/p:MesaStageDirectory=$MesaStageDirectory",
    "/p:IncludeMesaDozen=$($BuildMesaDozen.IsPresent -or $IncludeMesaDozen.IsPresent)",
    "/p:SpirvCrossBinary=$SpirvCrossBinary",
    "/p:FfmpegStageDirectory=$FfmpegStageDirectory",
    "/p:CoreHostBinary=$coreHostBinary",
    "/p:NativeHostBinary=$nativeHostBinary"
)

& $msbuild $projectFile @properties /nologo /m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$generatedPackage = Get-ChildItem -LiteralPath $IntermediatePackageDirectory -Recurse -File -Filter '*.msix' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $generatedPackage) { throw "MSBuild terminou sem gerar um arquivo .msix" }

$generatedPackageDirectory = $generatedPackage.Directory.FullName
$artifactDirectory = Join-Path $packageOutput $generatedPackage.Directory.Name
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Copy-Item -Path (Join-Path $generatedPackageDirectory '*') -Destination $artifactDirectory -Recurse -Force

# The manifest declares Microsoft.VCLibs.140.00, but MSBuild does not always
# stage the x64 framework package when the project is built outside the Visual
# Studio packaging UI. Mesa Dozen and the native UWP shim use the UWP CRT, so
# ship the matching framework beside the MSIX for Xbox Device Portal/Dev Home
# installation. Without it, DllImport reports 0x8007007E and the Vulkan ICD
# can surface VK_ERROR_OUT_OF_HOST_MEMORY while loading its CRT dependencies.
if (-not (Test-Path -LiteralPath $VCLibsPackage)) {
    throw "Microsoft.VCLibs x64 nÃ£o encontrado: $VCLibsPackage"
}

$vclibsDirectory = Join-Path $artifactDirectory 'Dependencies\x64'
New-Item -ItemType Directory -Force -Path $vclibsDirectory | Out-Null
Copy-Item -LiteralPath $VCLibsPackage -Destination $vclibsDirectory -Force

$signedPackage = Join-Path $artifactDirectory $generatedPackage.Name
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path $signtool)) { throw "SignTool não encontrado: $signtool" }

& $signtool sign /fd SHA256 /f $certificateFile /p $CertificatePassword /d 'Ryujinx Xbox UWP' $signedPackage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$signature = Get-AuthenticodeSignature -FilePath $signedPackage
if ($signature.Status -eq 'NotSigned' -or $null -eq $signature.SignerCertificate) {
    throw "O MSIX final não contém uma assinatura: $signedPackage"
}

Copy-Item -LiteralPath $publicCertificateFile -Destination $artifactDirectory -Force

$packagedFileList = & tar.exe -tf $signedPackage
foreach ($requiredPackageFile in @('Ryujinx.Uwp.Native.dll', 'Ryujinx.Uwp.CoreHost.dll', 'libSkiaSharp.dll', 'spirv-cross-c-shared.dll', 'avcodec-59.dll', 'avutil-57.dll', 'swresample-4.dll')) {
    if ($packagedFileList -notcontains $requiredPackageFile) {
        throw "Pacote D3D11 incompleto; arquivo ausente no MSIX: $requiredPackageFile"
    }
}

# A successful MSIX build is not enough: verify that incremental UWP staging
# did not package an older CoreHost. This check makes the source build and the
# signed artifact cryptographically identical before it can be handed off.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expectedCoreHostHash = (Get-FileHash -LiteralPath $coreHostBinary -Algorithm SHA256).Hash
$packageArchive = [System.IO.Compression.ZipFile]::OpenRead($signedPackage)
try {
    $coreHostEntry = $packageArchive.GetEntry('Ryujinx.Uwp.CoreHost.dll')
    if ($null -eq $coreHostEntry) {
        throw 'Ryujinx.Uwp.CoreHost.dll ausente no arquivo ZIP interno do MSIX.'
    }

    $entryStream = $coreHostEntry.Open()
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $packagedCoreHostHash = [BitConverter]::ToString($sha256.ComputeHash($entryStream)).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $entryStream.Dispose()
    }
}
finally {
    $packageArchive.Dispose()
}

if ($packagedCoreHostHash -ne $expectedCoreHostHash) {
    throw "CoreHost incorreto no MSIX: esperado=$expectedCoreHostHash empacotado=$packagedCoreHostHash origem=$coreHostBinary"
}
Write-Output "CoreHost verificado no MSIX: SHA256=$packagedCoreHostHash"

# A matching CoreHost with a stale native/compiler DLL still breaks the ABI.
$packageArchive = [System.IO.Compression.ZipFile]::OpenRead($signedPackage)
try {
    foreach ($binary in @(@('Ryujinx.Uwp.Native.dll',$nativeHostBinary), @('spirv-cross-c-shared.dll',$SpirvCrossBinary), @('libSkiaSharp.dll',$skiaSharpNativeBinary))) {
        $entry=$packageArchive.GetEntry($binary[0])
        if (-not $entry) { throw "Binary absent from MSIX: $($binary[0])" }
        $stream=$entry.Open()
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $expected=(Get-FileHash -LiteralPath $binary[1] -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Stale binary in MSIX: $($binary[0])" }
        Write-Output "$($binary[0]) verified in MSIX: SHA256=$actual"
    }
} finally { $packageArchive.Dispose() }

if (-not ($BuildMesaDozen.IsPresent -or $IncludeMesaDozen.IsPresent)) {
    foreach ($excludedD3D11File in @(
        'vulkan_dzn.dll',
        'vulkan-1.dll',
        'spirv_to_dxil.dll',
        'DXIL.dll',
        'dxcompiler.dll',
        'expat.dll',
        'z-1.dll')) {
        if ($packagedFileList -contains $excludedD3D11File) {
            throw "Pacote D3D11-only contem uma dependencia Vulkan/Mesa incompatível: $excludedD3D11File"
        }
    }
}

Write-Output "MSIX assinado: $signedPackage"
Write-Output "Certificado para instalar no Xbox: $(Join-Path $artifactDirectory (Split-Path $publicCertificateFile -Leaf))"
