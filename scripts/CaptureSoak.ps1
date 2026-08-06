[CmdletBinding()]
param(
    [ValidateRange(10000, 5000000)]
    [int] $DatagramCount = 100000,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot `
    "tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj"
$resultsDirectory = Join-Path $repositoryRoot "artifacts/test-results/capture-soak"
$priorDatagramCount = $env:APEXLAB_SOAK_DATAGRAMS

try {
    $env:APEXLAB_SOAK_DATAGRAMS = `
        $DatagramCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null

    & dotnet restore (Join-Path $repositoryRoot "ApexLab.slnx") --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed with exit code $LASTEXITCODE."
    }

    & dotnet test $projectPath `
        --configuration $Configuration `
        --no-restore `
        --filter "TestCategory=Soak" `
        --logger "console;verbosity=normal" `
        --logger "trx;LogFileName=capture-soak.trx" `
        --results-directory $resultsDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Capture soak failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:APEXLAB_SOAK_DATAGRAMS = $priorDatagramCount
}
