param(
    [Parameter(Mandatory = $true)][string] $StateDirectory,
    [int] $LastSamples = 60
)
$ErrorActionPreference = 'Stop'
$logs = Join-Path $StateDirectory 'Logs\CoreHost'
$performance = Get-ChildItem -LiteralPath $logs -File -Filter '*performance*' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$performance) { throw 'No CoreHost performance log yet.' }
$samples = @(Get-Content -LiteralPath $performance.FullName | ForEach-Object {
    if ($_ -match '^\[(?<time>[^\]]+)\].*FPS backend=(?<backend>\w+) callbacks=(?<fps>[\d.,]+), processed=(?<processed>[\d.,]+), totalCallbacks=(?<total>\d+)') {
        $line = $Matches
        $acceptedFps = $null
        if ($_ -match 'presentAcceptedFps=(?<present>[\d.,]+)') {
            $acceptedFps = [double]::Parse($Matches.present.Replace(',','.'), [Globalization.CultureInfo]::InvariantCulture)
        }
        [pscustomobject]@{
            Timestamp = [DateTimeOffset]::Parse($line.time)
            Backend = $line.backend
            CallbackFps = [double]::Parse($line.fps.Replace(',','.'), [Globalization.CultureInfo]::InvariantCulture)
            PresentAcceptedFps = $acceptedFps
            TotalCallbacks = [long]$line.total
        }
    }
})
if (!$samples.Count) { throw 'No performance samples yet.' }
$window = @($samples | Select-Object -Last $LastSamples)
$span = ($window[-1].Timestamp - $window[0].Timestamp).TotalSeconds
[pscustomobject]@{
    Log = $performance.FullName
    Backend = $window[-1].Backend
    Samples = $window.Count
    Start = $window[0].Timestamp
    End = $window[-1].Timestamp
    CallbackFpsFromDelta = if ($span -gt 0) { [math]::Round(($window[-1].TotalCallbacks - $window[0].TotalCallbacks) / $span, 2) } else { $null }
    MinimumIntervalFps = ($window | Measure-Object CallbackFps -Minimum).Minimum
    MaximumIntervalFps = ($window | Measure-Object CallbackFps -Maximum).Maximum
    MinimumPresentAcceptedFps = if (@($window | Where-Object { $null -ne $_.PresentAcceptedFps }).Count) { ($window | Where-Object { $null -ne $_.PresentAcceptedFps } | Measure-Object PresentAcceptedFps -Minimum).Minimum } else { $null }
    MaximumPresentAcceptedFps = if (@($window | Where-Object { $null -ne $_.PresentAcceptedFps }).Count) { ($window | Where-Object { $null -ne $_.PresentAcceptedFps } | Measure-Object PresentAcceptedFps -Maximum).Maximum } else { $null }
    Note = 'CallbackFps is GAL callback rate. PresentAcceptedFps counts successful native DXGI Present results when available; it is not GPU timing or proof of visible, non-black pixels. Compare identical scenes and cache state.'
} | ConvertTo-Json
