[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BundlePath,

    [ValidateNotNullOrEmpty()]
    [string]$RemoteUrl = "https://github.com/Cainareiss/ryubox-uwp-private.git",

    [ValidateNotNullOrEmpty()]
    [string]$SourceBranch = "work",

    [ValidateNotNullOrEmpty()]
    [string]$DestinationBranch = "work",

    [switch]$Push
)

$ErrorActionPreference = "Stop"

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$GitArguments
    )

    & git @GitArguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-GitRef {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Reference
    )

    & git rev-parse --verify --quiet $Reference *> $null
    return $LASTEXITCODE -eq 0
}

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git was not found in PATH. Install Git and open a new PowerShell session."
}

Invoke-Git @("check-ref-format", "--branch", $SourceBranch)
Invoke-Git @("check-ref-format", "--branch", $DestinationBranch)

$bundleFullPath = Resolve-FullPath $BundlePath
Invoke-Git @("bundle", "verify", $bundleFullPath)

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ryubox-cloud-bundle-" + [System.Guid]::NewGuid().ToString("N"))

try {
    Invoke-Git @("clone", $bundleFullPath, $tempRoot)

    Push-Location $tempRoot
    try {
        Write-Host "Branches in bundle:"
        Invoke-Git @("branch", "--list", "--all")

        if (Test-GitRef "refs/heads/$SourceBranch") {
            Invoke-Git @("checkout", $SourceBranch)
        }
        elseif (Test-GitRef "refs/remotes/origin/$SourceBranch") {
            Invoke-Git @("checkout", "-B", $SourceBranch, "origin/$SourceBranch")
        }
        else {
            throw "Branch '$SourceBranch' was not found in the bundle."
        }

        Write-Host "Latest commits from bundle branch '$SourceBranch':"
        Invoke-Git @("log", "--oneline", "--decorate", "-10")

        Invoke-Git @("remote", "set-url", "origin", $RemoteUrl)

        Write-Host "Comparing with GitHub main when the remote is reachable:"
        & git fetch origin main --depth=1
        if ($LASTEXITCODE -eq 0) {
            Invoke-Git @("diff", "--stat", "origin/main...HEAD")
        }
        else {
            Write-Warning "Could not fetch origin/main. The bundle was imported, but the remote comparison was skipped."
        }

        if ($Push) {
            Write-Host "Pushing '$SourceBranch' to '$DestinationBranch' on $RemoteUrl"
            Invoke-Git @("push", "--set-upstream", "origin", "${SourceBranch}:${DestinationBranch}")
        }
        else {
            Write-Host "Dry run complete. Re-run with -Push to upload:"
            Write-Host "  pwsh ./eng/Import-CodexCloudBundle.ps1 -BundlePath '$bundleFullPath' -SourceBranch '$SourceBranch' -DestinationBranch '$DestinationBranch' -Push"
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
