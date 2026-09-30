param(
    [int]$Seconds = 100
)
$ErrorActionPreference = 'Stop'
$game = (Get-ChildItem -Path 'E:\jogos para testar ryuboxz\*Undertale*.nsp').FullName
Write-Host "Found game: $game"
$capturePath = "E:\ports\ryujinx uwp\artifacts\undertale_test.bmp"
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
    -CaptureSequence 1
