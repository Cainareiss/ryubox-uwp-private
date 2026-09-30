param(
    [Parameter(Mandatory=$true)][string] $ArchivePath,
    [string] $BaselineArchivePath,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\structural-baseline')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$patterns = [ordered]@{
    JitHardLimit = 'JIT Cache hard limit reached'
    InvalidSubscript = 'invalid subscript'
    GlobalStorage = 'Failed to find storage buffer'
    InterruptRace = 'JitExecutionContext\.RequestInterrupt'
    UnauthorizedDrive = 'UnauthorizedAccessException|Access to the path .Q:'
    SharedMemory = 'MapSharedMemory.*InvalidMemoryRegion|InvalidMemoryRegion.*MapSharedMemory'
    GradientLoop = 'gradient instruction used in a loop|varying iteration'
    OutOfMemory = 'OutOfMemoryException|0x8007000E|paging file|MEM_COMMIT failed'
    DeviceRemoved = '0x887A0005|DXGI_ERROR_DEVICE_REMOVED|DEVICE_HUNG'
    PtcDisabled = 'enabled: False'
    MissingAotCode = 'no code (?:was )?generated|MissingRuntimeArtifactException'
    ContentIntegrity = '2002-4604|2002-4644'
    CoverCodec = '0x88982F50'
}
$baselineNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if ($BaselineArchivePath) {
    $baselineArchive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $BaselineArchivePath).Path)
    try { foreach ($entry in $baselineArchive.Entries) { [void]$baselineNames.Add($entry.FullName) } }
    finally { $baselineArchive.Dispose() }
}
$evidence = [Collections.Generic.List[object]]::new()
$sessions = [Collections.Generic.List[object]]::new()
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -notmatch '\.log$') { continue }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $families = [Collections.Generic.List[string]]::new()
        foreach ($pattern in $patterns.GetEnumerator()) {
            $hits = [regex]::Matches($body, $pattern.Value, 'IgnoreCase')
            if ($hits.Count -eq 0) { continue }
            $families.Add($pattern.Key)
            $start = $body.LastIndexOf("`n", $hits[0].Index)
            $end = $body.IndexOf("`n", $hits[0].Index)
            if ($end -lt 0) { $end = $body.Length }
            $evidence.Add([pscustomobject]@{
                File=$entry.FullName; Family=$pattern.Key; Occurrences=$hits.Count
                FirstLine=$body.Substring($start + 1, $end - $start - 1).Trim()
            })
        }
        if ($entry.FullName -notmatch '^Ryujinx/') { continue }
        $loaded = [regex]::Match($body, 'Application Loaded:\s*(?<name>[^\r\n]+?)\s*\[(?<id>[0-9a-fA-F]{16})\]')
        $title = [regex]::Match($body, 'title: (?<id>[0-9a-fA-F]{16})')
        $game = if ($loaded.Success) { $loaded.Groups['name'].Value.Trim() } else { 'Unknown' }
        $titleId = if ($loaded.Success) { $loaded.Groups['id'].Value } else { $title.Groups['id'].Value }
        $sessions.Add([pscustomobject][ordered]@{
            LogFile=$entry.FullName
            ArchiveCohort=$(if (-not $BaselineArchivePath) { 'Uncompared' } elseif ($baselineNames.Contains($entry.FullName)) { 'PreviouslyListed' } else { 'NewLogName' })
            Game=$game; TitleId=$titleId
            Boot=$(if ($loaded.Success) { 'ApplicationLoaded' } else { 'Unknown' })
            Menu='Unknown'; InGame='Unknown'; FPSAvg=''; P95=''; P99=''; PeakRAM=''; JitMB=''
            ShaderFailures=([regex]::Matches($body, 'error X\d{4}:').Count)
            Fatal=$(if ($body -match '\|E\|.*Unhandled|Unhandled exception|Fatal error') { 'Observed' } else { 'NotEstablished' })
            Notes=('Historical log only; not a new regression. File=' + $entry.FullName + '; families=' + ($families -join '|'))
        })
    }
} finally { $archive.Dispose() }
$evidence | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'error-evidence.csv') -NoTypeInformation -Encoding UTF8
$sessions | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'game-regression.csv') -NoTypeInformation -Encoding UTF8
$evidence | Group-Object Family | ForEach-Object {
    [pscustomobject]@{ Family=$_.Name; Files=$_.Count; Occurrences=($_.Group | Measure-Object Occurrences -Sum).Sum }
} | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'error-summary.csv') -NoTypeInformation -Encoding UTF8
Write-Output ("Historical HLE sessions: " + $sessions.Count)
Write-Output 'Menu/InGame/FPS left unknown unless independently validated; error counts may repeat across aggregate files.'
Write-Output ("Output: " + (Resolve-Path -LiteralPath $OutputDirectory).Path)
