[CmdletBinding()]
param(
    [string] $RepositoryUrl = $env:USAGEDECK_UPDATE_REPOSITORY_URL,
    [switch] $ResetReleaseHistory,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\UsageDeck.App\UsageDeck.App.csproj'
$solutionPath = Join-Path $repositoryRoot 'UsageDeck.slnx'
$buildPropsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts\velopack'
$publishDirectory = Join-Path $artifactsRoot 'publish'
$releasesDirectory = Join-Path $artifactsRoot 'releases'
$iconPath = Join-Path $repositoryRoot 'src\UsageDeck.App\Assets\AppIcon.ico'

[xml] $buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$version = [string] $buildProps.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Directory.Build.props must contain a valid three-part SemVer <Version>; found '$version'."
}

if (-not [string]::IsNullOrWhiteSpace($RepositoryUrl)) {
    $parsedRepositoryUrl = $null
    $isValidRepositoryUrl = [Uri]::TryCreate(
        $RepositoryUrl,
        [UriKind]::Absolute,
        [ref] $parsedRepositoryUrl)
    if (-not $isValidRepositoryUrl -or $parsedRepositoryUrl.Scheme -ne [Uri]::UriSchemeHttps) {
        throw 'RepositoryUrl must be an absolute HTTPS GitHub repository URL.'
    }

    $RepositoryUrl = $parsedRepositoryUrl.AbsoluteUri.TrimEnd('/')
}
else {
    $RepositoryUrl = ''
    Write-Warning 'No update repository was supplied. The package will run, but automatic update checks will be disabled.'
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releasesDirectory -Force | Out-Null

$resolvedArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot)
$resolvedPublishDirectory = [IO.Path]::GetFullPath($publishDirectory)
$resolvedReleasesDirectory = [IO.Path]::GetFullPath($releasesDirectory)
$expectedPrefix = $resolvedArtifactsRoot + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($resolvedPublishDirectory, $resolvedReleasesDirectory)) {
    if (-not $target.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside the Velopack artifacts directory: $target"
    }
}

if (Test-Path -LiteralPath $resolvedPublishDirectory) {
    Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
}

if ($ResetReleaseHistory -and (Test-Path -LiteralPath $resolvedReleasesDirectory)) {
    Write-Warning 'Resetting local Velopack release history. Previously generated delta packages will not be retained.'
    Remove-Item -LiteralPath $resolvedReleasesDirectory -Recurse -Force
    New-Item -ItemType Directory -Path $resolvedReleasesDirectory -Force | Out-Null
}

Push-Location $repositoryRoot
try {
    Write-Host 'Restoring the pinned Velopack tool...'
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "Local tool restore failed with exit code $LASTEXITCODE."
    }

    if (-not $SkipTests) {
        Write-Host 'Running Release tests...'
        & dotnet test $solutionPath `
            -c Release `
            -p:SkipReleaseArtifacts=true `
            -p:WindowsAppSdkBootstrapInitialize=false `
            --blame-hang-timeout 2m `
            --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Release tests failed with exit code $LASTEXITCODE."
        }
    }

    Write-Host 'Publishing the self-contained Windows application...'
    & dotnet publish $projectPath `
        -c Release `
        -p:PublishProfile=Portable-win-x64 `
        -p:SkipReleaseArtifacts=true `
        "-p:Version=$version" `
        "-p:UpdateRepositoryUrl=$RepositoryUrl" `
        "-p:PublishDir=$publishDirectory" `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Portable publish failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        'UsageDeck.App.exe',
        'UsageDeck.App.pri',
        'Microsoft.UI.Xaml.dll',
        'Assets\AppIcon.ico',
        'Assets\AppIcon.png'
    )
    $missingFiles = @(
        $requiredFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_) -PathType Leaf)
        }
    )
    if ($missingFiles.Count -gt 0) {
        throw "Portable publish is incomplete. Missing: $($missingFiles -join ', ')"
    }

    Write-Host 'Creating the Velopack release assets...'
    & dotnet tool run vpk -- pack `
        --packId UsageDeck `
        --packVersion $version `
        --packDir $publishDirectory `
        --mainExe UsageDeck.App.exe `
        --packTitle 'UsageDeck' `
        --icon $iconPath `
        --runtime win-x64 `
        --outputDir $releasesDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Velopack packaging failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host "Packaged UsageDeck $version"
Write-Host "Velopack releases: $releasesDirectory"
