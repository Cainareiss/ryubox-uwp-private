param(
    [string] $PackagePath = '',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Assert-Contains {
    param(
        [string] $Text,
        [string] $Pattern,
        [string] $Failure
    )

    if ($Text -notmatch $Pattern) {
        throw $Failure
    }
}

function Assert-NotContains {
    param(
        [string] $Text,
        [string] $Pattern,
        [string] $Failure
    )

    if ($Text -match $Pattern) {
        throw $Failure
    }
}

$manifestPath = Join-Path $repoRoot 'src\Ryujinx.Uwp.App\Package.appxmanifest'
$coreHostPath = Join-Path $repoRoot 'src\Ryujinx.Uwp.CoreHost\CoreHostExports.cs'
$nativePath = Join-Path $repoRoot 'src\Ryujinx.Uwp.Native\Ryujinx.Uwp.Native.cpp'
$d3d11NativePath = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.D3D11\D3D11Native.cs'
$rendererPath = Join-Path $repoRoot 'src\src\Ryujinx.Graphics.D3D11\D3D11Renderer.cs'

$manifest = Get-Content -LiteralPath $manifestPath -Raw
$coreHost = Get-Content -LiteralPath $coreHostPath -Raw
$native = Get-Content -LiteralPath $nativePath -Raw
$d3d11Native = Get-Content -LiteralPath $d3d11NativePath -Raw
$renderer = Get-Content -LiteralPath $rendererPath -Raw

Assert-Contains $coreHost 'SetSwitch\("Ryujinx\.Uwp\.SafeMonotonicClock", false\)' 'High-resolution QPC clock was not restored.'
Assert-Contains $coreHost 'UwpSvcTelemetry\.GetSnapshot' 'Detailed SVC telemetry is missing.'
Assert-Contains $coreHost 'UwpKernelCriticalSectionTelemetry\.GetSnapshot' 'Kernel critical-section telemetry is missing.'
Assert-Contains $coreHost 'UwpGpfifoTelemetry\.GetSnapshot' 'GPFIFO telemetry is missing.'

Assert-Contains $manifest 'Version="0\.1\.1\.51"' 'Manifesto não está na versão Kernel Sync 0.1.1.51.'
Assert-Contains $manifest '<Capability Name="codeGeneration"\s*/>' 'Capability codeGeneration ausente; ARMeilleure não poderá emitir código no Xbox.'
Assert-Contains $manifest '<rescap:Capability Name="expandedResources"\s*/>' 'Capability expandedResources ausente; orçamento de memória do Xbox ficará limitado.'
Assert-Contains $coreHost 'Native_D3D11_BeginCoreSession' 'CoreHost não inicia a posse exclusiva do D3D11.'
Assert-Contains $coreHost 'Native_D3D11_BindGraphicsThread' 'CoreHost não vincula a GPU thread ao host nativo.'
Assert-NotContains $coreHost 'PresentStartupHeartbeat' 'O heartbeat antigo ainda está compilado e pode competir com a GPU thread.'
Assert-Contains $native 'SetMultithreadProtected\(TRUE\)' 'Proteção multithread do immediate context ausente.'
Assert-Contains $native 'SetMaximumFrameLatency\(2\)' 'Limite da fila de apresentação ausente.'
Assert-Contains $native 'if \(m_coreSessionActive\)\s*\{\s*return;\s*\}' 'Shutdown do host não protege uma sessão ativa.'
Assert-Contains $d3d11Native 'GetDelegate<ResolveSubresourceDelegate>\(_self, 57\)' 'ResolveSubresource não usa o slot ABI 57.'
Assert-Contains $d3d11Native 'GetDelegate<FlushDelegate>\(_self, 111\)' 'Flush não usa o slot ABI 111.'
Assert-Contains $renderer 'supportsImageLoadFormatted: false' 'Capabilities ainda anunciam image load/store tipado não implementado.'
Assert-Contains $renderer 'maximumTexturesPerStage: 16' 'Limite de samplers D3D11 não está aplicado.'

if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
    $packageFiles = @(& tar.exe -tf $resolvedPackage)
    foreach ($required in @('Ryujinx.Uwp.Native.dll', 'Ryujinx.Uwp.CoreHost.dll', 'spirv-cross-c-shared.dll', 'AppxManifest.xml')) {
        if ($packageFiles -notcontains $required) {
            throw "Arquivo obrigatório ausente no MSIX: $required"
        }
    }

    foreach ($forbidden in @('vulkan_dzn.dll', 'vulkan-1.dll', 'spirv_to_dxil.dll', 'DXIL.dll', 'dxcompiler.dll')) {
        if ($packageFiles -contains $forbidden) {
            throw "Dependência Vulkan/Mesa encontrada no MSIX D3D11-only: $forbidden"
        }
    }

    $packagedManifest = (& tar.exe -xOf $resolvedPackage AppxManifest.xml) -join "`n"
    Assert-Contains $packagedManifest 'Version="0\.1\.1\.51"' 'MSIX não contém o manifesto Kernel Sync 0.1.1.51.'
    Assert-Contains $packagedManifest '<Capability Name="codeGeneration"\s*/>' 'MSIX perdeu a capability codeGeneration.'
    Assert-Contains $packagedManifest '<rescap:Capability Name="expandedResources"\s*/>' 'MSIX perdeu a capability expandedResources.'

    $signature = Get-AuthenticodeSignature -FilePath $resolvedPackage
    if ($signature.Status -eq 'NotSigned' -or $null -eq $signature.SignerCertificate) {
        throw 'MSIX sem assinatura digital.'
    }
}

$nativeBinary = Join-Path $repoRoot "src\Ryujinx.Uwp.Native\bin\x64\$Configuration\Ryujinx.Uwp.Native.dll"
if (Test-Path -LiteralPath $nativeBinary) {
    $dumpbin = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022' -Recurse -File -Filter dumpbin.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\Hostx64\\x64\\dumpbin\.exe$' } |
        Select-Object -First 1

    if ($dumpbin) {
        $exports = (& $dumpbin.FullName /nologo /exports $nativeBinary) -join "`n"
        foreach ($export in @(
            'RyujinxUwpGraphicsD3D11_BeginCoreSession',
            'RyujinxUwpGraphicsD3D11_BindGraphicsThread',
            'RyujinxUwpGraphicsD3D11_EndCoreSession',
            'RyujinxUwpGraphicsD3D11_GetOwnershipState')) {
            Assert-Contains $exports ([regex]::Escape($export)) "Export nativo ausente: $export"
        }
    }
}

Write-Output 'NATIVE_GS_VALIDATION=PASS'
