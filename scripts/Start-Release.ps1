<#
.SYNOPSIS
    Starts a release: branches release/X.Y.Z from an up-to-date main, bumps the version, opens
    the CHANGELOG section, commits and pushes.

.DESCRIPTION
    Sets AssemblyVersion and AssemblyFileVersion in Properties/AssemblyInfo.cs to X.Y.Z.0 and
    AssemblyInformationalVersion to X.Y.Z, raises
    mlAddInVersion in AddIn.cs to one above the previous release tag's value when it is not
    already higher, and turns the [Unreleased] section of CHANGELOG.md into [X.Y.Z] - YYYY-MM-DD
    under a fresh empty [Unreleased] heading. Merging the release pull request into main tags the
    release and creates its draft (release.yml); scripts/Publish-Release.ps1 then builds and
    attaches the DLL. Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Version
    The version to release, X.Y.Z.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

# Rewrites a file through a transform while keeping its encoding (BOM or not) and line endings,
# which Visual Studio and .gitattributes both care about.
function Update-File([string] $Path, [scriptblock] $Transform) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($Path)
    $updated = & $Transform $text
    [System.IO.File]::WriteAllText($Path, $updated, (New-Object System.Text.UTF8Encoding($hasBom)))
}

function Get-AddInVersion([string] $Source) {
    $match = [regex]::Match($Source, 'poInfo\.mlAddInVersion\s*=\s*(\d+)\s*;')
    if (-not $match.Success) { return $null }
    return [int] $match.Groups[1].Value
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "'$Version' is not an X.Y.Z version" }
$branch = "release/$Version"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (git status --porcelain) { throw 'working tree is not clean' }

Invoke-Git checkout main
Invoke-Git pull --ff-only
Invoke-Git fetch --tags origin

git show-ref --verify --quiet "refs/heads/$branch"
$localExists = $LASTEXITCODE -eq 0
# --exit-code: 0 when the ref exists, 2 when it does not, anything else when origin was not reached.
git ls-remote --exit-code --heads origin $branch | Out-Null
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 2) { throw "could not ask origin for $branch" }
$remoteExists = $LASTEXITCODE -eq 0
if ($localExists -or $remoteExists) { throw "branch $branch already exists" }
git ls-remote --exit-code --tags origin "refs/tags/v$Version" | Out-Null
if ($LASTEXITCODE -eq 0) { throw "tag v$Version already exists" }
if ($LASTEXITCODE -ne 2) { throw "could not ask origin for tag v$Version" }

Invoke-Git checkout -b $branch

Update-File (Join-Path $root 'Properties/AssemblyInfo.cs') {
    param($text)
    $text = $text -replace '\[assembly: AssemblyVersion\("[^"]+"\)\]', "[assembly: AssemblyVersion(""$Version.0"")]"
    $text = $text -replace '\[assembly: AssemblyFileVersion\("[^"]+"\)\]', "[assembly: AssemblyFileVersion(""$Version.0"")]"
    $text -replace '\[assembly: AssemblyInformationalVersion\("[^"]+"\)\]', "[assembly: AssemblyInformationalVersion(""$Version"")]"
}

$addInPath = Join-Path $root 'AddIn.cs'
$addInVersion = Get-AddInVersion ([System.IO.File]::ReadAllText($addInPath))
if ($null -eq $addInVersion) { throw 'AddIn.cs sets no mlAddInVersion' }

$target = [version] $Version
$previous = git tag --list 'v*' |
    Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
    ForEach-Object { [pscustomobject] @{ Tag = $_; Version = [version] $_.Substring(1) } } |
    Where-Object { $_.Version -lt $target } |
    Sort-Object Version -Descending |
    Select-Object -First 1
$global:LASTEXITCODE = 0

if ($previous) {
    $previousAddInVersion = Get-AddInVersion (git show "$($previous.Tag):AddIn.cs" | Out-String)
    if ($null -ne $previousAddInVersion -and $addInVersion -le $previousAddInVersion) {
        $next = $previousAddInVersion + 1
        Update-File $addInPath {
            param($text)
            $text -replace 'poInfo\.mlAddInVersion\s*=\s*\d+\s*;', "poInfo.mlAddInVersion = $next;"
        }
        Write-Host "mlAddInVersion raised from $addInVersion to $next ($($previous.Tag) had $previousAddInVersion)."
    }
}

$changelogPath = Join-Path $root 'CHANGELOG.md'
$changelog = [System.IO.File]::ReadAllText($changelogPath)
if ($changelog -notmatch "(?m)^## \[$([regex]::Escape($Version))\]") {
    $newline = if ($changelog.Contains("`r`n")) { "`r`n" } else { "`n" }
    $date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    Update-File $changelogPath {
        param($text)
        ([regex] '(?m)^## \[Unreleased\](?=\r?$)').Replace($text, "## [Unreleased]$newline$newline## [$Version] - $date", 1)
    }
}

Invoke-Git add Properties/AssemblyInfo.cs AddIn.cs CHANGELOG.md
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    Invoke-Git commit -m "release: $Version"
} else {
    Write-Host "Nothing to commit, main already carries $Version and its CHANGELOG section."
}

& (Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'release check failed' }

Invoke-Git push -u origin $branch

Write-Host ''
Write-Host "Next: build $branch locally and test it in the vault (Debug Add-ins), review CHANGELOG.md"
Write-Host "(state the supported backend versions), then open the PR into main:"
Write-Host "  gh pr create --base main --head $branch --title ""release: $Version"""
Write-Host "Merging it tags v$Version and creates a draft release; then run:"
Write-Host "  scripts\Publish-Release.ps1 $Version"
