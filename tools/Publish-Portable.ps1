[CmdletBinding()]
param(
    [string] $RepositoryUrl = $env:USAGEDECK_UPDATE_REPOSITORY_URL,
    [switch] $ResetReleaseHistory,
    [switch] $SkipTests
)

$releaseScript = Join-Path $PSScriptRoot 'Publish-Release.ps1'
& $releaseScript `
    -RepositoryUrl $RepositoryUrl `
    -ResetReleaseHistory:$ResetReleaseHistory `
    -SkipTests:$SkipTests
