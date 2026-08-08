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
    if ($targetRate -gt 100000 `
        -or $datagramCount -gt 5000000) {
        throw "The measured peak cannot produce a feasible private rate gate."
    }

    return [pscustomobject][ordered]@{
        MinimumRate = $minimumRate
        TargetRate = $targetRate
        DatagramCount = [int]$datagramCount
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
        [string[]] $ArgumentList,

        [Parameter(Mandatory)]
        [string] $StandardOutputPath,

        [Parameter(Mandatory)]
        [string] $StandardErrorPath
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

        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync(
            $outputStream)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync(
            $errorStream)
        $process.WaitForExit()
        [Threading.Tasks.Task]::WaitAll(
            [Threading.Tasks.Task[]]@($outputCopy, $errorCopy))
        $exitCode = $process.ExitCode
    }
    finally {
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
    if (!(Test-ApexLabNonnegativeInteger $report.playerIndices.secondaryAbsentCount)) {
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
    'Assert-ApexLabSafeValidatorJson',
    'New-ApexLabSafeSummary',
    'Invoke-ApexLabCapturedProcess',
    'Read-ApexLabPrivateProbePlan'
)
