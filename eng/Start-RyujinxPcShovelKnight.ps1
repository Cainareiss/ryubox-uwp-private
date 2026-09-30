param(
    [string] $GameDirectory = 'E:\switchkeys.io-v22.5.0',
    [string] $ProofRoot = (Join-Path $PSScriptRoot '..\artifacts\proof\shovel-knight'),
    [switch] $HeadlessGameWindow
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$core = Join-Path $repoRoot 'src\Ryujinx\bin\Release\net10.0\Ryujinx.exe'
$game = Get-ChildItem -LiteralPath $GameDirectory -File -Filter '*.nsp' |
    Where-Object { $_.Name -match 'Shovel Knight' } |
    Select-Object -First 1
$system = Join-Path $ProofRoot 'system'
$logDirectory = Join-Path (Split-Path -Parent $ProofRoot) 'pc'
$log = Join-Path $logDirectory 'ryujinx-pc.log'
$errorLog = Join-Path $logDirectory 'ryujinx-pc-error.log'

if (-not (Test-Path -LiteralPath $core)) { throw "Core nao compilado: $core" }
if (-not $game) { throw "NSP do Shovel Knight nao encontrado em $GameDirectory" }
foreach ($keyName in @('prod.keys', 'title.keys')) {
    $sourceKey = Join-Path $GameDirectory $keyName
    if (-not (Test-Path -LiteralPath $sourceKey)) { throw "Chave ausente: $sourceKey" }
}

New-Item -ItemType Directory -Force -Path $system, $logDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $GameDirectory 'prod.keys') -Destination (Join-Path $system 'prod.keys') -Force
Copy-Item -LiteralPath (Join-Path $GameDirectory 'title.keys') -Destination (Join-Path $system 'title.keys') -Force

$modeArguments = if ($HeadlessGameWindow) {
    '--no-gui --memory-manager-mode SoftwarePageTable --disable-shader-cache --disable-file-logging '
} else {
    ''
}
$argumentLine = $modeArguments + '--root-data-dir "' + (Resolve-Path -LiteralPath $ProofRoot).Path + '" "' + $game.FullName + '"'
$process = Start-Process -FilePath $core -ArgumentList $argumentLine -WorkingDirectory (Split-Path -Parent $core) -RedirectStandardOutput $log -RedirectStandardError $errorLog -WindowStyle Normal -PassThru

Write-Output "Ryujinx iniciado no PC. PID: $($process.Id)"
Write-Output "Jogo: $($game.FullName)"
Write-Output "Dados isolados: $ProofRoot"
Write-Output "Log: $log"
