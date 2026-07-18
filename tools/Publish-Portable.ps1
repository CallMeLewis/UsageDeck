[CmdletBinding()]
param(
    [string] $RepositoryUrl = $env:USAGEDECK_UPDATE_REPOSITORY_URL,
    [switch] $ResetReleaseHistory,
    [switch] $SkipTests
)

# Continue accepting the pre-rebrand variable for existing release environments.
if ([string]::IsNullOrWhiteSpace($RepositoryUrl)) {
    $RepositoryUrl = $env:CODEXBAR_UPDATE_REPOSITORY_URL
}

$releaseScript = Join-Path $PSScriptRoot 'Publish-Release.ps1'
& $releaseScript `
    -RepositoryUrl $RepositoryUrl `
    -ResetReleaseHistory:$ResetReleaseHistory `
    -SkipTests:$SkipTests
