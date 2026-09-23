param(
    [ValidateSet('Restore', 'Build')]
    [string] $Action = 'Restore',
    [string] $CacheRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) '..\.cache')
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$nugetPackages = Join-Path $CacheRoot 'nuget\packages'
$nugetHttp = Join-Path $CacheRoot 'nuget\http'
$nugetPlugins = Join-Path $CacheRoot 'nuget\plugins'
$tempRoot = Join-Path $CacheRoot 'temp'

New-Item -ItemType Directory -Force -Path $nugetPackages, $nugetHttp, $nugetPlugins, $tempRoot | Out-Null

$env:NUGET_PACKAGES = $nugetPackages
$env:NUGET_HTTP_CACHE_PATH = $nugetHttp
$env:NUGET_PLUGINS_CACHE_PATH = $nugetPlugins
$env:TEMP = $tempRoot
$env:TMP = $tempRoot

Push-Location $repoRoot
try
{
    if ($Action -eq 'Restore')
    {
        dotnet restore Ryujinx.sln --packages $nugetPackages --nologo
    }
    else
    {
        dotnet build src\Ryujinx\Ryujinx.csproj -c Release --no-restore --nologo
    }

    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}
finally
{
    Pop-Location
}
