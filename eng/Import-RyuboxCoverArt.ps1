param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDirectory,
    [string] $DestinationDirectory = (Join-Path $PSScriptRoot '..\src\Ryujinx.Uwp.App\Assets\CoverArt')
)

$source = Get-Item -LiteralPath $SourceDirectory -ErrorAction Stop
if (-not $source.PSIsContainer) {
    throw "A origem das capas nao e uma pasta: $SourceDirectory"
}

$destination = New-Item -ItemType Directory -Force -Path $DestinationDirectory
$sourceFiles = Get-ChildItem -LiteralPath $source.FullName -File -Filter '*.png' |
    Sort-Object Name

if ($sourceFiles.Count -eq 0) {
    throw "Nenhuma capa PNG encontrada em: $SourceDirectory"
}

$indexLines = [System.Collections.Generic.List[string]]::new()
$indexLines.Add("# Ryubox embedded cover art index v1")
$indexLines.Add("# packaged-file`tcover-title")

$position = 0
foreach ($file in $sourceFiles) {
    $position++
    $packageName = ('{0:D6}.png' -f $position)
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination.FullName $packageName) -Force
    $indexLines.Add($packageName + "`t" + $file.BaseName)
}

$indexPath = Join-Path $destination.FullName 'index.tsv'
[System.IO.File]::WriteAllLines(
    $indexPath,
    $indexLines,
    [System.Text.UTF8Encoding]::new($false))

$totalBytes = ($sourceFiles | Measure-Object Length -Sum).Sum
Write-Host ("Cover art importado: {0} arquivo(s), {1:N2} MiB" -f $sourceFiles.Count, ($totalBytes / 1MB))
Write-Host "Destino: $($destination.FullName)"
Write-Host "Indice: $indexPath"
