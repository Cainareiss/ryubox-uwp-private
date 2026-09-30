param(
    [string]$DevicePortal = 'https://192.168.100.20:11443',
    [string]$PackageFullName = 'Ryujinx.Xbox.UWP_0.1.0.3_x64__z7hww2mmeny5j',
    [string]$AppId = 'Ryujinx.Xbox.UWP_z7hww2mmeny5j!App'
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Hex64([string]$value) {
    $base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))
    return [Uri]::EscapeDataString($base64)
}

$cookie = Join-Path ([IO.Path]::GetTempPath()) ('ryujinx-xbox-wdp-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
    & curl.exe -k --max-time 20 -sS -c $cookie -o NUL "$DevicePortal/"
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível iniciar a sessão do Device Portal.' }

    $csrf = $null
    foreach ($line in (Get-Content -LiteralPath $cookie -ErrorAction SilentlyContinue)) {
        if ($line -match "\tCSRF-Token\t(?<value>[^\r\n]+)$") { $csrf = $Matches.value }
    }
    if ([string]::IsNullOrWhiteSpace($csrf)) { throw 'O Device Portal não forneceu o token CSRF da sessão.' }

    $appidHex = ConvertTo-Hex64 $AppId
    $packageHex = ConvertTo-Hex64 $PackageFullName
    $uri = "$DevicePortal/api/taskmanager/app?appid=$appidHex&package=$packageHex"
    $response = & curl.exe -k --max-time 30 -sS -b $cookie -c $cookie -H "X-CSRF-Token: $csrf" -H 'Content-Type: application/json' -X POST --data-binary '{}' $uri -w "`nHTTP_STATUS=%{http_code}`n" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Falha ao iniciar o pacote (curl $LASTEXITCODE): $($response -join ' ')" }
    $response | Write-Output
}
finally {
    Remove-Item -LiteralPath $cookie -Force -ErrorAction SilentlyContinue
}
