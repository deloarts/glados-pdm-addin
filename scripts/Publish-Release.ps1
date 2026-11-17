<#
.SYNOPSIS
    Builds the tagged release locally, attaches GladosPdmAddin.dll and SHA256SUMS.txt to its
    draft GitHub Release and publishes it.

.DESCRIPTION
    The DLL cannot be built on a GitHub-hosted runner: it compiles against EPDM.Interop.epdm.dll
    from the SOLIDWORKS PDM client install, which is not ours to redistribute. So release.yml
    only tags vX.Y.Z and creates a draft release, and this script does the rest on a Windows
    machine with the PDM client and Visual Studio installed.

    It checks out the tag into a temporary git worktree (the current checkout is left alone),
    rebuilds Release there, checks that the DLL's file version is X.Y.Z.0, writes the checksums,
    uploads both files to the draft and publishes it. A release that is already published is
    refused, so a published DLL is never replaced. Needs the GitHub CLI (gh), signed in with
    write access to the repository. Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Version
    The version to publish, X.Y.Z. Its tag and draft release must exist.

.PARAMETER KeepDraft
    Uploads the files but leaves the release a draft, to inspect it on GitHub first.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $KeepDraft
)

$ErrorActionPreference = 'Stop'

function Invoke-Native {
    $command = $args[0]
    $rest = @($args | Select-Object -Skip 1)
    & $command @rest
    if ($LASTEXITCODE -ne 0) { throw "$command $($rest -join ' ') failed with exit code $LASTEXITCODE" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "'$Version' is not an X.Y.Z version" }
$tag = "v$Version"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'the GitHub CLI (gh) is not installed' }
Invoke-Native gh auth status

$interop = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS PDM\EPDM.Interop.epdm.dll'
if (-not (Test-Path $interop)) { throw "the SOLIDWORKS PDM client is not installed ($interop is missing)" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio is not installed (vswhere.exe is missing)' }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found' }

Invoke-Native git fetch --tags origin
git rev-parse --verify --quiet "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "tag $tag does not exist; merge the release pull request first" }

$release = gh release view $tag --json isDraft,assets | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $release) { throw "there is no GitHub Release for $tag; re-run release.yml" }
if (-not $release.isDraft) { throw "$tag is already published; cut the next patch instead of replacing its DLL" }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "glados-pdm-addin-$tag"
$dist = "$work-dist"
foreach ($dir in @($work, $dist)) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
git worktree prune

Invoke-Native git worktree add --detach $work $tag
try {
    Invoke-Native $msbuild (Join-Path $work 'GladosPdmAddin.csproj') /p:Configuration=Release /t:Rebuild /nr:false /nologo /v:m

    $dll = Join-Path $work 'bin\Release\GladosPdmAddin.dll'
    if (-not (Test-Path $dll)) { throw "the build produced no $dll" }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    if ($fileVersion -ne "$Version.0") { throw "the built DLL has file version $fileVersion, expected $Version.0" }

    New-Item -ItemType Directory -Path $dist | Out-Null
    Copy-Item $dll (Join-Path $dist 'GladosPdmAddin.dll')
    $hash = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $dist 'GladosPdmAddin.dll')).Hash.ToLower()
    Set-Content -Encoding ascii -Path (Join-Path $dist 'SHA256SUMS.txt') -Value "$hash  GladosPdmAddin.dll"

    Invoke-Native gh release upload $tag (Join-Path $dist 'GladosPdmAddin.dll') (Join-Path $dist 'SHA256SUMS.txt') --clobber
    if ($KeepDraft) {
        Write-Host "Uploaded to the draft $tag; publish it on GitHub or re-run without -KeepDraft."
    } else {
        Invoke-Native gh release edit $tag --draft=false
        Write-Host "Published $tag (GladosPdmAddin.dll sha256 $hash)."
    }
    Write-Host 'Upload this DLL in the PDM Administration tool: Add-ins, New Add-in.'
} finally {
    git worktree remove --force $work
    $global:LASTEXITCODE = 0
}
