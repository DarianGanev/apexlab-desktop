[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $GitPath = "git",

    [ValidateNotNullOrEmpty()]
    [string] $DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$testResultsDirectory = Join-Path $artifactRoot "test-results"
$maximumFixtureBytes = 1MB

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-TrackedFiles {
    param([Parameter(Mandatory)][string] $Root)

    $files = @(& $GitPath -C $Root ls-files)
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

function Assert-CompatibleDotNetSdk {
    param(
        [Parameter(Mandatory)][string] $GlobalJsonPath,
        [Parameter(Mandatory)][string] $ActualVersion)

    try {
        $policy = Get-Content -Raw -LiteralPath $GlobalJsonPath |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "global.json contains an invalid SDK policy."
    }

    $versionPattern = '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$'
    if ($null -eq $policy.sdk `
        -or [string]$policy.sdk.rollForward -cne 'latestPatch' `
        -or [string]$policy.sdk.version -notmatch $versionPattern) {
        throw "global.json must declare a stable latestPatch SDK policy."
    }

    $minimumMatch = [regex]::Match(
        [string]$policy.sdk.version,
        $versionPattern)
    $actualMatch = [regex]::Match($ActualVersion, $versionPattern)
    if (!$actualMatch.Success) {
        throw "Expected a compatible .NET SDK but found '$ActualVersion'."
    }

    try {
        $minimumMajor = [int]::Parse($minimumMatch.Groups['major'].Value)
        $minimumMinor = [int]::Parse($minimumMatch.Groups['minor'].Value)
        $minimumPatch = [int]::Parse($minimumMatch.Groups['patch'].Value)
        $actualMajor = [int]::Parse($actualMatch.Groups['major'].Value)
        $actualMinor = [int]::Parse($actualMatch.Groups['minor'].Value)
        $actualPatch = [int]::Parse($actualMatch.Groups['patch'].Value)
    }
    catch [OverflowException] {
        throw "global.json contains an invalid SDK policy."
    }

    $minimumFeatureBand = [math]::Floor($minimumPatch / 100)
    $actualFeatureBand = [math]::Floor($actualPatch / 100)
    if ($actualMajor -ne $minimumMajor `
        -or $actualMinor -ne $minimumMinor `
        -or $actualFeatureBand -ne $minimumFeatureBand `
        -or $actualPatch -lt $minimumPatch) {
        throw (
            "Expected a compatible .NET SDK at or above {0} in feature band {1}.{2}.{3}xx, but found {4}." -f `
                $policy.sdk.version,
                $minimumMajor,
                $minimumMinor,
                $minimumFeatureBand,
                $ActualVersion)
    }
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
        [pscustomobject]@{
            Name = "AWS access key"
            Pattern = ("AK" + "IA[0-9A-Z]{16}")
        },
        [pscustomobject]@{
            Name = "GitHub legacy token"
            Pattern = ("gh" + "[pousr]_[A-Za-z0-9]{36,}")
        },
        [pscustomobject]@{
            Name = "GitHub fine-grained token"
            Pattern = ("github" + "_pat_[A-Za-z0-9_]{20,}")
        },
        [pscustomobject]@{
            Name = "private key"
            Pattern = ("-----BEGIN " + "(RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----")
        }
    )
    foreach ($relativePath in $RelativePaths) {
        $path = Join-Path $Root $relativePath
        $lines = Get-TextLines -Path $path
        if ($null -eq $lines) { continue }
        foreach ($pattern in $patterns) {
            if (($lines -join "`n") -match $pattern.Pattern) {
                throw "Potential $($pattern.Name) credential pattern found in $relativePath."
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

function Assert-NoRawEvidenceFiles {
    param([Parameter(Mandatory)][string[]] $RelativePaths)

    foreach ($relativePath in $RelativePaths) {
        $normalized = $relativePath.Replace("\", "/")
        if ($normalized -match "(^|/)private-validation(/|$)" `
            -or $normalized.EndsWith(".apxraw", [StringComparison]::OrdinalIgnoreCase) `
            -or $normalized.EndsWith(".apxraw.json", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Raw evidence or private validation content is tracked: $relativePath."
        }
    }
}

function Assert-PrivateValidationVerificationRecord {
    param([Parameter(Mandatory)][string] $Path)

    if (![IO.File]::Exists($Path)) {
        throw "Private validation verification record is missing."
    }
    $text = [IO.File]::ReadAllText($Path)
    $matches = [regex]::Matches(
        $text,
        '(?ms)```json\s*(\{.*?\})\s*```')
    if ($matches.Count -eq 0) { return }
    if ($matches.Count -ne 1) {
        throw "Private validation record is invalid."
    }
    try {
        $record = $matches[0].Groups[1].Value |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Private validation record is invalid."
    }
    if ($null -eq $record -or $record -isnot [pscustomobject]) {
        throw "Private validation record is invalid."
    }

    $forbidden = @(
        'captureId', 'sha256', 'path', 'sender', 'sessionUid', 'payload',
        'measuredPeak', 'minimumRate', 'targetRate')
    foreach ($propertyName in @(Get-JsonPropertyNamesRecursive -Value $record)) {
        if ($forbidden -contains $propertyName) {
            throw "Private validation record contains a forbidden property."
        }
    }
    $expected = @(
        'schemaVersion', 'status', 'validationDate', 'gameBuild',
        'adapterId', 'applicationVersion', 'offlineCapture',
        'manifestIntegrity', 'deterministicReplay',
        'sequenceGapPreservation', 'zeroPrivacyExcludedEvidence',
        'probeAssumptions', 'rateGate2x', 'conclusion')
    $actual = @($record.PSObject.Properties.Name)
    if ($actual.Count -ne $expected.Count) {
        throw "Private validation record is invalid."
    }
    foreach ($propertyName in $expected) {
        if ($actual -cnotcontains $propertyName) {
            throw "Private validation record is invalid."
        }
    }
    if ($record.schemaVersion -isnot [int] `
        -or $record.schemaVersion -ne 1 `
        -or $record.status -cne 'passed' `
        -or $record.adapterId -cne 'ea-f1-25-v3' `
        -or $record.applicationVersion -notmatch '^\d+\.\d+\.\d+$' `
        -or $record.validationDate -notmatch '^\d{4}-\d{2}-\d{2}$' `
        -or $record.conclusion -cne 'PASS' `
        -or [string]::IsNullOrWhiteSpace($record.gameBuild) `
        -or $record.gameBuild.Trim() -cne $record.gameBuild `
        -or $record.gameBuild.Contains([IO.Path]::DirectorySeparatorChar) `
        -or $record.gameBuild.Contains([IO.Path]::AltDirectorySeparatorChar) `
        -or $record.gameBuild.Length -gt 80) {
        throw "Private validation record is invalid."
    }
    foreach ($character in $record.gameBuild.ToCharArray()) {
        if ([char]::IsControl($character)) {
            throw "Private validation record is invalid."
        }
    }
    $parsedDate = [datetime]::MinValue
    if (![datetime]::TryParseExact(
        $record.validationDate,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$parsedDate)) {
        throw "Private validation record is invalid."
    }
    foreach ($propertyName in $expected[6..12]) {
        if ($record.$propertyName -isnot [bool] -or !$record.$propertyName) {
            throw "Private validation record is invalid."
        }
    }
}

function Get-JsonPropertyNamesRecursive {
    param($Value)

    if ($Value -is [pscustomobject]) {
        foreach ($property in $Value.PSObject.Properties) {
            $property.Name
            Get-JsonPropertyNamesRecursive -Value $property.Value
        }
    }
    elseif ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            Get-JsonPropertyNamesRecursive -Value $item
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
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Before,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $After)

    if (($Before -join "`n") -ne ($After -join "`n")) {
        throw "Verification changed tracked or untracked repository output."
    }
}

function Assert-CheckRejects {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $ExpectedMessagePattern,
        [Parameter(Mandatory)][scriptblock] $Check)

    $rejection = $null
    try {
        & $Check
    }
    catch {
        $rejection = $_
    }

    if ($null -eq $rejection) {
        throw "Repository check '$Name' did not reject its intentionally failing fixture."
    }
    if ($rejection.Exception.Message -notmatch $ExpectedMessagePattern) {
        throw "Repository check '$Name' failed for the wrong reason: $($rejection.Exception.Message)"
    }
}

function Test-RepositoryCheckFailurePaths {
    $fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("apexlab-verify-checks-" + [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    try {
        $sdkPolicyPath = Join-Path $fixtureRoot "global.json"
        [IO.File]::WriteAllText(
            $sdkPolicyPath,
            '{"sdk":{"version":"10.0.302","rollForward":"latestPatch"}}')
        Assert-CompatibleDotNetSdk `
            -GlobalJsonPath $sdkPolicyPath `
            -ActualVersion "10.0.302"
        Assert-CompatibleDotNetSdk `
            -GlobalJsonPath $sdkPolicyPath `
            -ActualVersion "10.0.303"
        Assert-CheckRejects `
            -Name "SDK patch below minimum" `
            -ExpectedMessagePattern '^Expected a compatible .NET SDK' `
            -Check {
            Assert-CompatibleDotNetSdk `
                -GlobalJsonPath $sdkPolicyPath `
                -ActualVersion "10.0.301"
        }
        Assert-CheckRejects `
            -Name "SDK feature-band drift" `
            -ExpectedMessagePattern '^Expected a compatible .NET SDK' `
            -Check {
            Assert-CompatibleDotNetSdk `
                -GlobalJsonPath $sdkPolicyPath `
                -ActualVersion "10.0.400"
        }

        [IO.File]::WriteAllText((Join-Path $fixtureRoot "conflict.txt"), ("<" * 7) + " HEAD")
        Assert-NoGeneratedChanges -Before @() -After @()
        Assert-CheckRejects `
            -Name "conflict markers" `
            -ExpectedMessagePattern '^Conflict marker found' `
            -Check {
            Assert-NoConflictMarkers -Root $fixtureRoot -RelativePaths @("conflict.txt")
        }

        $credentialFixtures = @(
            [pscustomobject]@{
                Name = "AWS access key"
                File = "aws-secret.txt"
                Value = ("AK" + "IA" + ("A" * 16))
            },
            [pscustomobject]@{
                Name = "GitHub legacy token"
                File = "github-legacy-secret.txt"
                Value = ("gh" + "p_" + ("a" * 36))
            },
            [pscustomobject]@{
                Name = "GitHub fine-grained token"
                File = "github-fine-grained-secret.txt"
                Value = ("github" + "_pat_" + ("a" * 32))
            },
            [pscustomobject]@{
                Name = "private key"
                File = "private-key.txt"
                Value = ("-----BEGIN " + "ENCRYPTED PRIVATE KEY-----")
            }
        )
        foreach ($credentialFixture in $credentialFixtures) {
            [IO.File]::WriteAllText(
                (Join-Path $fixtureRoot $credentialFixture.File),
                $credentialFixture.Value)
            Assert-CheckRejects `
                -Name $credentialFixture.Name `
                -ExpectedMessagePattern ("^Potential " + [regex]::Escape($credentialFixture.Name)) `
                -Check {
                Assert-NoSecretPatterns `
                    -Root $fixtureRoot `
                    -RelativePaths @($credentialFixture.File)
            }
        }

        [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot "data")) | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixtureRoot "data\private.txt"), "private")
        Assert-CheckRejects `
            -Name "personal data" `
            -ExpectedMessagePattern '^Personal-data directory is tracked' `
            -Check {
            Assert-NoPersonalDataDirectories -RelativePaths @("data/private.txt")
        }

        Assert-CheckRejects `
            -Name "raw evidence" `
            -ExpectedMessagePattern '^Raw evidence or private validation content is tracked' `
            -Check {
            Assert-NoRawEvidenceFiles -RelativePaths @("captures/synthetic.apxraw")
        }
        Assert-CheckRejects `
            -Name "raw evidence manifest" `
            -ExpectedMessagePattern '^Raw evidence or private validation content is tracked' `
            -Check {
            Assert-NoRawEvidenceFiles -RelativePaths @("synthetic.apxraw.json")
        }
        Assert-CheckRejects `
            -Name "private validation" `
            -ExpectedMessagePattern '^Raw evidence or private validation content is tracked' `
            -Check {
            Assert-NoRawEvidenceFiles -RelativePaths @("private-validation/aggregate.json")
        }

        $safePrivateRecord = @'
```json
{"schemaVersion":1,"status":"passed","validationDate":"2026-08-08","gameBuild":"1.2.3 test","adapterId":"ea-f1-25-v3","applicationVersion":"0.1.0","offlineCapture":true,"manifestIntegrity":true,"deterministicReplay":true,"sequenceGapPreservation":true,"zeroPrivacyExcludedEvidence":true,"probeAssumptions":true,"rateGate2x":true,"conclusion":"PASS"}
```
'@
        $safePrivateRecordPath = Join-Path $fixtureRoot "safe-private.md"
        [IO.File]::WriteAllText($safePrivateRecordPath, $safePrivateRecord)
        Assert-PrivateValidationVerificationRecord `
            -Path $safePrivateRecordPath
        foreach ($forbiddenProperty in @(
            'captureId', 'sha256', 'path', 'sender', 'sessionUid',
            'payload', 'measuredPeak', 'minimumRate', 'targetRate')) {
            $unsafePrivateRecordPath = Join-Path $fixtureRoot (
                "unsafe-private-{0}.md" -f $forbiddenProperty)
            $unsafePrivateRecord = $safePrivateRecord.Replace(
                '"conclusion":"PASS"',
                ('"conclusion":"PASS","{0}":"private"' -f $forbiddenProperty))
            [IO.File]::WriteAllText(
                $unsafePrivateRecordPath,
                $unsafePrivateRecord)
            Assert-CheckRejects `
                -Name ("private validation property " + $forbiddenProperty) `
                -ExpectedMessagePattern '^Private validation record contains a forbidden property' `
                -Check {
                Assert-PrivateValidationVerificationRecord `
                    -Path $unsafePrivateRecordPath
            }
        }

        $largeFixtureDirectory = Join-Path $fixtureRoot "tests\Fixtures"
        [IO.Directory]::CreateDirectory($largeFixtureDirectory) | Out-Null
        $largeFixture = Join-Path $largeFixtureDirectory "large.bin"
        $stream = [IO.File]::Open($largeFixture, [IO.FileMode]::CreateNew)
        try { $stream.SetLength($maximumFixtureBytes + 1) } finally { $stream.Dispose() }
        Assert-CheckRejects `
            -Name "fixture size" `
            -ExpectedMessagePattern '^Fixture exceeds' `
            -Check {
            Assert-FixtureSizeBudget `
                -Root $fixtureRoot `
                -RelativePaths @("tests/Fixtures/large.bin") `
                -MaximumBytes $maximumFixtureBytes
        }

        Assert-CheckRejects `
            -Name "generated changes" `
            -ExpectedMessagePattern '^Verification changed' `
            -Check {
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
    $initialStatus = @(& $GitPath status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw "git status failed before verification." }

    Test-RepositoryCheckFailurePaths

    $actualSdkVersion = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE." }
    Assert-CompatibleDotNetSdk `
        -GlobalJsonPath (Join-Path $repositoryRoot 'global.json') `
        -ActualVersion $actualSdkVersion

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
    Assert-NoRawEvidenceFiles -RelativePaths $trackedFiles
    Assert-PrivateValidationVerificationRecord `
        -Path (Join-Path $repositoryRoot 'docs/verification/v0.2.0-private-f125.md')
    Assert-FixtureSizeBudget `
        -Root $repositoryRoot `
        -RelativePaths $trackedFiles `
        -MaximumBytes $maximumFixtureBytes

    $finalStatus = @(& $GitPath status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw "git status failed after verification." }
    Assert-NoGeneratedChanges -Before $initialStatus -After $finalStatus
}
finally {
    Pop-Location
}
