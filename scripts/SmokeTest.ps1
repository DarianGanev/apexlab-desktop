param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [ValidateRange(35, 300)]
    [int] $TimeoutSeconds = 45
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
$smokeEvidence = $null
$primaryError = $null

function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Argument)

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [Text.StringBuilder]::new()
    [void] $builder.Append([char] 34)
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char] 92) {
            $backslashCount++
            continue
        }

        if ($character -eq [char] 34) {
            [void] $builder.Append([char] 92, ($backslashCount * 2) + 1)
            [void] $builder.Append([char] 34)
        }
        else {
            [void] $builder.Append([char] 92, $backslashCount)
            [void] $builder.Append($character)
        }
        $backslashCount = 0
    }

    [void] $builder.Append([char] 92, $backslashCount * 2)
    [void] $builder.Append([char] 34)
    return $builder.ToString()
}

try {
    [IO.Directory]::CreateDirectory($extractRoot) | Out-Null
    [IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $extractRoot
    $executables = @(Get-ChildItem -LiteralPath $extractRoot -Filter "ApexLab.App.exe" -File -Recurse)
    if ($executables.Count -ne 1) {
        throw "Package must contain exactly one ApexLab.App.exe; found $($executables.Count)."
    }

    $processArguments = @(
        "--smoke-test", "--data-root", $dataRoot, "--result-file", $resultFile) |
        ForEach-Object { ConvertTo-ProcessArgument -Argument $_ }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executables[0].FullName
    $startInfo.Arguments = $processArguments -join " "
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Packaged smoke-test process did not start."
    }
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

    $smokeEvidence = [pscustomobject]@{
        Package = $resolvedPackage
        ExitCode = $process.ExitCode
        Product = $result.product
        Version = $result.version
        SchemaVersion = $schemaVersion
        SettingsValid = [bool] $result.settingsValid
        Completion = $result.completionState
    }
}
catch {
    $primaryError = $_
}

$cleanupFailures = [Collections.Generic.List[Exception]]::new()
if ($null -ne $process) {
    try {
        if (!$process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
    finally {
        $process.Dispose()
    }
}

try {
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
catch {
    $cleanupFailures.Add($_.Exception)
}

if ($null -ne $primaryError) {
    if ($cleanupFailures.Count -gt 0) {
        $failures = [Collections.Generic.List[Exception]]::new()
        $failures.Add($primaryError.Exception)
        $failures.AddRange($cleanupFailures)
        throw [AggregateException]::new(
            "Packaged smoke verification and cleanup both failed.",
            $failures)
    }
    throw $primaryError
}
if ($cleanupFailures.Count -eq 1) {
    throw $cleanupFailures[0]
}
if ($cleanupFailures.Count -gt 1) {
    throw [AggregateException]::new("Packaged smoke cleanup failed.", $cleanupFailures)
}

$smokeEvidence
