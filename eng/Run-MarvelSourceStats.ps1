$env:RYUJINX_UWP_D3D11_SOURCE_STATS = '1'
$env:RYUJINX_UWP_D3D11_PRESENT_COPY_RESOURCE = '0'
$env:RYUJINX_UWP_D3D11_SOURCE_STATS_PATH = 'E:\ports\ryujinx uwp\artifacts\marvel-source-stats-20260906.log'
& "$PSScriptRoot\Test-RyujinxD3D11DirectHost.ps1" `
    -GamePath 'E:\jogos para testar ryuboxz\MARVEL Cosmic Invasion.nsp' `
    -KeysDirectory 'E:\switchkeys.io-v22.5.0' `
    -RuntimeDirectory 'C:\Users\Cainã\AppData\Local\RyuboxMarvelSourceStats-20260906' `
    -EnablePtc -EnableD3D11Telemetry -EnableGuestLogs `
    -AutoStartMatch -AutoStartStopSeconds 180
