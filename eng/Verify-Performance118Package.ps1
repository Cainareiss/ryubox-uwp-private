param(
    [string] $Package = (Join-Path $PSScriptRoot '..\artifacts\xbox\Release\AppPackages\Ryujinx.Uwp.App_0.2.0.118_x64_Test\Ryujinx.Uwp.App_0.2.0.118_x64.msix'),
    [string] $ExpectedVersion = '0.2.0.118'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$packagePath = [IO.Path]::GetFullPath($Package)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    foreach ($pair in @(
        @('Ryujinx.Uwp.CoreHost.dll', 'src\Ryujinx.Uwp.CoreHost\bin\Release\net10.0\win-x64\publish\Ryujinx.Uwp.CoreHost.dll'),
        @('Ryujinx.Uwp.Native.dll', 'src\Ryujinx.Uwp.Native\bin\x64\Release\Ryujinx.Uwp.Native.dll')
    )) {
        $entry = $archive.GetEntry($pair[0])
        if (-not $entry) { throw "Missing package DLL: $($pair[0])" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $expected = (Get-FileHash -LiteralPath (Join-Path $repo $pair[1]) -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Stale package DLL: $($pair[0]) expected=$expected actual=$actual" }
        Write-Output "PASS $($pair[0]) SHA256=$actual"
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.Package.Identity.Version -ne $ExpectedVersion) { throw 'Unexpected package version' }
    if ($manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Unexpected architecture' }
    Write-Output "PASS identity=$($manifest.Package.Identity.Name) version=$($manifest.Package.Identity.Version) architecture=x64"
} finally { $archive.Dispose() }
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
& $signtool verify /pa /v $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MSIX signature verification failed' }
Write-Output "MSIX SHA256=$((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash)"
Write-Output "MSIX PATH=$packagePath"
