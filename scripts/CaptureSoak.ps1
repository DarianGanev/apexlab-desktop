[CmdletBinding()]
param(
    [ValidateRange(10000, 5000000)]
    [int] $DatagramCount = 100000,

    [ValidateRange(0, 100000)]
    [int] $TargetDatagramsPerSecond = 0,

    [ValidateRange(0, 37878)]
    [int] $MeasuredRealPeakDatagramsPerSecond = 0,

    [ValidateRange(950, 1000)]
    [int] $MinimumObservedFractionPermille = 950,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [ValidateNotNullOrEmpty()]
    [string] $DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot `
    "tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj"
$resultsDirectory = Join-Path $repositoryRoot "artifacts/test-results/capture-soak"
$summaryPath = Join-Path $resultsDirectory "capture-soak-summary.json"
$priorDatagramCount = $env:APEXLAB_SOAK_DATAGRAMS
$priorTargetRate = $env:APEXLAB_SOAK_TARGET_DATAGRAMS_PER_SECOND
$priorMeasuredRealPeakRate = `
    $env:APEXLAB_SOAK_MEASURED_REAL_PEAK_DATAGRAMS_PER_SECOND
$priorMinimumFraction = $env:APEXLAB_SOAK_MINIMUM_OBSERVED_FRACTION_PERMILLE
$priorSummaryPath = $env:APEXLAB_SOAK_SUMMARY_PATH
$minimumObservedRate = 2 * $MeasuredRealPeakDatagramsPerSecond

if ($MeasuredRealPeakDatagramsPerSecond -gt 0) {
    $minimumTargetRate = [int][Math]::Ceiling(
        ([decimal]$minimumObservedRate * 11) / 10)
    if ($TargetDatagramsPerSecond -lt $minimumTargetRate) {
        throw "The rate gate target must be at least 110% of its derived 2x minimum."
    }
    $minimumDatagramCount = 60L * $TargetDatagramsPerSecond
    if ($DatagramCount -lt $minimumDatagramCount) {
        throw "The rate gate must run for at least 60 seconds at its target rate."
    }
}

try {
    $env:APEXLAB_SOAK_DATAGRAMS = `
        $DatagramCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:APEXLAB_SOAK_TARGET_DATAGRAMS_PER_SECOND = `
        $TargetDatagramsPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:APEXLAB_SOAK_MEASURED_REAL_PEAK_DATAGRAMS_PER_SECOND = `
        $MeasuredRealPeakDatagramsPerSecond.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
    $env:APEXLAB_SOAK_MINIMUM_OBSERVED_FRACTION_PERMILLE = `
        $MinimumObservedFractionPermille.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
    $env:APEXLAB_SOAK_SUMMARY_PATH = $summaryPath
    New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null
    Remove-Item -LiteralPath $summaryPath -ErrorAction SilentlyContinue

    & $DotNetPath restore (Join-Path $repositoryRoot "ApexLab.slnx") --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed with exit code $LASTEXITCODE."
    }

    & $DotNetPath test $projectPath `
        --configuration $Configuration `
        --no-restore `
        --filter "TestCategory=Soak" `
        --logger "console;verbosity=normal" `
        --logger "trx;LogFileName=capture-soak.trx" `
        --results-directory $resultsDirectory
    $testExitCode = $LASTEXITCODE

    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        Get-Content -Raw -LiteralPath $summaryPath
    }
    elseif ($testExitCode -eq 0) {
        throw "Capture soak did not produce its aggregate summary."
    }

    if ($testExitCode -ne 0) {
        throw "Capture soak failed with exit code $testExitCode."
    }
}
finally {
    $env:APEXLAB_SOAK_DATAGRAMS = $priorDatagramCount
    $env:APEXLAB_SOAK_TARGET_DATAGRAMS_PER_SECOND = $priorTargetRate
    $env:APEXLAB_SOAK_MEASURED_REAL_PEAK_DATAGRAMS_PER_SECOND = `
        $priorMeasuredRealPeakRate
    $env:APEXLAB_SOAK_MINIMUM_OBSERVED_FRACTION_PERMILLE = $priorMinimumFraction
    $env:APEXLAB_SOAK_SUMMARY_PATH = $priorSummaryPath
}
