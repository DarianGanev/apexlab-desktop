$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$expectedSdkVersion = "10.0.302"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$testResultsDirectory = Join-Path $artifactRoot "test-results"
$maximumFixtureBytes = 1MB

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-TrackedFiles {
    param([Parameter(Mandatory)][string] $Root)

    $files = @(& git -C $Root ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed."
    }

    return $files
}

function Get-TextLines {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes -contains 0) {
        return $null
    }

    $lines = [IO.File]::ReadAllLines($Path)
    return ,$lines
}

function Assert-NoConflictMarkers {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string[]] $RelativePaths)

    $markerPattern = "^(<{7}|={7}|>{7})"
    foreach ($relativePath in $RelativePaths) {
        $path = Join-Path $Root $relativePath
        $lines = Get-TextLines -Path $path
        if ($null -eq $lines) { continue }
        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match $markerPattern) {
                throw "Conflict marker found in $relativePath at line $($index + 1)."
            }
        }
    }
}

function Assert-NoSecretPatterns {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string[]] $RelativePaths)

    $patterns = @(
        ("AK" + "IA[0-9A-Z]{16}"),
        ("gh" + "[pousr]_[A-Za-z0-9]{36,}"),
        ("-----BEGIN " + "(RSA |EC |OPENSSH )?PRIVATE KEY-----")
    )
    foreach ($relativePath in $RelativePaths) {
        $path = Join-Path $Root $relativePath
        $lines = Get-TextLines -Path $path
        if ($null -eq $lines) { continue }
        foreach ($pattern in $patterns) {
            if (($lines -join "`n") -match $pattern) {
                throw "Potential secret pattern found in $relativePath."
            }
        }
    }
}

function Assert-NoPersonalDataDirectories {
    param([Parameter(Mandatory)][string[]] $RelativePaths)

    $forbiddenDirectories = @(
        "data", "captures", "sessions", "telemetry", "exports", "backups", "imports",
        "profiles", "private", "vendor-specs")
    foreach ($relativePath in $RelativePaths) {
        $segments = $relativePath -split "[/\\]"
        if ($segments.Count -le 1) { continue }
        foreach ($segment in $segments[0..($segments.Count - 2)]) {
            if ($forbiddenDirectories -contains $segment.ToLowerInvariant()) {
                throw "Personal-data directory is tracked: $relativePath."
            }
        }
    }
}

function Assert-FixtureSizeBudget {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string[]] $RelativePaths,
        [Parameter(Mandatory)][long] $MaximumBytes)

    foreach ($relativePath in $RelativePaths) {
        if ($relativePath -notmatch "(^|[/\\])Fixtures([/\\]|$)") { continue }
        $file = Get-Item -LiteralPath (Join-Path $Root $relativePath)
        if ($file.Length -gt $MaximumBytes) {
            throw "Fixture exceeds $MaximumBytes bytes: $relativePath ($($file.Length) bytes)."
        }
    }
}

function Assert-NoGeneratedChanges {
    param(
        [Parameter(Mandatory)][string[]] $Before,
        [Parameter(Mandatory)][string[]] $After)

    if (($Before -join "`n") -ne ($After -join "`n")) {
        throw "Verification changed tracked or untracked repository output."
    }
}

function Assert-CheckRejects {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Check)

    $rejected = $false
    try {
        & $Check
    }
    catch {
        $rejected = $true
    }

    if (!$rejected) {
        throw "Repository check '$Name' did not reject its intentionally failing fixture."
    }
}

function Test-RepositoryCheckFailurePaths {
    $fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("apexlab-verify-checks-" + [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    try {
        [IO.File]::WriteAllText((Join-Path $fixtureRoot "conflict.txt"), ("<" * 7) + " HEAD")
        Assert-CheckRejects -Name "conflict markers" -Check {
            Assert-NoConflictMarkers -Root $fixtureRoot -RelativePaths @("conflict.txt")
        }

        [IO.File]::WriteAllText(
            (Join-Path $fixtureRoot "secret.txt"),
            ("AK" + "IA" + ("A" * 16)))
        Assert-CheckRejects -Name "secret patterns" -Check {
            Assert-NoSecretPatterns -Root $fixtureRoot -RelativePaths @("secret.txt")
        }

        [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot "data")) | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixtureRoot "data\private.txt"), "private")
        Assert-CheckRejects -Name "personal data" -Check {
            Assert-NoPersonalDataDirectories -RelativePaths @("data/private.txt")
        }

        $largeFixtureDirectory = Join-Path $fixtureRoot "tests\Fixtures"
        [IO.Directory]::CreateDirectory($largeFixtureDirectory) | Out-Null
        $largeFixture = Join-Path $largeFixtureDirectory "large.bin"
        $stream = [IO.File]::Open($largeFixture, [IO.FileMode]::CreateNew)
        try { $stream.SetLength($maximumFixtureBytes + 1) } finally { $stream.Dispose() }
        Assert-CheckRejects -Name "fixture size" -Check {
            Assert-FixtureSizeBudget `
                -Root $fixtureRoot `
                -RelativePaths @("tests/Fixtures/large.bin") `
                -MaximumBytes $maximumFixtureBytes
        }

        Assert-CheckRejects -Name "generated changes" -Check {
            Assert-NoGeneratedChanges -Before @("before") -After @("after")
        }
    }
    finally {
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}

function Assert-SafeArtifactPath {
    param([Parameter(Mandatory)][string] $Path)

    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (!$resolvedPath.StartsWith(
        $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated path escaped the repository artifacts directory: $resolvedPath"
    }
}

Push-Location $repositoryRoot
try {
    $initialStatus = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw "git status failed before verification." }

    Test-RepositoryCheckFailurePaths

    $actualSdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE." }
    if ($actualSdkVersion -ne $expectedSdkVersion) {
        throw "Expected .NET SDK $expectedSdkVersion but found $actualSdkVersion."
    }

    Assert-SafeArtifactPath -Path $testResultsDirectory
    if ([IO.Directory]::Exists($testResultsDirectory)) {
        [IO.Directory]::Delete($testResultsDirectory, $true)
    }
    [IO.Directory]::CreateDirectory($testResultsDirectory) | Out-Null

    Invoke-DotNet -Arguments @("restore", "ApexLab.slnx", "--locked-mode")
    Invoke-DotNet -Arguments @("format", "ApexLab.slnx", "--no-restore", "--verify-no-changes")
    Invoke-DotNet -Arguments @("build", "ApexLab.slnx", "-c", "Release", "--no-restore")
    Invoke-DotNet -Arguments @(
        "test", "ApexLab.slnx", "-c", "Release", "--no-build", "--no-restore",
        "--filter", "TestCategory!=Soak",
        "--logger", "trx",
        "--collect", "XPlat Code Coverage",
        "--results-directory", $testResultsDirectory)

    $trackedFiles = @(Get-TrackedFiles -Root $repositoryRoot)
    Assert-NoConflictMarkers -Root $repositoryRoot -RelativePaths $trackedFiles
    Assert-NoSecretPatterns -Root $repositoryRoot -RelativePaths $trackedFiles
    Assert-NoPersonalDataDirectories -RelativePaths $trackedFiles
    Assert-FixtureSizeBudget `
        -Root $repositoryRoot `
        -RelativePaths $trackedFiles `
        -MaximumBytes $maximumFixtureBytes

    $finalStatus = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw "git status failed after verification." }
    Assert-NoGeneratedChanges -Before $initialStatus -After $finalStatus
}
finally {
    Pop-Location
}
