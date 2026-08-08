Set-StrictMode -Version Latest

function Assert-ApexLabGameBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $GameBuild
    )

    if ([string]::IsNullOrWhiteSpace($GameBuild) `
        -or $GameBuild.Length -gt 80 `
        -or $GameBuild.Trim() -ne $GameBuild `
        -or $GameBuild.Contains([IO.Path]::DirectorySeparatorChar) `
        -or $GameBuild.Contains([IO.Path]::AltDirectorySeparatorChar)) {
        throw "The F1 25 game build must be bounded single-line text without a path separator."
    }

    foreach ($character in $GameBuild.ToCharArray()) {
        if ([char]::IsControl($character)) {
            throw "The F1 25 game build must be bounded single-line text without a path separator."
        }
    }
}

function Select-ApexLabNewCaptureId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $Before,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $After
    )

    $pattern = '^[0-9a-f]{32}\.apxraw\.json$'
    $beforeSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($leafName in $Before) {
        if ($leafName -notmatch $pattern -or !$beforeSet.Add($leafName)) {
            throw "The existing finalized manifest set is invalid."
        }
    }

    $afterSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $added = [Collections.Generic.List[string]]::new()
    foreach ($leafName in $After) {
        if ($leafName -notmatch $pattern -or !$afterSet.Add($leafName)) {
            throw "The finalized manifest set is invalid."
        }
        if (!$beforeSet.Contains($leafName)) {
            $added.Add($leafName)
        }
    }

    if ($added.Count -ne 1) {
        throw "Exactly one new finalized capture is required."
    }

    return $added[0].Substring(0, 32)
}

function Get-ApexLabRateGatePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int] $Peak
    )

    if ($Peak -lt 1 -or $Peak -gt 37878) {
        throw "The measured peak is outside the feasible private rate-gate range."
    }

    $minimumRate = [int](2L * $Peak)
    $targetRate = [int][Math]::Ceiling(
        ([decimal]$minimumRate * 11) / 10)
    $datagramCount = [long][Math]::Max(
        10000L,
        60L * $targetRate)
    $injectionSeconds = [int][Math]::Ceiling(
        [decimal]$datagramCount / [decimal]$targetRate)
    $timeoutSeconds = $injectionSeconds + 240
    if ($targetRate -gt 100000 `
        -or $datagramCount -gt 5000000 `
        -or $timeoutSeconds -gt 3600) {
        throw "The measured peak cannot produce a feasible private rate gate."
    }

    return [pscustomobject][ordered]@{
        MinimumRate = $minimumRate
        TargetRate = $targetRate
        DatagramCount = [int]$datagramCount
        TimeoutSeconds = [int]$timeoutSeconds
    }
}

function Get-ApexLabPrivateValidationExitCode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Stage
    )

    $exitCodes = @{
        preflight = 40
        probe = 41
        probeEvaluation = 42
        capture = 43
        captureSelection = 44
        privateValidation = 45
        rateGate = 46
        safeSummary = 47
        cancelled = 48
        unexpectedFailure = 49
    }
    if (!$exitCodes.ContainsKey($Stage)) {
        throw "The private validation stage is invalid."
    }

    return [int]$exitCodes[$Stage]
}

function Invoke-ApexLabPrivateF125Validation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $GameBuild,

        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [hashtable] $Dependencies
    )

    if ($null -eq $Dependencies) {
        $Dependencies = New-ApexLabProductionDependencies
    }
    $requiredDependencies = @(
        'Preflight', 'Probe', 'ProbeEvaluation', 'Capture',
        'CaptureSelection', 'PrivateValidation', 'RateGate',
        'SafeSummary', 'Cleanup', 'Emit')
    foreach ($name in $requiredDependencies) {
        if (!$Dependencies.ContainsKey($name) `
            -or $Dependencies[$name] -isnot [scriptblock]) {
            throw "The private validation dependency set is incomplete."
        }
    }

    $context = $null
    try {
        [void](& $Dependencies.Emit 'stage=preflight')
        $context = Invoke-ApexLabPrivateValidationStage `
            -Stage 'preflight' `
            -Action {
                param($game, $root, $preflight)
                Assert-ApexLabGameBuild -GameBuild $game
                if (![IO.Path]::IsPathRooted($root)) {
                    throw "The repository root must be absolute."
                }
                & $preflight $game ([IO.Path]::GetFullPath($root))
            } `
            -Arguments @(
                $GameBuild, $RepositoryRoot, $Dependencies.Preflight)
        [void](& $Dependencies.Emit 'stage=probe')
        $probePath = Invoke-ApexLabPrivateValidationStage `
            -Stage 'probe' `
            -Action $Dependencies.Probe `
            -Arguments @($context)
        [void](& $Dependencies.Emit 'stage=probeEvaluation')
        $probePlan = Invoke-ApexLabPrivateValidationStage `
            -Stage 'probeEvaluation' `
            -Action $Dependencies.ProbeEvaluation `
            -Arguments @($probePath)
        [void](& $Dependencies.Emit 'stage=capture')
        $capture = Invoke-ApexLabPrivateValidationStage `
            -Stage 'capture' `
            -Action $Dependencies.Capture `
            -Arguments @($context)
        [void](& $Dependencies.Emit 'stage=captureSelection')
        $captureId = Invoke-ApexLabPrivateValidationStage `
            -Stage 'captureSelection' `
            -Action $Dependencies.CaptureSelection `
            -Arguments @($capture)
        [void](& $Dependencies.Emit 'stage=privateValidation')
        $validatorJson = Invoke-ApexLabPrivateValidationStage `
            -Stage 'privateValidation' `
            -Action $Dependencies.PrivateValidation `
            -Arguments @($context, $captureId, $probePath)
        $validation = Invoke-ApexLabPrivateValidationStage `
            -Stage 'privateValidation' `
            -Action { param($json) Assert-ApexLabSafeValidatorJson -Json $json } `
            -Arguments @($validatorJson)
        $ratePlan = Get-ApexLabRateGatePlan `
            -Peak $probePlan.MeasuredPeakDatagramsPerSecond
        [void](& $Dependencies.Emit 'stage=rateGate')
        [void](Invoke-ApexLabPrivateValidationStage `
            -Stage 'rateGate' `
            -Action $Dependencies.RateGate `
            -Arguments @($context, $ratePlan))
        [void](& $Dependencies.Emit 'stage=safeSummary')
        $summary = Invoke-ApexLabPrivateValidationStage `
            -Stage 'safeSummary' `
            -Action $Dependencies.SafeSummary `
            -Arguments @($context, $validation, $GameBuild)
        return $summary
    }
    finally {
        & $Dependencies.Cleanup $context
    }
}

function New-ApexLabProductionDependencies {
    return @{
        Preflight = {
            param($gameBuild, $repositoryRoot)
            Invoke-ApexLabProductionPreflight `
                -GameBuild $gameBuild `
                -RepositoryRoot $repositoryRoot
        }
        Probe = {
            param($context)
            Invoke-ApexLabProductionProbe -Context $context
        }
        ProbeEvaluation = {
            param($path)
            Read-ApexLabPrivateProbePlan -Path $path
        }
        Capture = {
            param($context)
            Invoke-ApexLabProductionCapture -Context $context
        }
        CaptureSelection = {
            param($capture)
            Select-ApexLabNewCaptureId `
                -Before $capture.Before `
                -After $capture.After
        }
        PrivateValidation = {
            param($context, $captureId, $probePath)
            Invoke-ApexLabProductionPrivateValidation `
                -Context $context `
                -CaptureId $captureId `
                -ProbePath $probePath
        }
        RateGate = {
            param($context, $ratePlan)
            Invoke-ApexLabProductionRateGate `
                -Context $context `
                -RatePlan $ratePlan
        }
        SafeSummary = {
            param($context, $validation, $gameBuild)
            Write-ApexLabProductionSafeSummary `
                -Context $context `
                -Validation $validation `
                -GameBuild $gameBuild
        }
        Cleanup = {
            param($context)
            if ($null -ne $context -and $null -ne $context.RunRoot) {
                Remove-ApexLabPrivateRunRoot `
                    -RunRoot $context.RunRoot `
                    -PrivateRoot $context.PrivateRoot
            }
        }
        Emit = {
            param($message)
            [Console]::Out.WriteLine($message)
        }
    }
}

function Invoke-ApexLabProductionPreflight {
    param(
        [Parameter(Mandatory)] [string] $GameBuild,
        [Parameter(Mandatory)] [string] $RepositoryRoot
    )

    if ($env:OS -cne 'Windows_NT' `
        -or [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw "The private validation requires Windows and local application data."
    }
    Assert-ApexLabGameBuild -GameBuild $GameBuild
    $repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    if (![IO.Directory]::Exists($repositoryRoot)) {
        throw "The repository root is unavailable."
    }
    $tools = Get-ApexLabTrustedExecutablePaths `
        -RepositoryRoot $repositoryRoot

    $dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ApexLab'))
    $privateRoot = [IO.Path]::GetFullPath((
        Join-Path $dataRoot 'private-validation'))
    [void][IO.Directory]::CreateDirectory($privateRoot)
    Assert-ApexLabNoReparsePath `
        -Anchor ([IO.Path]::GetFullPath($env:LOCALAPPDATA)) `
        -Target $privateRoot
    $runRoot = Join-Path $privateRoot ("run-{0}" -f [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($runRoot)
    try {
        Assert-ApexLabNoReparsePath -Anchor $privateRoot -Target $runRoot
        $head = Invoke-ApexLabPrivateTextCommand `
            -RunRoot $runRoot `
            -FilePath $tools.GitPath `
            -Arguments @('-C', $repositoryRoot, 'rev-parse', '--verify', 'HEAD')
        $head = $head.Trim()
        if ($head -notmatch '^[0-9a-f]{40}$') {
            throw "The repository commit is unresolved."
        }
        [void](Invoke-ApexLabPrivateTextCommand `
            -RunRoot $runRoot `
            -FilePath $tools.GitPath `
            -Arguments @(
                '-C', $repositoryRoot, 'symbolic-ref', '--quiet', '--short',
                'HEAD'))
        $status = Invoke-ApexLabPrivateTextCommand `
            -RunRoot $runRoot `
            -FilePath $tools.GitPath `
            -Arguments @(
                '-C', $repositoryRoot, 'status', '--porcelain=v1',
                '--untracked-files=no')
        if (![string]::IsNullOrWhiteSpace($status)) {
            throw "The tracked worktree must be clean."
        }
        $sdk = (Invoke-ApexLabPrivateTextCommand `
            -RunRoot $runRoot `
            -FilePath $tools.DotNetPath `
            -Arguments @('--version')).Trim()
        if ($sdk -cne '10.0.302') {
            throw "The required .NET SDK is unavailable."
        }
        if (@(Get-Process -Name 'ApexLab' -ErrorAction SilentlyContinue).Count -ne 0) {
            throw "ApexLab must be closed before validation."
        }
        Assert-ApexLabLoopbackPortAvailable

        [void](Invoke-ApexLabPrivateTextCommand `
            -RunRoot $runRoot `
            -FilePath $tools.PowerShellPath `
            -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repositoryRoot 'scripts/Verify.ps1'),
                '-GitPath', $tools.GitPath,
                '-DotNetPath', $tools.DotNetPath))
        $applicationPath = Join-Path $repositoryRoot (
            'src/ApexLab.App/bin/Release/net10.0-windows/ApexLab.exe')
        $replayPath = Join-Path $repositoryRoot (
            'tools/ApexLab.Replay/bin/Release/net10.0-windows/ApexLab.Replay.exe')
        $soakPath = Join-Path $repositoryRoot 'scripts/CaptureSoak.ps1'
        foreach ($path in @($applicationPath, $replayPath, $soakPath)) {
            if (![IO.File]::Exists($path)) {
                throw "A required Release validation file is unavailable."
            }
        }
        $applicationHash = Get-ApexLabFileSha256 -Path $applicationPath
        $replayHash = Get-ApexLabFileSha256 -Path $replayPath
        $soakHash = Get-ApexLabFileSha256 -Path $soakPath
        $versionText = [IO.File]::ReadAllText((
            Join-Path $repositoryRoot 'Version.props'))
        $versionMatch = [regex]::Match(
            $versionText,
            '<VersionPrefix>(\d+\.\d+\.\d+)</VersionPrefix>')
        if (!$versionMatch.Success) {
            throw "The application version is invalid."
        }

        return [pscustomobject][ordered]@{
            RepositoryRoot = $repositoryRoot
            RepositoryHead = $head
            DataRoot = $dataRoot
            CapturesRoot = (Join-Path $dataRoot 'captures')
            PrivateRoot = $privateRoot
            RunRoot = $runRoot
            ApplicationPath = $applicationPath
            ReplayPath = $replayPath
            SoakPath = $soakPath
            ApplicationVersion = $versionMatch.Groups[1].Value
            ApplicationHash = $applicationHash
            ReplayHash = $replayHash
            SoakHash = $soakHash
            GitPath = $tools.GitPath
            DotNetPath = $tools.DotNetPath
            PowerShellPath = $tools.PowerShellPath
            RequireCleanWorktree = $true
        }
    }
    catch {
        Remove-ApexLabPrivateRunRoot `
            -RunRoot $runRoot `
            -PrivateRoot $privateRoot
        throw
    }
}

function Get-ApexLabTrustedExecutablePaths {
    param([Parameter(Mandatory)] [string] $RepositoryRoot)

    $repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    $currentRoot = [IO.Path]::GetFullPath(
        (Get-Location).ProviderPath).TrimEnd('\')
    $gitSigner = '(^|,\s*)(CN|O)=Johannes Schindelin(,|$)'
    $dotNetSigner = '(^|,\s*)CN=\.NET(,|$)'
    $powerShellSigner = '(^|,\s*)O=Microsoft (Windows|Corporation)(,|$)'
    $paths = [ordered]@{
        GitPath = Resolve-ApexLabInstalledApplication `
            -Name 'git.exe' `
            -RepositoryRoot $repositoryRoot `
            -CurrentRoot $currentRoot `
            -AllowedSignerSubjectPattern $gitSigner
        DotNetPath = Resolve-ApexLabInstalledApplication `
            -Name 'dotnet.exe' `
            -RepositoryRoot $repositoryRoot `
            -CurrentRoot $currentRoot `
            -AllowedSignerSubjectPattern $dotNetSigner
        PowerShellPath = Join-Path $PSHOME 'powershell.exe'
    }
    foreach ($name in @($paths.Keys)) {
        $path = (Resolve-Path `
            -LiteralPath $paths[$name] `
            -ErrorAction Stop).ProviderPath
        if (![IO.File]::Exists($path) `
            -or (Test-ApexLabPathWithinRoot `
                -Path $path `
                -Root $repositoryRoot) `
            -or (Test-ApexLabPathWithinRoot `
                -Path $path `
                -Root $currentRoot)) {
            throw "A trusted validation executable is unavailable."
        }
        $paths[$name] = $path
    }
    if (!(Test-ApexLabTrustedApplicationPublisher `
        -Path $paths.PowerShellPath `
        -AllowedSignerSubjectPattern $powerShellSigner) `
        -or !(Test-ApexLabProtectedApplicationLocation `
            -Path $paths.PowerShellPath)) {
        throw "A trusted validation executable is unavailable."
    }
    return [pscustomobject]$paths
}

function Resolve-ApexLabInstalledApplication {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $CurrentRoot,
        [Parameter(Mandatory)] [string] $AllowedSignerSubjectPattern
    )

    $commands = @(Get-Command `
        -Name $Name `
        -CommandType Application `
        -All `
        -ErrorAction SilentlyContinue)
    foreach ($command in $commands) {
        try {
            $candidate = (Resolve-Path `
                -LiteralPath $command.Source `
                -ErrorAction Stop).ProviderPath
        }
        catch {
            continue
        }
        if ([IO.File]::Exists($candidate) `
            -and !(Test-ApexLabPathWithinRoot `
                -Path $candidate `
                -Root $RepositoryRoot) `
            -and !(Test-ApexLabPathWithinRoot `
                -Path $candidate `
                -Root $CurrentRoot) `
            -and (Test-ApexLabTrustedApplicationPublisher `
                -Path $candidate `
                -AllowedSignerSubjectPattern $AllowedSignerSubjectPattern) `
            -and (Test-ApexLabProtectedApplicationLocation `
                -Path $candidate)) {
            return $candidate
        }
    }
    throw "A trusted validation executable is unavailable."
}

function Test-ApexLabTrustedApplicationPublisher {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $AllowedSignerSubjectPattern
    )

    $signature = Get-AuthenticodeSignature `
        -LiteralPath $Path `
        -ErrorAction SilentlyContinue
    return $null -ne $signature `
        -and $signature.Status.ToString() -ceq 'Valid' `
        -and $null -ne $signature.SignerCertificate `
        -and $signature.SignerCertificate.Subject `
            -match $AllowedSignerSubjectPattern
}

function Test-ApexLabProtectedApplicationLocation {
    param([Parameter(Mandatory)] [string] $Path)

    $trustedOwners = @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $trustedWriters = @($trustedOwners) + @('S-1-3-0')
    $writeRights = [Security.AccessControl.FileSystemRights]::WriteData `
        -bor [Security.AccessControl.FileSystemRights]::CreateDirectories `
        -bor [Security.AccessControl.FileSystemRights]::AppendData `
        -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes `
        -bor [Security.AccessControl.FileSystemRights]::WriteAttributes `
        -bor [Security.AccessControl.FileSystemRights]::Delete `
        -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
        -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
        -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($target in @($Path, [IO.Path]::GetDirectoryName($Path))) {
        try {
            $item = Get-Item -LiteralPath $target -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                return $false
            }
            $acl = Get-Acl -LiteralPath $target -ErrorAction Stop
            $owner = $acl.GetOwner(
                [Security.Principal.SecurityIdentifier]).Value
            if ($owner -notin $trustedOwners) {
                return $false
            }
            $rules = $acl.GetAccessRules(
                $true,
                $true,
                [Security.Principal.SecurityIdentifier])
            foreach ($rule in $rules) {
                if ($rule.AccessControlType `
                    -eq [Security.AccessControl.AccessControlType]::Allow `
                    -and $rule.IdentityReference.Value -notin $trustedWriters `
                    -and ($rule.FileSystemRights -band $writeRights) -ne 0) {
                    return $false
                }
            }
        }
        catch {
            return $false
        }
    }
    return $true
}

function Test-ApexLabPathWithinRoot {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Root
    )

    $path = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $path.Equals($root, [StringComparison]::OrdinalIgnoreCase) `
        -or $path.StartsWith(
            "$root\",
            [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-ApexLabProductionProbe {
    param([Parameter(Mandatory)] $Context)

    Assert-ApexLabFileSha256 `
        -Path $Context.ReplayPath `
        -Expected $Context.ReplayHash
    [Console]::Out.WriteLine(
        'Configure F1 25 UDP v3 at 127.0.0.1:20777 and drive offline Time Trial for 30 seconds.')
    $probePath = Join-Path $Context.RunRoot 'probe.json'
    $errorPath = Join-Path $Context.RunRoot 'probe.stderr'
    $result = Invoke-ApexLabCapturedProcess `
        -FilePath $Context.ReplayPath `
        -ArgumentList @('probe', '--duration-seconds', '30') `
        -StandardOutputPath $probePath `
        -StandardErrorPath $errorPath `
        -TimeoutSeconds 45
    if ($result.ExitCode -ne 0) {
        throw "The private probe failed."
    }
    return $probePath
}

function Invoke-ApexLabProductionCapture {
    param(
        [Parameter(Mandatory)] $Context,
        [ValidateRange(1, 86400)]
        [int] $TimeoutSeconds = 21600,
        [AllowEmptyString()]
        [string] $ApplicationArguments = ''
    )

    Assert-ApexLabFileSha256 `
        -Path $Context.ApplicationPath `
        -Expected $Context.ApplicationHash
    $before = Get-ApexLabFinalManifestLeafNames `
        -CapturesRoot $Context.CapturesRoot
    [Console]::Out.WriteLine(
        'Arm and stop exactly one capture in ApexLab, then close ApexLab.')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Context.ApplicationPath
    $startInfo.Arguments = $ApplicationArguments
    $startInfo.UseShellExecute = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "ApexLab could not be started."
    }
    try {
        $exitCode = Wait-ApexLabOwnedInteractiveProcess `
            -Process $process `
            -TimeoutSeconds $TimeoutSeconds
        if ($exitCode -ne 0) {
            throw "ApexLab closed with a failure."
        }
    }
    finally {
        $process.Dispose()
    }
    $after = Get-ApexLabFinalManifestLeafNames `
        -CapturesRoot $Context.CapturesRoot
    return [pscustomobject][ordered]@{
        Before = @($before)
        After = @($after)
    }
}

function Invoke-ApexLabProductionPrivateValidation {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $CaptureId,
        [Parameter(Mandatory)] [string] $ProbePath
    )

    Assert-ApexLabFileSha256 `
        -Path $Context.ReplayPath `
        -Expected $Context.ReplayHash
    $outputPath = Join-Path $Context.RunRoot 'validator.json'
    $errorPath = Join-Path $Context.RunRoot 'validator.stderr'
    $result = Invoke-ApexLabCapturedProcess `
        -FilePath $Context.ReplayPath `
        -ArgumentList @(
            'validate', '--data-root', $Context.DataRoot,
            '--capture-id', $CaptureId, '--probe-report', $ProbePath) `
        -StandardOutputPath $outputPath `
        -StandardErrorPath $errorPath `
        -TimeoutSeconds 300
    if ($result.ExitCode -ne 0) {
        $exception = [InvalidOperationException]::new(
            "The private evidence validator failed.")
        $exception.Data['ApexLabDiagnosticExitCode'] = $result.ExitCode
        throw $exception
    }
    return [IO.File]::ReadAllText($outputPath)
}

function Invoke-ApexLabProductionRateGate {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] $RatePlan
    )

    Assert-ApexLabFileSha256 `
        -Path $Context.SoakPath `
        -Expected $Context.SoakHash
    $outputPath = Join-Path $Context.RunRoot 'rate-gate.stdout'
    $errorPath = Join-Path $Context.RunRoot 'rate-gate.stderr'
    $result = Invoke-ApexLabCapturedProcess `
        -FilePath $Context.PowerShellPath `
        -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            $Context.SoakPath,
            '-DatagramCount', [string]$RatePlan.DatagramCount,
            '-TargetDatagramsPerSecond', [string]$RatePlan.TargetRate,
            '-MeasuredRealPeakDatagramsPerSecond',
            [string]($RatePlan.MinimumRate / 2),
            '-MinimumObservedFractionPermille', '950',
            '-Configuration', 'Release',
            '-DotNetPath', $Context.DotNetPath) `
        -StandardOutputPath $outputPath `
        -StandardErrorPath $errorPath `
        -TimeoutSeconds $RatePlan.TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw "The measured private rate gate failed."
    }
}

function Write-ApexLabProductionSafeSummary {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] $Validation,
        [Parameter(Mandatory)] [string] $GameBuild
    )

    $head = (Invoke-ApexLabPrivateTextCommand `
        -RunRoot $Context.RunRoot `
        -FilePath $Context.GitPath `
        -Arguments @(
            '-C', $Context.RepositoryRoot, 'rev-parse', '--verify', 'HEAD')).Trim()
    if ($head -cne $Context.RepositoryHead) {
        throw "The repository changed during private validation."
    }
    if ($Context.RequireCleanWorktree) {
        $status = Invoke-ApexLabPrivateTextCommand `
            -RunRoot $Context.RunRoot `
            -FilePath $Context.GitPath `
            -Arguments @(
                '-C', $Context.RepositoryRoot, 'status', '--porcelain=v1',
                '--untracked-files=no')
        if (![string]::IsNullOrWhiteSpace($status)) {
            throw "The tracked worktree changed during private validation."
        }
    }
    $summary = New-ApexLabSafeSummary `
        -GameBuild $GameBuild `
        -AdapterId $Validation.protocolId `
        -ApplicationVersion $Context.ApplicationVersion `
        -ValidationDate ([datetime]::UtcNow.ToString(
            'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture))
    $json = $summary | ConvertTo-Json -Compress
    $temporaryPath = Join-Path $Context.PrivateRoot (
        "safe-{0}.tmp" -f [guid]::NewGuid().ToString('N'))
    $backupPath = Join-Path $Context.PrivateRoot (
        "safe-{0}.bak" -f [guid]::NewGuid().ToString('N'))
    $latestPath = Join-Path $Context.PrivateRoot 'latest-safe.json'
    [IO.File]::WriteAllText(
        $temporaryPath,
        $json,
        [Text.UTF8Encoding]::new($false))
    try {
        if ([IO.File]::Exists($latestPath)) {
            [IO.File]::Replace($temporaryPath, $latestPath, $backupPath)
        }
        else {
            [IO.File]::Move($temporaryPath, $latestPath)
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        if ([IO.File]::Exists($backupPath)) {
            [IO.File]::Delete($backupPath)
        }
    }
    return $summary
}

function Invoke-ApexLabPrivateTextCommand {
    param(
        [Parameter(Mandatory)] [string] $RunRoot,
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $token = [guid]::NewGuid().ToString('N')
    $outputPath = Join-Path $RunRoot ("command-{0}.stdout" -f $token)
    $errorPath = Join-Path $RunRoot ("command-{0}.stderr" -f $token)
    $result = Invoke-ApexLabCapturedProcess `
        -FilePath $FilePath `
        -ArgumentList $Arguments `
        -StandardOutputPath $outputPath `
        -StandardErrorPath $errorPath
    if ($result.ExitCode -ne 0) {
        throw "A private validation child process failed."
    }
    return [IO.File]::ReadAllText($outputPath)
}

function Get-ApexLabFileSha256 {
    param([Parameter(Mandatory)] [string] $Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Assert-ApexLabFileSha256 {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Expected
    )

    if ($Expected -notmatch '^[0-9a-f]{64}$' `
        -or (Get-ApexLabFileSha256 -Path $Path) -cne $Expected) {
        throw "A verified validation file changed after preflight."
    }
}

function Get-ApexLabFinalManifestLeafNames {
    param([Parameter(Mandatory)] [string] $CapturesRoot)

    if (![IO.Directory]::Exists($CapturesRoot)) {
        return @()
    }
    return @(Get-ChildItem `
        -LiteralPath $CapturesRoot `
        -File `
        -Filter '*.apxraw.json' | ForEach-Object { $_.Name })
}

function Assert-ApexLabLoopbackPortAvailable {
    $client = [Net.Sockets.UdpClient]::new()
    try {
        $client.Client.ExclusiveAddressUse = $true
        $client.Client.Bind([Net.IPEndPoint]::new(
            [Net.IPAddress]::Loopback,
            20777))
    }
    catch {
        throw "UDP loopback port 20777 is unavailable."
    }
    finally {
        $client.Dispose()
    }
}

function Assert-ApexLabNoReparsePath {
    param(
        [Parameter(Mandatory)] [string] $Anchor,
        [Parameter(Mandatory)] [string] $Target
    )

    $anchor = [IO.Path]::GetFullPath($Anchor).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
    if (!$target.StartsWith(
        "$anchor\",
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "The private validation directory is outside its safe root."
    }
    $current = $anchor
    $relative = $target.Substring($anchor.Length + 1)
    foreach ($part in $relative.Split('\')) {
        $current = Join-Path $current $part
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The private validation directory contains a reparse point."
        }
    }
}

function Remove-ApexLabPrivateRunRoot {
    param(
        [Parameter(Mandatory)] [string] $RunRoot,
        [Parameter(Mandatory)] [string] $PrivateRoot
    )

    $privateRoot = [IO.Path]::GetFullPath($PrivateRoot).TrimEnd('\')
    $runRoot = [IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($runRoot) -cne $privateRoot `
        -or [IO.Path]::GetFileName($runRoot) -notmatch '^run-[0-9a-f]{32}$') {
        throw "The private validation cleanup target is unsafe."
    }
    if ([IO.Directory]::Exists($runRoot)) {
        Assert-ApexLabNoReparsePath -Anchor $privateRoot -Target $runRoot
        Assert-ApexLabRunTreeContainsNoReparsePoint -RunRoot $runRoot
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}

function Assert-ApexLabRunTreeContainsNoReparsePoint {
    param([Parameter(Mandatory)] [string] $RunRoot)

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($RunRoot)
    while ($pending.Count -ne 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The private validation run tree contains a reparse point."
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
}

function Invoke-ApexLabPrivateValidationStage {
    param(
        [Parameter(Mandatory)]
        [string] $Stage,

        [Parameter(Mandatory)]
        [scriptblock] $Action,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]] $Arguments
    )

    try {
        return & $Action @Arguments
    }
    catch {
        $exception = [InvalidOperationException]::new(
            "The ApexLab private validation stage failed.")
        $exception.Data['ApexLabStage'] = $Stage
        if ($_.Exception -is [OperationCanceledException]) {
            $exception.Data['ApexLabExitCode'] =
                Get-ApexLabPrivateValidationExitCode -Stage 'cancelled'
            $exception.Data['ApexLabCorrection'] =
                Get-ApexLabPrivateValidationCorrection -Stage 'cancelled'
        }
        else {
            $exception.Data['ApexLabExitCode'] =
                Get-ApexLabPrivateValidationExitCode -Stage $Stage
            $exception.Data['ApexLabCorrection'] =
                Get-ApexLabPrivateValidationCorrection -Stage $Stage
        }
        if ($_.Exception.Data.Contains('ApexLabDiagnosticExitCode')) {
            $exception.Data['ApexLabDiagnosticExitCode'] =
                $_.Exception.Data['ApexLabDiagnosticExitCode']
        }
        throw $exception
    }
}

function Get-ApexLabPrivateValidationCorrection {
    param(
        [Parameter(Mandatory)]
        [string] $Stage
    )

    $corrections = @{
        preflight = 'Resolve the preflight requirement, then run the command again.'
        probe = 'Check the F1 25 UDP settings, then repeat the probe.'
        probeEvaluation = 'Drive offline with base F1 25 UDP v3 traffic, then retry.'
        capture = 'Complete one capture and close ApexLab, then retry.'
        captureSelection = 'Keep exactly one new completed capture, then retry.'
        privateValidation = 'Create a new complete capture after a passing probe.'
        rateGate = 'Close other heavy applications, then repeat the validation.'
        safeSummary = 'Keep the repository unchanged and retry summary creation.'
        cancelled = 'The validation was cancelled; run it again when ready.'
    }
    if (!$corrections.ContainsKey($Stage)) {
        throw "The private validation stage is invalid."
    }
    return [string]$corrections[$Stage]
}

function Assert-ApexLabSafeValidatorJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Json
    )

    try {
        $value = $Json | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The private validator result is malformed."
    }

    if ($null -eq $value -or $value -isnot [pscustomobject]) {
        throw "The private validator result is malformed."
    }

    $expected = @(
        'schemaVersion',
        'status',
        'protocolId',
        'manifestIntegrity',
        'deterministicReplay',
        'sequenceGapPreservation',
        'zeroPrivacyExcludedEvidence',
        'probeAssumptions')
    $actual = @($value.PSObject.Properties.Name)
    if ($actual.Count -ne $expected.Count) {
        throw "The private validator result has an unsafe property set."
    }
    foreach ($propertyName in $expected) {
        if ($actual -cnotcontains $propertyName) {
            throw "The private validator result has an unsafe property set."
        }
    }

    if ($value.schemaVersion -isnot [int] `
        -or $value.schemaVersion -ne 1 `
        -or $value.status -cne 'validated' `
        -or $value.protocolId -cne 'ea-f1-25-v3') {
        throw "The private validator result did not pass its identity contract."
    }

    foreach ($propertyName in $expected[3..($expected.Count - 1)]) {
        if ($value.$propertyName -isnot [bool] -or !$value.$propertyName) {
            throw "The private validator result contains a failed conclusion."
        }
    }

    return $value
}

function New-ApexLabSafeSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $GameBuild,

        [Parameter(Mandatory)]
        [string] $AdapterId,

        [Parameter(Mandatory)]
        [string] $ApplicationVersion,

        [Parameter(Mandatory)]
        [string] $ValidationDate
    )

    Assert-ApexLabGameBuild -GameBuild $GameBuild
    if ($AdapterId -cne 'ea-f1-25-v3' `
        -or $ApplicationVersion -notmatch '^\d+\.\d+\.\d+$' `
        -or $ValidationDate -notmatch '^\d{4}-\d{2}-\d{2}$') {
        throw "The safe validation summary identity is invalid."
    }

    $parsedDate = [datetime]::MinValue
    if (![datetime]::TryParseExact(
        $ValidationDate,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$parsedDate)) {
        throw "The safe validation summary date is invalid."
    }

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'passed'
        validationDate = $ValidationDate
        gameBuild = $GameBuild
        adapterId = $AdapterId
        applicationVersion = $ApplicationVersion
        offlineCapture = $true
        manifestIntegrity = $true
        deterministicReplay = $true
        sequenceGapPreservation = $true
        zeroPrivacyExcludedEvidence = $true
        probeAssumptions = $true
        rateGate2x = $true
        conclusion = 'PASS'
    }
}

function Invoke-ApexLabCapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]] $ArgumentList,

        [Parameter(Mandatory)]
        [string] $StandardOutputPath,

        [Parameter(Mandatory)]
        [string] $StandardErrorPath,

        [ValidateRange(1, 3600)]
        [int] $TimeoutSeconds = 900
    )

    if ([string]::IsNullOrWhiteSpace($FilePath) `
        -or ![IO.Path]::IsPathRooted($StandardOutputPath) `
        -or ![IO.Path]::IsPathRooted($StandardErrorPath)) {
        throw "The captured process paths are invalid."
    }

    $resolvedOutput = [IO.Path]::GetFullPath($StandardOutputPath)
    $resolvedError = [IO.Path]::GetFullPath($StandardErrorPath)
    if ($resolvedOutput -eq $resolvedError `
        -or [IO.File]::Exists($resolvedOutput) `
        -or [IO.File]::Exists($resolvedError) `
        -or ![IO.Directory]::Exists([IO.Path]::GetDirectoryName($resolvedOutput)) `
        -or ![IO.Directory]::Exists([IO.Path]::GetDirectoryName($resolvedError))) {
        throw "The captured process output targets are invalid."
    }

    # PowerShell 5.1 turns redirected native stderr into ErrorRecord text when
    # ErrorActionPreference is Stop. ProcessStartInfo preserves raw streams;
    # this quoting implements the documented Windows CreateProcess rules.
    $commandLine = (($ArgumentList | ForEach-Object {
        ConvertTo-ApexLabWindowsCommandLineArgument -Value $_
    }) -join ' ')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $commandLine
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $outputStream = $null
    $errorStream = $null
    $outputCopy = $null
    $errorCopy = $null
    $started = $false
    try {
        $outputStream = [IO.File]::Open(
            $resolvedOutput,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        $errorStream = [IO.File]::Open(
            $resolvedError,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        if (!$process.Start()) {
            throw "The captured process could not be started."
        }
        $started = $true

        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync(
            $outputStream)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync(
            $errorStream)
        $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (!$process.WaitForExit(100)) {
            if ([datetime]::UtcNow -ge $deadline) {
                throw [TimeoutException]::new(
                    "The captured process exceeded its bounded wait.")
            }
        }
        $process.WaitForExit()
        [Threading.Tasks.Task]::WaitAll(
            [Threading.Tasks.Task[]]@($outputCopy, $errorCopy))
        $exitCode = $process.ExitCode
    }
    finally {
        if ($started -and !$process.HasExited) {
            Stop-ApexLabOwnedProcessTree -Process $process
        }
        if ($null -ne $outputCopy -and $null -ne $errorCopy) {
            try {
                [void][Threading.Tasks.Task]::WaitAll(
                    [Threading.Tasks.Task[]]@($outputCopy, $errorCopy),
                    5000)
            }
            catch {
                # The primary stage failure remains authoritative.
            }
        }
        if ($null -ne $outputStream) { $outputStream.Dispose() }
        if ($null -ne $errorStream) { $errorStream.Dispose() }
        $process.Dispose()
    }

    return [pscustomobject][ordered]@{
        ExitCode = [int]$exitCode
        StandardOutputPath = $resolvedOutput
        StandardErrorPath = $resolvedError
    }
}

function Wait-ApexLabOwnedInteractiveProcess {
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process] $Process,

        [ValidateRange(1, 86400)]
        [int] $TimeoutSeconds = 21600
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    try {
        while (!$Process.WaitForExit(100)) {
            if ([datetime]::UtcNow -ge $deadline) {
                throw [TimeoutException]::new(
                    "The interactive process exceeded its bounded wait.")
            }
        }
        $Process.WaitForExit()
        return [int]$Process.ExitCode
    }
    finally {
        if (!$Process.HasExited) {
            Stop-ApexLabOwnedProcess -Process $Process
        }
    }
}

function Stop-ApexLabOwnedProcess {
    param([Parameter(Mandatory)] [Diagnostics.Process] $Process)

    try {
        if (!$Process.HasExited) {
            $Process.Kill()
        }
        if (!$Process.WaitForExit(5000)) {
            throw "The owned process did not terminate within its bounded wait."
        }
    }
    catch [InvalidOperationException] {
        # The exact owned process exited between the state check and termination.
    }
    if (!$Process.HasExited) {
        throw "The owned process did not terminate."
    }
}

function Stop-ApexLabOwnedProcessTree {
    param([Parameter(Mandatory)] [Diagnostics.Process] $Process)

    $processId = $Process.Id
    $children = @(Get-CimInstance `
        -ClassName Win32_Process `
        -Filter ("ParentProcessId = {0}" -f $processId) `
        -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        try {
            $childProcess = [Diagnostics.Process]::GetProcessById(
                [int]$child.ProcessId)
            try {
                Stop-ApexLabOwnedProcessTree -Process $childProcess
            }
            finally {
                $childProcess.Dispose()
            }
        }
        catch [ArgumentException] {
            # The child exited between enumeration and termination.
        }
    }
    try {
        if (!$Process.HasExited) {
            $Process.Kill()
            [void]$Process.WaitForExit(5000)
        }
    }
    catch [InvalidOperationException] {
        # The process exited between the state check and termination.
    }
}

function Read-ApexLabPrivateProbePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (![IO.Path]::IsPathRooted($Path)) {
        throw "The private probe report path is invalid."
    }
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (![IO.File]::Exists($resolvedPath)) {
        throw "The private probe report is unavailable."
    }
    $file = [IO.FileInfo]::new($resolvedPath)
    if ($file.Length -lt 1 -or $file.Length -gt 1MB) {
        throw "The private probe report size is invalid."
    }

    try {
        $report = [IO.File]::ReadAllText($resolvedPath) |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The private probe report is malformed."
    }
    Assert-ApexLabExactProperties -Value $report -Expected @(
        'schemaVersion', 'status', 'protocolId', 'durationMilliseconds',
        'source', 'classification', 'packetShapes', 'descriptors',
        'rateBuckets', 'sequence', 'headers', 'playerIndices')
    if ($report.schemaVersion -ne 1 `
        -or $report.status -cne 'success' `
        -or $report.protocolId -cne 'ea-f1-25-v3' `
        -or !(Test-ApexLabNonnegativeInteger $report.durationMilliseconds) `
        -or $report.durationMilliseconds -le 0) {
        throw "The private probe report identity is invalid."
    }

    Assert-ApexLabExactProperties -Value $report.source -Expected @(
        'datagramsObserved', 'sourceEnqueued', 'sourceDroppedFull',
        'sourceRejectedOversized', 'socketErrors')
    Assert-ApexLabExactProperties -Value $report.classification -Expected @(
        'sourceDequeued', 'compatible', 'malformedHeader',
        'unsupportedFormat', 'unsupportedYear', 'unknownPacketId',
        'unsupportedPacketVersion', 'invalidPacketLength',
        'excludedPrivacyPacket', 'unexpectedSender',
        'classifierAbandonedOnTermination')
    foreach ($property in $report.source.PSObject.Properties) {
        if (!(Test-ApexLabNonnegativeInteger $property.Value)) {
            throw "The private probe source counters are invalid."
        }
    }
    foreach ($property in $report.classification.PSObject.Properties) {
        if (!(Test-ApexLabNonnegativeInteger $property.Value)) {
            throw "The private probe classifier counters are invalid."
        }
    }

    $sourceTotal = [decimal]$report.source.sourceEnqueued `
        + [decimal]$report.source.sourceDroppedFull `
        + [decimal]$report.source.sourceRejectedOversized
    $classifiedTotal = [decimal]$report.classification.compatible `
        + [decimal]$report.classification.malformedHeader `
        + [decimal]$report.classification.unsupportedFormat `
        + [decimal]$report.classification.unsupportedYear `
        + [decimal]$report.classification.unknownPacketId `
        + [decimal]$report.classification.unsupportedPacketVersion `
        + [decimal]$report.classification.invalidPacketLength `
        + [decimal]$report.classification.excludedPrivacyPacket `
        + [decimal]$report.classification.unexpectedSender
    if ([decimal]$report.source.datagramsObserved -ne $sourceTotal `
        -or [decimal]$report.classification.sourceDequeued -ne $classifiedTotal `
        -or [decimal]$report.source.sourceEnqueued -ne (
            [decimal]$report.classification.sourceDequeued `
            + [decimal]$report.classification.classifierAbandonedOnTermination)) {
        throw "The private probe accounting is invalid."
    }
    $forbiddenCounters = @(
        $report.source.sourceDroppedFull,
        $report.source.sourceRejectedOversized,
        $report.source.socketErrors,
        $report.classification.malformedHeader,
        $report.classification.unsupportedFormat,
        $report.classification.unsupportedYear,
        $report.classification.unknownPacketId,
        $report.classification.unsupportedPacketVersion,
        $report.classification.invalidPacketLength,
        $report.classification.unexpectedSender,
        $report.classification.classifierAbandonedOnTermination)
    if ($report.classification.compatible -le 0 `
        -or @($forbiddenCounters | Where-Object { $_ -ne 0 }).Count -ne 0) {
        throw "The private probe contains rejected traffic."
    }

    Assert-ApexLabExactProperties -Value $report.sequence -Expected @(
        'gaps', 'regressions', 'monotonicTimestampRegressions',
        'utcTimestampRegressions')
    Assert-ApexLabExactProperties -Value $report.headers -Expected @(
        'sessionUidCardinality', 'sessionTimeRegressions',
        'frameSkippedIdentifierValues', 'frameRegressions',
        'overallFrameSkippedIdentifierValues', 'overallFrameRegressions')
    foreach ($property in @(
        $report.sequence.PSObject.Properties
        $report.headers.PSObject.Properties)) {
        if (!(Test-ApexLabNonnegativeInteger $property.Value)) {
            throw "The private probe regression aggregates are invalid."
        }
    }
    if ($report.sequence.regressions -ne 0 `
        -or $report.sequence.monotonicTimestampRegressions -ne 0 `
        -or $report.sequence.utcTimestampRegressions -ne 0 `
        -or $report.headers.sessionTimeRegressions -ne 0 `
        -or $report.headers.frameRegressions -ne 0 `
        -or $report.headers.overallFrameRegressions -ne 0) {
        throw "The private probe contains a regression."
    }

    $catalog = @{
        0 = @(1, 1349); 1 = @(1, 753); 2 = @(1, 1285); 3 = @(1, 45)
        4 = @(1, 1284); 5 = @(1, 1133); 6 = @(1, 1352); 7 = @(1, 1239)
        8 = @(1, 1042); 9 = @(1, 954); 10 = @(1, 1041); 11 = @(1, 1460)
        12 = @(1, 231); 13 = @(1, 273); 14 = @(1, 101); 15 = @(1, 1131)
    }
    $shapeCounts = @{}
    foreach ($shape in @($report.packetShapes)) {
        Assert-ApexLabExactProperties -Value $shape -Expected @(
            'packetId', 'packetVersion', 'datagramLength', 'count')
        $key = Assert-ApexLabProbeDescriptor `
            -Value $shape `
            -Catalog $catalog
        if ($shapeCounts.ContainsKey($key)) {
            throw "The private probe packet shapes contain a duplicate."
        }
        $shapeCounts[$key] = [long]$shape.count
    }

    $descriptorCounts = @{}
    [decimal]$descriptorTotal = 0
    foreach ($descriptor in @($report.descriptors)) {
        Assert-ApexLabExactProperties -Value $descriptor -Expected @(
            'packetId', 'packetVersion', 'datagramLength', 'count')
        $key = Assert-ApexLabProbeDescriptor `
            -Value $descriptor `
            -Catalog $catalog
        if ($descriptorCounts.ContainsKey($key)) {
            throw "The private probe descriptors contain a duplicate."
        }
        $descriptorCounts[$key] = [long]$descriptor.count
        $descriptorTotal += [decimal]$descriptor.count
    }
    if ($descriptorTotal -ne [decimal]$report.classification.sourceDequeued `
        -or $shapeCounts.Count -ne $descriptorCounts.Count) {
        throw "The private probe descriptor accounting is invalid."
    }
    foreach ($key in $descriptorCounts.Keys) {
        if (!$shapeCounts.ContainsKey($key) `
            -or $shapeCounts[$key] -ne $descriptorCounts[$key]) {
            throw "The private probe descriptor assumptions are invalid."
        }
    }

    $offsets = [Collections.Generic.HashSet[long]]::new()
    [decimal]$rateTotal = 0
    [long]$peak = 0
    foreach ($bucket in @($report.rateBuckets)) {
        Assert-ApexLabExactProperties -Value $bucket -Expected @(
            'offsetSeconds', 'count')
        if (!(Test-ApexLabNonnegativeInteger $bucket.offsetSeconds) `
            -or !(Test-ApexLabNonnegativeInteger $bucket.count) `
            -or $bucket.count -le 0 `
            -or !$offsets.Add([long]$bucket.offsetSeconds)) {
            throw "The private probe rate buckets are invalid."
        }
        $rateTotal += [decimal]$bucket.count
        $peak = [Math]::Max($peak, [long]$bucket.count)
    }
    if ($peak -lt 1 -or $peak -gt 37878 `
        -or $rateTotal -ne [decimal]$report.classification.sourceDequeued) {
        throw "The private probe peak is invalid."
    }

    Assert-ApexLabExactProperties -Value $report.playerIndices -Expected @(
        'playerMinimum', 'playerMaximum', 'secondaryMinimum',
        'secondaryMaximum', 'secondaryAbsentCount')
    if (!(Test-ApexLabNonnegativeInteger $report.playerIndices.secondaryAbsentCount) `
        -or !(Test-ApexLabNullableByteRange `
            -Minimum $report.playerIndices.playerMinimum `
            -Maximum $report.playerIndices.playerMaximum) `
        -or !(Test-ApexLabNullableByteRange `
            -Minimum $report.playerIndices.secondaryMinimum `
            -Maximum $report.playerIndices.secondaryMaximum)) {
        throw "The private probe player-index aggregates are invalid."
    }

    return [pscustomobject][ordered]@{
        ProtocolId = [string]$report.protocolId
        MeasuredPeakDatagramsPerSecond = [int]$peak
    }
}

function Assert-ApexLabExactProperties {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [string[]] $Expected
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "The private JSON object is malformed."
    }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "The private JSON object has an unexpected property set."
    }
    foreach ($propertyName in $Expected) {
        if ($actual -cnotcontains $propertyName) {
            throw "The private JSON object has an unexpected property set."
        }
    }
}

function Test-ApexLabNonnegativeInteger {
    param($Value)

    return ($Value -is [int] -or $Value -is [long]) -and $Value -ge 0
}

function Test-ApexLabNullableByteRange {
    param($Minimum, $Maximum)

    if ($null -eq $Minimum -or $null -eq $Maximum) {
        return $null -eq $Minimum -and $null -eq $Maximum
    }
    return (Test-ApexLabNonnegativeInteger $Minimum) `
        -and (Test-ApexLabNonnegativeInteger $Maximum) `
        -and $Minimum -le 255 `
        -and $Maximum -le 255 `
        -and $Minimum -le $Maximum
}

function Assert-ApexLabProbeDescriptor {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [hashtable] $Catalog
    )

    if (!(Test-ApexLabNonnegativeInteger $Value.packetId) `
        -or !(Test-ApexLabNonnegativeInteger $Value.packetVersion) `
        -or !(Test-ApexLabNonnegativeInteger $Value.datagramLength) `
        -or !(Test-ApexLabNonnegativeInteger $Value.count) `
        -or $Value.count -le 0 `
        -or !$Catalog.ContainsKey([int]$Value.packetId)) {
        throw "The private probe descriptor is invalid."
    }
    $catalogValue = $Catalog[[int]$Value.packetId]
    if ($Value.packetVersion -ne $catalogValue[0] `
        -or $Value.datagramLength -ne $catalogValue[1]) {
        throw "The private probe descriptor disagrees with the base-v3 catalog."
    }
    return "$($Value.packetId)/$($Value.packetVersion)/$($Value.datagramLength)"
}

function ConvertTo-ApexLabWindowsCommandLineArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value
    )

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
        }
        else {
            [void]$builder.Append(('\' * $backslashes))
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }

    [void]$builder.Append(('\' * ($backslashes * 2)))
    [void]$builder.Append('"')
    return $builder.ToString()
}

Export-ModuleMember -Function @(
    'Assert-ApexLabGameBuild',
    'Select-ApexLabNewCaptureId',
    'Get-ApexLabRateGatePlan',
    'Get-ApexLabPrivateValidationExitCode',
    'Invoke-ApexLabPrivateF125Validation',
    'Assert-ApexLabSafeValidatorJson',
    'New-ApexLabSafeSummary',
    'Invoke-ApexLabCapturedProcess',
    'Read-ApexLabPrivateProbePlan'
)
