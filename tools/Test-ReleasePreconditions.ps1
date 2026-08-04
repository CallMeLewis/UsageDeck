[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Tag,
    [string] $MainRef = 'origin/main',
    [string] $ReleaseCommit = 'HEAD',
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$buildPropsPath = Join-Path $resolvedRepositoryRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) {
    throw "Directory.Build.props was not found under '$resolvedRepositoryRoot'."
}

[xml] $buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$version = [string] $buildProps.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Directory.Build.props must contain a valid three-part SemVer <Version>; found '$version'."
}

$expectedTag = "v$version"
if ($Tag -cne $expectedTag) {
    throw "Tag '$Tag' does not match Directory.Build.props version '$version'. Expected '$expectedTag'."
}

$releaseNotesPath = Join-Path $resolvedRepositoryRoot ".github\release-notes\$Tag.md"
if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    throw "Release notes are required at '.github/release-notes/$Tag.md'."
}
if ((Get-Item -LiteralPath $releaseNotesPath).Length -eq 0) {
    throw "Release notes are required at '.github/release-notes/$Tag.md'."
}

function Resolve-GitCommit([string] $Reference, [string] $Description) {
    $commit = & git -C $resolvedRepositoryRoot rev-parse --verify "$Reference`^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw "Could not resolve $Description '$Reference' to a commit."
    }

    return ([string] $commit).Trim()
}

$releaseCommitHash = Resolve-GitCommit $ReleaseCommit 'release commit'
$mainCommitHash = Resolve-GitCommit $MainRef 'main reference'

& git -C $resolvedRepositoryRoot merge-base --is-ancestor $releaseCommitHash $mainCommitHash
$ancestorExitCode = $LASTEXITCODE
if ($ancestorExitCode -eq 1) {
    throw "Release commit '$releaseCommitHash' is not reachable from '$MainRef' ($mainCommitHash)."
}
if ($ancestorExitCode -ne 0) {
    throw "Could not verify whether release commit '$releaseCommitHash' is reachable from '$MainRef'. Git exited with code $ancestorExitCode."
}

Write-Host "Release preconditions passed for $Tag at $releaseCommitHash."
