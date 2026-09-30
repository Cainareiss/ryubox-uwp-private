param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\logs6-audit-20260909'))
$ErrorActionPreference='Stop'
$culture=[Globalization.CultureInfo]::InvariantCulture
$sessions=@(foreach($line in Get-Content -LiteralPath "$Directory\Frontend\session.log" -Encoding UTF8) {
    if($line -match '^\[(?<time>[^\]]+)\].*CoreHost_Start game=''(?<name>.+)'', brokeredHandle=') {
        $name=$Matches.name; $start=[datetime]::Parse($Matches.time,$culture).ToUniversalTime()
        $id=if($name -match '\[(?<id>[0-9A-Fa-f]{16})\]'){$Matches.id.ToUpper()}else{'unknown'}
        [pscustomobject]@{Start=$start;End=[datetime]::MaxValue;Title=$id;Name=$name;Fps=[Collections.Generic.List[double]]::new();Last=$null;First=$null;ShaderCompiles=0;ShaderHits=0;ShaderMs=0.0;Evictions=0L;JitBytes=0L;MaxPrivate=0L;MaxHazardBytes=0L;Waits=0;LastWatchdog=''}
    }
})
for($i=0;$i -lt $sessions.Count-1;$i++){$sessions[$i].End=$sessions[$i+1].Start}
foreach($file in @('performance','d3d11-shader','memory','d3d11-present-path','watchdog')){
    $index=0
    foreach($line in Get-Content -LiteralPath "$Directory\CoreHost\$file.log" -Encoding UTF8){
        if($line -notmatch '^\[(?<time>[^\]]+)\].*TitleId=(?<title>[A-Fa-f0-9]{16});'){continue}
        $title=$Matches.title; $time=[datetime]::Parse($Matches.time,$culture).ToUniversalTime()
        while($index -lt $sessions.Count-1 -and $time -ge $sessions[$index].End){$index++}
        $s=$sessions[$index]; if($time -lt $s.Start){continue}
        # Some filenames have no title ID (Sonic Mania, Top Racer). Learn the
        # resolved title from CoreHost in this start interval, not the filename.
        if($s.Title -eq 'unknown'){$s.Title=$title.ToUpper()}
        if($title -ne $s.Title){continue}
        switch($file){
            'performance'{
                if($line -match 'FPS .*callbacks=(?<fps>\d+(?:[.,]\d+)?), processed=.*totalCallbacks=(?<total>\d+)'){
                    if([long]$Matches.total -gt 0){
                        $s.Fps.Add([double]::Parse($Matches.fps.Replace(',','.'),$culture)); if($null -eq $s.First){$s.First=$time}
                    }
                    $s.Last=[string]$line
                }
            }
            'd3d11-shader'{
                if($line -match 'HLSL compile end:.*elapsed=(\d+) ms'){$s.ShaderCompiles++;$s.ShaderMs+=[int]$Matches[1]}
                if($line -match 'HLSL (disk )?cache hit:'){$s.ShaderHits++}
            }
            'memory'{
                if($line -match 'evictionCount=(\d+)'){$s.Evictions=[Math]::Max($s.Evictions,[long]$Matches[1])}
                if($line -match 'jitUsed=(\d+)'){$s.JitBytes=[Math]::Max($s.JitBytes,[long]$Matches[1])}
                if($line -match 'privateCommit=(\d+)'){$s.MaxPrivate=[Math]::Max($s.MaxPrivate,[long]$Matches[1])}
            }
            'd3d11-present-path'{if($line -match 'hazardBytes=(\d+)'){$s.MaxHazardBytes=[Math]::Max($s.MaxHazardBytes,[long]$Matches[1])}}
            'watchdog'{if($line -match 'Startup state:'){$s.LastWatchdog=[string]$line}}
        }
    }
}
$rows=@(foreach($s in $sessions){
    $fps=@($s.Fps | Sort-Object)
    $median=if($fps.Count){
        $lower=[int][Math]::Floor(($fps.Count-1)/2)
        $upper=[int][Math]::Floor($fps.Count/2)
        ($fps[$lower]+$fps[$upper])/2
    }else{0}
    $lastFps=@($s.Fps | Select-Object -Last 30)
    [pscustomobject]@{
        UTC=$s.Start.ToString('HH:mm:ss');Title=$s.Title;Name=[IO.Path]::GetFileName($s.Name)
        FirstFrameSeconds=if($null-ne $s.First){[Math]::Round(($s.First-$s.Start).TotalSeconds,1)}else{$null}
        Samples=$fps.Count;MedianFps=$median;Last30Mean=if($lastFps.Count){[Math]::Round(($lastFps|Measure-Object -Average).Average,2)}else{0}
        MaximumFps=if($fps.Count){$fps[-1]}else{0};ZeroFpsSamples=@($fps|Where-Object {$_ -eq 0}).Count
        ShaderCompiles=$s.ShaderCompiles;ShaderHits=$s.ShaderHits;CompileSeconds=[Math]::Round($s.ShaderMs/1000,2)
        Evictions=$s.Evictions;MaxJitMiB=[Math]::Round($s.JitBytes/1MB,1);MaxPrivateMiB=[Math]::Round($s.MaxPrivate/1MB,1)
        LastPerformance=$s.Last;LastWatchdog=$s.LastWatchdog
    }
})
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'audit-summary.json') -Encoding UTF8
$rows | Select-Object UTC,Title,FirstFrameSeconds,Samples,MedianFps,Last30Mean,MaximumFps,ZeroFpsSamples,ShaderCompiles,ShaderHits,CompileSeconds,Evictions,MaxJitMiB | Format-Table -AutoSize
