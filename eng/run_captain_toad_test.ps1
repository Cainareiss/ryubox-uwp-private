param(
    [int]$Seconds = 35
)
$ErrorActionPreference = 'Stop'
$game = (Get-ChildItem -Path 'E:\jogos para testar ryuboxz\*Captain*Toad*.nsp').FullName
Write-Host "Found game: $game"
$capturePath = "E:\ports\ryujinx uwp\artifacts\captain_toad_test.bmp"
if (Test-Path $capturePath) {
    Remove-Item $capturePath -Force
}
$keys = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\system'
Write-Host "Keys directory: $keys"
$env:RYUJINX_UWP_D3D12_SHADER_OPT0 = '1'

& "$PSScriptRoot\Test-D3D12FrontendCoreHost.ps1" `
    -RuntimeDirectory 'E:\ports\ryujinx uwp\src\Ryujinx.Uwp.App\bin\x64\Release' `
    -GamePath $game `
    -KeysDirectory $keys `
    -CapturePath $capturePath `
    -Seconds $Seconds `
    -CaptureSequence 1
