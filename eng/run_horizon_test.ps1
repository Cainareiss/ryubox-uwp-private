param(
    [int]$Seconds = 300,
    [int]$CaptureSequence = 1800,
    [switch]$NoInput
)
$ErrorActionPreference = 'Stop'
$env:RYUJINX_UWP_CAPTURE_MIN_COLORED = '20000'
$game = (Get-ChildItem -Path 'E:\jogos para testar ryuboxz\*Horizon*Chase*.nsp').FullName
Write-Host "Found game: $game"
$capturePath = "E:\ports\ryujinx uwp\artifacts\horizon_test.bmp"
if (Test-Path $capturePath) {
    Remove-Item $capturePath -Force
}
$keys = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\system'
Write-Host "Keys directory: $keys"

& "$PSScriptRoot\Test-D3D12FrontendCoreHost.ps1" `
    -RuntimeDirectory 'E:\ports\ryujinx uwp\src\Ryujinx.Uwp.App\bin\x64\Release' `
    -GamePath $game `
    -KeysDirectory $keys `
    -CapturePath $capturePath `
    -Seconds $Seconds `
    -CaptureSequence $CaptureSequence `
    -NoInput:$NoInput

if (Test-Path $capturePath) {
    Add-Type -AssemblyName System.Drawing
    $pngPath = "E:\ports\ryujinx uwp\artifacts\horizon_test.png"
    $bmp = [System.Drawing.Bitmap]::FromFile($capturePath)
    $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Saved PNG capture: $pngPath"
}
