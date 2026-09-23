param(
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,

    [string]$RemoteUrl = "https://github.com/Cainareiss/ryubox-uwp-private.git",

    [string]$SourceBranch = "work",

    [string]$DestinationBranch = "work",

    [switch]$Push
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path) {
    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    return $resolved.ProviderPath
}

$bundleFullPath = Resolve-FullPath $BundlePath

git bundle verify $bundleFullPath | Write-Host

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ryubox-cloud-bundle-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    git clone $bundleFullPath $tempRoot

    Push-Location $tempRoot
    try {
        $branches = git branch --list --all
        Write-Host "Branches in bundle:"
        $branches | Write-Host

        $branchExists = git rev-parse --verify --quiet "refs/heads/$SourceBranch"
        if (-not $branchExists) {
            $remoteBranchExists = git rev-parse --verify --quiet "refs/remotes/origin/$SourceBranch"
            if ($remoteBranchExists) {
                git checkout -B $SourceBranch "origin/$SourceBranch"
            } else {
                throw "Branch '$SourceBranch' was not found in the bundle."
            }
        } else {
            git checkout $SourceBranch
        }

        Write-Host "Latest commits from bundle branch '$SourceBranch':"
        git log --oneline --decorate -10 | Write-Host

        Write-Host "Changed files compared with GitHub main, if the remote is reachable:"
        git remote remove origin 2>$null
        git remote add origin $RemoteUrl
        git fetch origin main --depth=1
        git diff --stat "origin/main...HEAD" | Write-Host

        if ($Push) {
            Write-Host "Pushing '$SourceBranch' to '$DestinationBranch' on $RemoteUrl"
            git push --set-upstream origin "$SourceBranch`:$DestinationBranch"
        } else {
            Write-Host "Dry run complete. Re-run with -Push to upload:"
            Write-Host "  pwsh ./eng/Import-CodexCloudBundle.ps1 -BundlePath '$bundleFullPath' -SourceBranch '$SourceBranch' -DestinationBranch '$DestinationBranch' -Push"
        }
    } finally {
        Pop-Location
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
