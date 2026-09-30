$dir = Join-Path $env:LOCALAPPDATA 'Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\Logs\Ryujinx'
$latest = Get-ChildItem -Path $dir | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Latest log: $($latest.FullName)"
Get-Content -Path $latest.FullName | Select-String -Pattern 'Capture|present|Aquiris|SaveData' | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
