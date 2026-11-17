<#
.SYNOPSIS
    Fails unless the checked-out tree is ready to be released as the given version.

.DESCRIPTION
    Checks that AssemblyVersion and AssemblyFileVersion in Properties/AssemblyInfo.cs are X.Y.Z.0
    and AssemblyInformationalVersion is X.Y.Z, that CHANGELOG.md has a section for the version, that the tag vX.Y.Z does not exist on origin
    yet, and that mlAddInVersion in AddIn.cs is higher than in the previous release tag. The PDM
    Administration tool shows mlAddInVersion next to the uploaded add-in, so it has to move with
    every upload. ci.yml's release-check job, release.yml's guard and scripts/Start-Release.ps1
    all run this script, so they agree on what releasable means. It reads local tags for the
    previous release and never changes git state.

.PARAMETER Version
    The version to check, X.Y.Z.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Host "::error::$Message"
    exit 1
}

function Get-AddInVersion([string] $Source) {
    $match = [regex]::Match($Source, 'poInfo\.mlAddInVersion\s*=\s*(\d+)\s*;')
    if (-not $match.Success) { return $null }
    return [int] $match.Groups[1].Value
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "'$Version' is not an X.Y.Z version" }

$root = Split-Path -Parent $PSScriptRoot
$assemblyInfo = Get-Content -Raw (Join-Path $root 'Properties/AssemblyInfo.cs')

$expected = [ordered] @{
    AssemblyVersion              = "$Version.0"
    AssemblyFileVersion          = "$Version.0"
    AssemblyInformationalVersion = $Version
}
foreach ($attribute in $expected.Keys) {
    $match = [regex]::Match($assemblyInfo, "\[assembly: $attribute\(""([^""]+)""\)\]")
    if (-not $match.Success) { Fail "AssemblyInfo.cs has no $attribute attribute" }
    if ($match.Groups[1].Value -ne $expected[$attribute]) {
        Fail "AssemblyInfo.cs has $attribute $($match.Groups[1].Value), expected $($expected[$attribute])"
    }
}

$changelog = Get-Content -Raw (Join-Path $root 'CHANGELOG.md')
if ($changelog -notmatch "(?m)^## \[$([regex]::Escape($Version))\]") {
    Fail "CHANGELOG.md has no '## [$Version]' section"
}

# --exit-code: 0 when the tag exists, 2 when it does not, anything else when origin was not reached.
git -C $root ls-remote --exit-code --tags origin "refs/tags/v$Version" | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "tag v$Version already exists on origin" }
if ($LASTEXITCODE -ne 2) { Fail "could not ask origin for tag v$Version (git exit code $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

$addInVersion = Get-AddInVersion (Get-Content -Raw (Join-Path $root 'AddIn.cs'))
if ($null -eq $addInVersion) { Fail 'AddIn.cs sets no mlAddInVersion' }

# The highest release tag below this version, from the local tags.
$target = [version] $Version
$previous = git -C $root tag --list 'v*' |
    Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
    ForEach-Object { [pscustomobject] @{ Tag = $_; Version = [version] $_.Substring(1) } } |
    Where-Object { $_.Version -lt $target } |
    Sort-Object Version -Descending |
    Select-Object -First 1
$global:LASTEXITCODE = 0

if ($previous) {
    $previousSource = git -C $root show "$($previous.Tag):AddIn.cs" | Out-String
    $previousAddInVersion = Get-AddInVersion $previousSource
    if ($null -ne $previousAddInVersion -and $addInVersion -le $previousAddInVersion) {
        Fail "AddIn.cs has mlAddInVersion $addInVersion, but $($previous.Tag) already had $previousAddInVersion"
    }
}

Write-Host "Ready to release $Version (mlAddInVersion $addInVersion)."
