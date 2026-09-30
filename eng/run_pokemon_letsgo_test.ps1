param(
    [int]$Seconds = 60,
    [int]$CaptureSequence = 30,
    [switch]$NoInput
)
$ErrorActionPreference = 'Stop'
$game = (Get-ChildItem -Path "E:\jogos para testar ryuboxz\*Let*Go*Pikachu*.nsp").FullName
Write-Host "Found game: $game"
$capturePath = "E:\ports\ryujinx uwp\artifacts\pokemon_letsgo_test.bmp"
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
    $pngPath = "E:\ports\ryujinx uwp\artifacts\pokemon_letsgo_test.png"
    $bmp = [System.Drawing.Bitmap]::FromFile($capturePath)
    $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Saved PNG capture: $pngPath"
}
