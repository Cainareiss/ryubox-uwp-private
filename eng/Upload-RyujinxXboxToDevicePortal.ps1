param(
    [string]$DevicePortal = 'https://192.168.100.20:11443',
    [string]$PackageRoot = 'E:\ports\ryujinx uwp\artifacts\xbox\Release\AppPackages\Ryujinx.Uwp.App_0.1.0.3_x64_Test'
)

$ErrorActionPreference = 'Stop'

$main = Join-Path $PackageRoot 'Ryujinx.Uwp.App_0.1.0.3_x64.msix'
$depRuntime = Join-Path $PackageRoot 'Dependencies\x64\Microsoft.NET.CoreRuntime.2.2.appx'
$depFramework = Join-Path $PackageRoot 'Dependencies\x64\Microsoft.NET.CoreFramework.Debug.2.2.appx'

foreach ($file in @($main, $depRuntime, $depFramework)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Arquivo de pacote ausente: $file"
    }
}

$cookie = Join-Path ([IO.Path]::GetTempPath()) ('ryujinx-xbox-wdp-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
    & curl.exe -k --max-time 20 -sS -c $cookie -o NUL "$DevicePortal/"
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível iniciar a sessão do Device Portal.' }

    $csrf = $null
    foreach ($line in (Get-Content -LiteralPath $cookie -ErrorAction SilentlyContinue)) {
        if ($line -match "\tCSRF-Token\t(?<value>[^\r\n]+)$") {
            $csrf = $Matches.value
        }
    }
    if ([string]::IsNullOrWhiteSpace($csrf)) {
        throw 'O Device Portal não forneceu o token CSRF da sessão.'
    }

    $uploadUri = "$DevicePortal/api/app/packagemanager/package?package=Ryujinx.Uwp.App_0.1.0.3_x64.msix"
    $curlArgs = @(
        '-k', '--max-time', '240', '-sS', '-b', $cookie, '-c', $cookie,
        '-H', "X-CSRF-Token: $csrf",
        '-X', 'POST', $uploadUri,
        '-F', "package=@$main",
        '-F', "dependency=@$depRuntime",
        '-F', "dependency=@$depFramework",
        '-w', "`nHTTP_STATUS=%{http_code}`n"
    )

    $response = & curl.exe @curlArgs 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Falha no upload do MSIX (curl $exitCode): $($response -join ' ')"
    }

    if ($response) { $response | Write-Output }
}
finally {
    Remove-Item -LiteralPath $cookie -Force -ErrorAction SilentlyContinue
}
