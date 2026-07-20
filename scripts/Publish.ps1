param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $CleanKnownOutputs
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$packageName = "ApexLab-$Version-win-x64"
$publishDirectory = Join-Path $artifactRoot ("publish\" + $packageName)
$packagePath = Join-Path $artifactRoot ($packageName + ".zip")
$checksumPath = $packagePath + ".sha256"
$projectPath = "src/ApexLab.App/ApexLab.App.csproj"

function Assert-PathBelowArtifacts {
    param([Parameter(Mandatory)][string] $Path)

    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (!$resolvedPath.StartsWith(
        $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output escaped the repository artifacts directory: $resolvedPath"
    }
}

function Assert-PathIgnored {
    param([Parameter(Mandatory)][string] $Path)

    Assert-PathBelowArtifacts -Path $Path
    $resolvedRepository = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $relativePath = $resolvedPath.Substring($resolvedRepository.Length + 1)
    $currentPath = $resolvedRepository
    foreach ($segment in $relativePath.Split(
        [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        if ([IO.Directory]::Exists($currentPath) -or [IO.File]::Exists($currentPath)) {
            $item = Get-Item -Force -LiteralPath $currentPath
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release cleanup path contains a filesystem reparse point: $currentPath"
            }
        }
    }

    $trackedEntries = @(& git -C $repositoryRoot ls-files -- $relativePath)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the Git tracking state of release cleanup target: $resolvedPath"
    }
    if ($trackedEntries.Count -ne 0) {
        throw "Release cleanup target contains tracked repository content: $resolvedPath"
    }

    & git -C $repositoryRoot check-ignore --quiet -- $relativePath
    if ($LASTEXITCODE -ne 0) {
        throw "Release cleanup target is not ignored by Git: $resolvedPath"
    }
}

function Remove-KnownOutput {
    param([Parameter(Mandatory)][string] $Path)

    Assert-PathIgnored -Path $Path
    if ([IO.Directory]::Exists($Path)) {
        [IO.Directory]::Delete($Path, $true)
    }
    elseif ([IO.File]::Exists($Path)) {
        [IO.File]::Delete($Path)
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    [xml] $versionDocument = Get-Content -Raw -LiteralPath "Version.props"
    $versionPrefix = [string] $versionDocument.Project.PropertyGroup.VersionPrefix
    if ($Version -ne $versionPrefix) {
        throw "Requested version '$Version' must match VersionPrefix '$versionPrefix'."
    }

    foreach ($output in @($publishDirectory, $packagePath, $checksumPath)) {
        Assert-PathBelowArtifacts -Path $output
    }

    if ($CleanKnownOutputs) {
        foreach ($output in @($publishDirectory, $packagePath, $checksumPath)) {
            Remove-KnownOutput -Path $output
        }
    }

    foreach ($output in @($publishDirectory, $packagePath, $checksumPath)) {
        if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) {
            throw "Release output already exists and will not be overwritten: $output"
        }
    }

    [IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
    Invoke-DotNet -Arguments @(
        "restore", $projectPath, "-r", "win-x64", "--locked-mode")
    Invoke-DotNet -Arguments @(
        "publish", $projectPath,
        "-c", "Release", "-r", "win-x64", "--self-contained", "true", "--no-restore",
        "--output", $publishDirectory,
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version",
        "-p:IncludeSourceRevisionInInformationalVersion=false")

    [IO.File]::Copy((Join-Path $repositoryRoot "README.md"), (Join-Path $publishDirectory "README.md"))
    [IO.File]::Copy(
        (Join-Path $repositoryRoot "THIRD_PARTY_NOTICES.md"),
        (Join-Path $publishDirectory "THIRD_PARTY_NOTICES.md"))

    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $packagePath
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$hash  $([IO.Path]::GetFileName($packagePath))`n",
        [Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        Version = $Version
        PublishDirectory = $publishDirectory
        PackagePath = $packagePath
        ChecksumPath = $checksumPath
        Sha256 = $hash
    }
}
catch {
    $primaryError = $_
    try {
        if (![IO.File]::Exists($packagePath) -and ![IO.File]::Exists($checksumPath)) {
            Remove-KnownOutput -Path $publishDirectory
        }
    }
    catch {
        throw [AggregateException]::new(
            "Release creation and cleanup both failed.",
            [Exception[]]@($primaryError.Exception, $_.Exception))
    }
    throw $primaryError
}
finally {
    Pop-Location
}
