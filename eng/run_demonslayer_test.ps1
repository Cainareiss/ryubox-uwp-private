param(
    [int]$Seconds = 540,
    [int]$CaptureSequence = 1,
    [switch]$NoInput
)
$ErrorActionPreference = 'Stop'
$game = (Get-ChildItem -Path "E:\jogos para testar ryuboxz\*Demon Slayer*.nsp").FullName
Write-Host "Found game: $game"
$capturePath = "E:\ports\ryujinx uwp\artifacts\demonslayer_test.bmp"
if (Test-Path $capturePath) {
    Remove-Item $capturePath -Force
}
$keys = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\system'
Write-Host "Keys directory: $keys"

$env:RYUJINX_UWP_CAPTURE_MIN_COLORED = '1'
$env:RYUJINX_UWP_CAPTURE_MIN_ENERGY = '100'
$env:RYUJINX_UWP_CAPTURE_MIN_SECONDARY = '0'
$env:RYUJINX_UWP_FAST_EPOCH_JIT = '1'
$env:RYUJINX_UWP_MEMORY_6GIB = '1'
$env:RYUJINX_UWP_D3D12_DISABLE_GUEST_COUNTERS = '1'

& "$PSScriptRoot\Test-D3D12FrontendCoreHost.ps1" `
    -RuntimeDirectory 'E:\ports\ryujinx uwp\src\Ryujinx.Uwp.App\bin\x64\Release' `
    -GamePath $game `
    -KeysDirectory $keys `
    -CapturePath $capturePath `
    -Seconds $Seconds `
    -CaptureSequence $CaptureSequence `
    -NoInput:$NoInput

$foundBmp = $null
if (Test-Path $capturePath) {
    $foundBmp = $capturePath
} else {
    $series = Get-ChildItem -Path "E:\ports\ryujinx uwp\artifacts" -Filter "*demonslayer*.bmp" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($series) {
        $foundBmp = $series.FullName
    }
}

if ($foundBmp) {
    Add-Type -AssemblyName System.Drawing
    $pngPath = "E:\ports\ryujinx uwp\artifacts\demonslayer_test.png"
    $bmp = [System.Drawing.Bitmap]::FromFile($foundBmp)
    $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Saved PNG capture: $pngPath (from $foundBmp)"

    $artifactDir = "C:\Users\Cainã\.gemini\antigravity\brain\e31e7eb3-76ac-45f8-9fa2-098d19001d3e"
    if (Test-Path $artifactDir) {
        Copy-Item -Path $pngPath -Destination (Join-Path $artifactDir "demonslayer_test.png") -Force
        Write-Host "Copied capture to conversation artifact directory."
    }
} else {
    Write-Warning "No capture BMP found in artifacts."
}
