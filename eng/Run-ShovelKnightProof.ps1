param(
    [string] $GameDirectory = 'E:\switchkeys.io-v22.5.0',
    [string] $ProofRoot = (Join-Path $PSScriptRoot '..\artifacts\proof\shovel-knight'),
    [int] $StopAfterSeconds = 45
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$core = Join-Path $repoRoot 'src\Ryujinx\bin\Release\net10.0\Ryujinx.exe'
$game = Get-ChildItem -LiteralPath $GameDirectory -File -Filter '*.nsp' |
    Where-Object { $_.Name -match 'Shovel Knight' } |
    Select-Object -First 1
$system = Join-Path $ProofRoot 'system'
$log = Join-Path (Split-Path -Parent $ProofRoot) 'shovel-knight-run.log'
$errorLog = Join-Path (Split-Path -Parent $ProofRoot) 'shovel-knight-run-error.log'

if (-not (Test-Path -LiteralPath $core)) { throw "Core nao compilado: $core" }
if (-not $game) { throw "NSP do Shovel Knight nao encontrado em $GameDirectory" }
foreach ($keyName in @('prod.keys', 'title.keys')) {
    $keyPath = Join-Path $GameDirectory $keyName
    if (-not (Test-Path -LiteralPath $keyPath)) { throw "Chave ausente: $keyPath" }
}

New-Item -ItemType Directory -Force -Path $system | Out-Null
Copy-Item -LiteralPath (Join-Path $GameDirectory 'prod.keys') -Destination (Join-Path $system 'prod.keys') -Force
Copy-Item -LiteralPath (Join-Path $GameDirectory 'title.keys') -Destination (Join-Path $system 'title.keys') -Force

$argumentLine = '--no-gui --root-data-dir "' + (Resolve-Path -LiteralPath $ProofRoot).Path + '" --disable-shader-cache --disable-file-logging "' + $game.FullName + '"'
$process = Start-Process -FilePath $core -ArgumentList $argumentLine -WorkingDirectory (Split-Path -Parent $core) -RedirectStandardOutput $log -RedirectStandardError $errorLog -WindowStyle Hidden -PassThru

Write-Output "PID: $($process.Id)"
Write-Output "Jogo: $($game.FullName)"
Write-Output "Dados: $ProofRoot"
Write-Output "Log: $log"

if ($StopAfterSeconds -gt 0) {
    Start-Sleep -Seconds $StopAfterSeconds
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
        Stop-Process -Id $process.Id -Force
        Write-Output "Instancia encerrada apos $StopAfterSeconds segundos."
    }
}

if (Select-String -LiteralPath $log -Pattern 'Application Loaded: Shovel Knight: Treasure Trove' -Quiet) {
    Write-Output 'PROVA OK: Shovel Knight foi carregado pelo core.'
    exit 0
}

Write-Error "O core iniciou, mas o marcador de carregamento nao apareceu. Consulte $log e $errorLog"
exit 1
