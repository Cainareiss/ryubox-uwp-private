param(
    [Parameter(Mandatory=$true)][int] $ProcessId,
    [Parameter(Mandatory=$true)][string] $OutputPath,
    [ValidateRange(10,1800)][int] $Seconds = 300
)
$ErrorActionPreference = 'Stop'
$target = Get-Process -Id $ProcessId
if ($target.MainWindowTitle -notlike 'Ryubox Direct D3D11 Host -*') {
    throw 'Not a Ryubox validation process.'
}
$samples = [Collections.Generic.List[object]]::new()
$start = [DateTime]::UtcNow
$previousCpu = $target.TotalProcessorTime.TotalSeconds
$previousTime = $start
for ($i = 0; $i -lt $Seconds; $i++) {
    $target = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $target) { break }
    $now = [DateTime]::UtcNow
    $cpu = $target.TotalProcessorTime.TotalSeconds
    $gpuCommitted = $null
    $gpuShared = $null
    if (($i % 5) -eq 0) {
        try {
            $gpu = @(Get-CimInstance -Namespace root/cimv2 -ClassName Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory |
                Where-Object Name -like "pid_${ProcessId}_*")
            if ($gpu.Count -gt 0) {
                $gpuCommitted = ($gpu | Measure-Object TotalCommitted -Sum).Sum
                $gpuShared = ($gpu | Measure-Object SharedUsage -Sum).Sum
            }
        } catch { } # unavailable counter remains blank, never reported as zero
    }
    $samples.Add([pscustomobject][ordered]@{
        Timestamp=$now.ToString('o')
        WorkingSetBytes=$target.WorkingSet64
        PrivateBytes=$target.PrivateMemorySize64
        CpuCoreEquivalent=([Math]::Round(($cpu-$previousCpu)/[Math]::Max(.001,($now-$previousTime).TotalSeconds),3))
        GpuCommittedBytes=$gpuCommitted
        GpuSharedBytes=$gpuShared
    })
    if (($i % 10) -eq 0) { $samples | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8 }
    $previousCpu=$cpu
    $previousTime=$now
    Start-Sleep -Seconds 1
}
$samples | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8
Write-Output "Samples=$($samples.Count) output=$OutputPath"
