[CmdletBinding()]
param(
    [string] $RepositoryUrl = $env:USAGEDECK_UPDATE_REPOSITORY_URL,
    [switch] $ResetReleaseHistory,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\UsageDeck.App\UsageDeck.App.csproj'
$bootstrapProjectPath = Join-Path $repositoryRoot 'src\UsageDeck.Bootstrap\UsageDeck.Bootstrap.csproj'
$solutionPath = Join-Path $repositoryRoot 'UsageDeck.slnx'
$buildPropsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts\velopack'
$publishDirectory = Join-Path $artifactsRoot 'publish'
$releasesDirectory = Join-Path $artifactsRoot 'releases'
$appReleaseIntermediateDirectory = Join-Path $repositoryRoot 'src\UsageDeck.App\obj\Release'
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
$resolvedAppReleaseIntermediateDirectory = [IO.Path]::GetFullPath($appReleaseIntermediateDirectory)
$expectedPrefix = $resolvedArtifactsRoot + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($resolvedPublishDirectory, $resolvedReleasesDirectory)) {
    if (-not $target.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside the Velopack artifacts directory: $target"
    }
}

$resolvedAppProjectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $projectPath))
$expectedAppIntermediatePrefix = Join-Path $resolvedAppProjectDirectory 'obj'
$expectedAppIntermediatePrefix += [IO.Path]::DirectorySeparatorChar
if (-not $resolvedAppReleaseIntermediateDirectory.StartsWith(
    $expectedAppIntermediatePrefix,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a path outside the application intermediate directory: $resolvedAppReleaseIntermediateDirectory"
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

    # Remove files left by earlier project names before compiling the Release test target.
    if (Test-Path -LiteralPath $resolvedAppReleaseIntermediateDirectory) {
        Remove-Item -LiteralPath $resolvedAppReleaseIntermediateDirectory -Recurse -Force
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

    # WinUI's incremental XAML and PRI generation can retain files that are unknown to
    # `dotnet clean`, including artefacts from a renamed project. Remove the complete Release
    # intermediate directory so stale type metadata cannot enter the packaged application.
    Write-Host 'Clearing WinUI Release intermediates before publishing...'
    if (Test-Path -LiteralPath $resolvedAppReleaseIntermediateDirectory) {
        Remove-Item -LiteralPath $resolvedAppReleaseIntermediateDirectory -Recurse -Force
    }

    & dotnet clean $bootstrapProjectPath `
        -c Release `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Bootstrap clean failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Publishing UsageDeck with a self-contained .NET runtime...'
    & dotnet publish $projectPath `
        -c Release `
        -p:PublishProfile=Portable-win-x64 `
        -p:SkipReleaseArtifacts=true `
        "-p:Version=$version" `
        "-p:UpdateRepositoryUrl=$RepositoryUrl" `
        "-p:PublishDir=$publishDirectory" `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Application publish failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Publishing the Windows App SDK bootstrapper and offline runtime packages...'
    & dotnet publish $bootstrapProjectPath `
        -c Release `
        -p:SelfContained=true `
        -p:PublishReadyToRun=false `
        "-p:Version=$version" `
        "-p:PublishDir=$publishDirectory" `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Bootstrap publish failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Removing development symbols from the release payload...'
    Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.pdb' |
        Remove-Item -Force
    if (Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.pdb') {
        throw 'Release publish still contains development symbol files.'
    }

    $requiredFiles = @(
        'UsageDeck.Bootstrap.exe',
        'UsageDeck.App.exe',
        'UsageDeck.App.pri',
        'Microsoft.WindowsAppRuntime.Bootstrap.dll',
        'Assets\AppIcon.ico',
        'Assets\AppIcon.png',
        'WindowsAppRuntime\Microsoft.WindowsAppRuntime.2.msix',
        'WindowsAppRuntime\Microsoft.WindowsAppRuntime.Main.2.msix',
        'WindowsAppRuntime\Microsoft.WindowsAppRuntime.Singleton.2.msix',
        'WindowsAppRuntime\Microsoft.WindowsAppRuntime.DDLM.2.msix'
    )
    $missingFiles = @(
        $requiredFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_) -PathType Leaf)
        }
    )
    if ($missingFiles.Count -gt 0) {
        throw "Release publish is incomplete. Missing: $($missingFiles -join ', ')"
    }

    $appPri = Get-Item -LiteralPath (Join-Path $publishDirectory 'UsageDeck.App.pri')
    if ($appPri.Length -le 1024) {
        throw 'Release publish has an incomplete application PRI.'
    }

    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'coreclr.dll') -PathType Leaf)) {
        throw 'Release publish is missing the self-contained .NET runtime.'
    }

    if (Test-Path -LiteralPath (Join-Path $publishDirectory 'Microsoft.UI.Xaml.dll') -PathType Leaf) {
        throw 'Release publish unexpectedly contains the self-contained Windows App SDK runtime.'
    }

    $runtimePackageBytes = @(
        Get-ChildItem -LiteralPath (Join-Path $publishDirectory 'WindowsAppRuntime') -Filter '*.msix' -File |
            Measure-Object -Property Length -Sum |
            Select-Object -ExpandProperty Sum
    )[0]
    if ($runtimePackageBytes -le 0 -or $runtimePackageBytes -gt 50MB) {
        throw "The bundled x64 Windows App SDK packages have an unexpected total size: $runtimePackageBytes bytes."
    }

    Write-Host 'Validating the bootstrapper and bundled runtime package identities...'
    & (Join-Path $publishDirectory 'UsageDeck.Bootstrap.exe') --validate-runtime-packages
    if ($LASTEXITCODE -ne 0) {
        throw "Bootstrapper validation failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Creating the Velopack release assets...'
    & dotnet tool run vpk -- pack `
        --packId UsageDeck `
        --packVersion $version `
        --packDir $publishDirectory `
        --mainExe UsageDeck.Bootstrap.exe `
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
