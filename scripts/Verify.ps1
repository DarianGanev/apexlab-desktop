$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$expectedSdkVersion = "10.0.302"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    $actualSdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet --version failed with exit code $LASTEXITCODE."
    }

    if ($actualSdkVersion -ne $expectedSdkVersion) {
        throw "Expected .NET SDK $expectedSdkVersion but found $actualSdkVersion."
    }

    Invoke-DotNet -Arguments @("restore", "ApexLab.slnx", "--locked-mode")
    Invoke-DotNet -Arguments @(
        "format",
        "ApexLab.slnx",
        "--no-restore",
        "--verify-no-changes")
    Invoke-DotNet -Arguments @(
        "build",
        "ApexLab.slnx",
        "-c",
        "Release",
        "--no-restore")
    Invoke-DotNet -Arguments @(
        "test",
        "ApexLab.slnx",
        "-c",
        "Release",
        "--no-build",
        "--no-restore")
}
finally {
    Pop-Location
}
