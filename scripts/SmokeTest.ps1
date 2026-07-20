param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [ValidateRange(1, 300)]
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetExtension($resolvedPackage) -ne ".zip") {
    throw "Smoke-test package must be a .zip file."
}
$packageMatch = [regex]::Match(
    [IO.Path]::GetFileName($resolvedPackage),
    '^ApexLab-(?<version>\d+\.\d+\.\d+)-win-x64\.zip$')
if (!$packageMatch.Success) {
    throw "Smoke-test package name must be ApexLab-<version>-win-x64.zip."
}
$expectedVersion = $packageMatch.Groups["version"].Value

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("apexlab-package-smoke-" + [Guid]::NewGuid().ToString("N"))
$extractRoot = Join-Path $testRoot "package"
$dataRoot = Join-Path $testRoot "data"
$resultDirectory = Join-Path $testRoot "result"
$resultFile = Join-Path $resultDirectory "smoke-result.json"
$process = $null

[IO.Directory]::CreateDirectory($extractRoot) | Out-Null
[IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
try {
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $extractRoot
    $executables = @(Get-ChildItem -LiteralPath $extractRoot -Filter "ApexLab.App.exe" -File -Recurse)
    if ($executables.Count -ne 1) {
        throw "Package must contain exactly one ApexLab.App.exe; found $($executables.Count)."
    }

    $process = Start-Process `
        -FilePath $executables[0].FullName `
        -ArgumentList @("--smoke-test", "--data-root", $dataRoot, "--result-file", $resultFile) `
        -WindowStyle Hidden `
        -PassThru
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        throw "Packaged smoke test timed out after $TimeoutSeconds seconds."
    }

    if ($process.ExitCode -ne 0) {
        throw "Packaged smoke test exited with code $($process.ExitCode)."
    }
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
        throw "Packaged smoke-test process still exists after exit."
    }

    $result = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json
    if ($result.product -ne "ApexLab" `
        -or $result.version -ne $expectedVersion `
        -or $result.schemaVersion -ne 1 `
        -or !$result.settingsValid `
        -or $result.completionState -ne "Completed") {
        throw "Packaged smoke-test result is invalid."
    }

    $settingsPath = Join-Path $dataRoot "settings.json"
    $databasePath = Join-Path $dataRoot "apexlab.db"
    $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    if ([IO.Path]::GetFullPath([string] $settings.dataRootPath) -ne [IO.Path]::GetFullPath($dataRoot)) {
        throw "Packaged smoke-test settings use an unexpected data root."
    }

    $header = [IO.File]::ReadAllBytes($databasePath)
    if ($header.Length -lt 64) { throw "Packaged smoke-test database header is incomplete." }
    $schemaVersion = ($header[60] -shl 24) -bor ($header[61] -shl 16) -bor ($header[62] -shl 8) -bor $header[63]
    if ($schemaVersion -ne 1) { throw "Packaged smoke-test database schema is $schemaVersion, expected 1." }

    [pscustomobject]@{
        Package = $resolvedPackage
        ExitCode = $process.ExitCode
        Product = $result.product
        Version = $result.version
        SchemaVersion = $schemaVersion
        SettingsValid = [bool] $result.settingsValid
        Completion = $result.completionState
    }
}
finally {
    if ($null -ne $process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }

    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolvedTestRoot.StartsWith(
        $resolvedTempRoot + "apexlab-package-smoke-",
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe smoke-test cleanup target: $resolvedTestRoot"
    }
    if ([IO.Directory]::Exists($resolvedTestRoot)) {
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}
