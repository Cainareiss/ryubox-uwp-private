param(
    [string] $MsixPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $MsixPath = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\artifacts\xbox\Release\AppPackages') -Recurse -File -Filter '*.msix' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($MsixPath) -or -not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
    throw "Nenhum MSIX encontrado. Compile primeiro com Build-RyujinxXbox.ps1."
}

$signature = Get-AuthenticodeSignature -LiteralPath $MsixPath
if ($signature.Status -ne 'Valid') {
    throw "O MSIX não está assinado com sucesso: $($signature.Status)."
}

$running = Get-Process -Name 'Ryujinx.Uwp.App' -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Add-AppxPackage -Path $MsixPath -ForceUpdateFromAnyVersion
$package = Get-AppxPackage -Name 'Ryujinx.Xbox.UWP'

[pscustomobject]@{
    Package = $package.Name
    Version = $package.Version
    Status = $package.Status
    PackageFullName = $package.PackageFullName
    Msix = $MsixPath
} | Format-List
