[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepositoryRoot,
    [Parameter(Mandatory)] [string] $StagingDataRoot,
    [Parameter(Mandatory)] [string] $DataRoot,
    [Parameter(Mandatory)] [string] $ProbeSourcePath,
    [Parameter(Mandatory)] [string] $ReplayPath,
    [Parameter(Mandatory)] [string] $DiagnosticsRoot,
    [Parameter(Mandatory)] [string] $GameBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$stagingDataRoot = [IO.Path]::GetFullPath($StagingDataRoot)
$dataRoot = [IO.Path]::GetFullPath($DataRoot)
$probeSourcePath = [IO.Path]::GetFullPath($ProbeSourcePath)
$replayPath = [IO.Path]::GetFullPath($ReplayPath)
$diagnosticsRoot = [IO.Path]::GetFullPath($DiagnosticsRoot)
$modulePath = Join-Path $repositoryRoot 'scripts/PrivateF125Validation.psm1'
Import-Module -Force -Name $modulePath
$module = Get-Module PrivateF125Validation

$privateRoot = Join-Path $dataRoot 'private-validation'
$capturesRoot = Join-Path $dataRoot 'captures'
[void][IO.Directory]::CreateDirectory($privateRoot)
[void][IO.Directory]::CreateDirectory($capturesRoot)
[IO.File]::WriteAllText(
    (Join-Path $privateRoot 'latest-safe.json'),
    '{"status":"stale"}',
    [Text.UTF8Encoding]::new($false))
$runRoot = Join-Path $privateRoot ("run-{0}" -f [guid]::NewGuid().ToString('N'))
$stages = [Collections.Generic.List[string]]::new()
$dependencies = @{
    Preflight = {
        param($game, $repo)
        [void][IO.Directory]::CreateDirectory($runRoot)
        $tools = & $module {
            param($resolvedRepositoryRoot)
            Get-ApexLabTrustedExecutablePaths `
                -RepositoryRoot $resolvedRepositoryRoot
        } $repo
        $head = & $module {
            param($resolvedRunRoot, $gitPath, $resolvedRepositoryRoot)
            (Invoke-ApexLabPrivateTextCommand `
                -RunRoot $resolvedRunRoot `
                -FilePath $gitPath `
                -Arguments @(
                    '-C', $resolvedRepositoryRoot, 'rev-parse', '--verify',
                    'HEAD')).Trim()
        } $runRoot $tools.GitPath $repo
        [pscustomobject][ordered]@{
            RepositoryRoot = $repo
            RepositoryHead = $head
            DataRoot = $dataRoot
            CapturesRoot = $capturesRoot
            PrivateRoot = $privateRoot
            RunRoot = $runRoot
            ReplayPath = $replayPath
            ApplicationVersion = '0.1.0'
            GitPath = $tools.GitPath
            RequireCleanWorktree = $false
        }
    }
    Probe = {
        param($context)
        $destination = Join-Path $context.RunRoot 'probe.json'
        [IO.File]::Copy($probeSourcePath, $destination, $false)
        $destination
    }
    ProbeEvaluation = {
        param($path)
        Read-ApexLabPrivateProbePlan -Path $path
    }
    Capture = {
        param($context)
        $before = @(Get-ChildItem `
            -LiteralPath $context.CapturesRoot `
            -File `
            -Filter '*.apxraw.json' | ForEach-Object { $_.Name })
        $stagingCaptures = Join-Path $stagingDataRoot 'captures'
        Get-ChildItem -LiteralPath $stagingCaptures -File | ForEach-Object {
            [IO.File]::Copy(
                $_.FullName,
                (Join-Path $context.CapturesRoot $_.Name),
                $false)
        }
        $after = @(Get-ChildItem `
            -LiteralPath $context.CapturesRoot `
            -File `
            -Filter '*.apxraw.json' | ForEach-Object { $_.Name })
        [pscustomobject][ordered]@{Before=$before;After=$after}
    }
    CaptureSelection = {
        param($capture)
        Select-ApexLabNewCaptureId `
            -Before $capture.Before `
            -After $capture.After
    }
    PrivateValidation = {
        param($context, $captureId, $probePath)
        $outputPath = Join-Path $context.RunRoot 'validator.json'
        $errorPath = Join-Path $context.RunRoot 'validator.stderr'
        $result = Invoke-ApexLabCapturedProcess `
            -FilePath $context.ReplayPath `
            -ArgumentList @(
                'validate', '--data-root', $context.DataRoot,
                '--capture-id', $captureId,
                '--probe-report', $probePath) `
            -StandardOutputPath $outputPath `
            -StandardErrorPath $errorPath `
            -TimeoutSeconds 300
        if ($result.ExitCode -ne 0) {
            throw 'Synthetic typed validation failed.'
        }
        [IO.File]::ReadAllText($outputPath)
    }
    RateGate = {
        param($context, $ratePlan)
        $diagnostic = [pscustomobject][ordered]@{
            DatagramCount = $ratePlan.DatagramCount
            TargetRate = $ratePlan.TargetRate
            MeasuredPeak = [int]($ratePlan.MinimumRate / 2)
        } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText(
            (Join-Path $diagnosticsRoot 'soak-arguments.json'),
            $diagnostic,
            [Text.UTF8Encoding]::new($false))
    }
    SafeSummary = {
        param($context, $validation, $game)
        & $module {
            param($resolvedContext, $resolvedValidation, $resolvedGame)
            Write-ApexLabProductionSafeSummary `
                -Context $resolvedContext `
                -Validation $resolvedValidation `
                -GameBuild $resolvedGame
        } $context $validation $game
    }
    Cleanup = {
        param($context)
        if ($null -ne $context) {
            & $module {
                param($resolvedRunRoot, $resolvedPrivateRoot)
                Remove-ApexLabPrivateRunRoot `
                    -RunRoot $resolvedRunRoot `
                    -PrivateRoot $resolvedPrivateRoot
            } $context.RunRoot $context.PrivateRoot
        }
    }
    Emit = {
        param($message)
        $stages.Add($message.Substring('stage='.Length))
    }
}

try {
    $summary = Invoke-ApexLabPrivateF125Validation `
        -GameBuild $GameBuild `
        -RepositoryRoot $repositoryRoot `
        -Dependencies $dependencies
}
catch {
    [Console]::Error.WriteLine(
        ("syntheticStage={0}" -f $_.Exception.Data['ApexLabStage']))
    throw
}
[pscustomobject][ordered]@{
    Stages = @($stages)
    Summary = $summary
} | ConvertTo-Json -Compress
