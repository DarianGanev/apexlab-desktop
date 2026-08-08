# Private F1 25 Validation Assistant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one privacy-safe PowerShell command that guides the owner through the real F1 25 probe and capture gate, validates the new evidence twice through the production pipeline, runs the measured 2x soak, and writes only a commit-safe result.

**Architecture:** Keep evidence semantics in `ApexLab.Replay`: factor the existing replay path into a typed engine, parse and evaluate the probe report strictly, then expose a `validate` command that runs the same capture twice. Keep interaction and process orchestration in a small PowerShell module used by one public script; inject process boundaries in tests so the complete workflow is exercised with generated synthetic evidence and no game, UI, network, or 60-second wait.

**Tech Stack:** C# 14, .NET 10 (`net10.0-windows`), MSTest, WPF release binaries, PowerShell 5.1-compatible scripts, existing ApexLab raw-evidence and F1 25 base-v3 protocol libraries.

## Global Constraints

- Work only in `C:\Users\ASUS\Desktop\diplomna\.worktrees\private-validation-assistant` on `feature/private-validation-assistant`; do not modify the release worktree.
- Preserve the existing `replay` and `probe` command syntax and JSON contracts.
- The public command remains exactly `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ValidatePrivateF125.ps1 -GameBuild "<build shown by F1 25>"`.
- Support Windows, the repository-pinned .NET SDK, base F1 25 protocol `ea-f1-25-v3`, and loopback UDP `127.0.0.1:20777` only.
- Do not add a NuGet or PowerShell dependency; in particular, do not require Pester.
- Raw evidence stays under `%LOCALAPPDATA%\ApexLab\captures`; never upload, copy, rename, alter, or delete a user capture.
- Never print or write to the safe report a capture ID, evidence hash, filesystem path, username, sender endpoint, session UID, payload, exact live peak, or exact derived rate threshold.
- Keep the probe JSON and captured child output in a random temporary directory below `%LOCALAPPDATA%\ApexLab\private-validation`; remove that directory in `finally` on success, failure, or cancellation.
- Require exactly one new finalized `*.apxraw.json` leaf name by ordinal set difference; never select by timestamp.
- Private validation must open evidence with `RawEvidenceReader`, resolve its recorded adapter, replay twice in immediate mode, and compare typed values rather than JSON text.
- The measured rate gate must derive `minimum = 2 * peak`, `target = ceiling(1.10 * minimum)`, and `datagramCount = max(10000, 60 * target)`; `CaptureSoak.ps1` remains the enforcing test runner.
- Use `apply_patch` for edits, `$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'` for .NET commands, and commit as `DarianGanev <darianganev83@gmail.com>`.
- Every production change follows red-green-refactor and ends in a focused owner-authored commit.
- The feature does not pass the real private F1 gate by itself; v0.2.0 remains blocked until the owner performs the live workflow and approves its safe result.

## File Structure

| File | Responsibility |
|---|---|
| `tools/ApexLab.Replay/Replay/ReplayExecution.cs` | Open verified evidence, resolve the recorded adapter, replay once, and return a typed result. |
| `tools/ApexLab.Replay/Replay/ReplayObservationAggregator.cs` | Record stable sequence-gap and descriptor-shape aggregates during replay. |
| `tools/ApexLab.Replay/Replay/ReplayCommand.cs` | Preserve the existing public replay JSON by adapting the typed engine result. |
| `tools/ApexLab.Replay/Validation/PrivateValidationArguments.cs` | Parse the exact private `validate` command arguments. |
| `tools/ApexLab.Replay/Validation/PrivateProbeEvidence.cs` | Strictly deserialize and evaluate schema-v1 probe JSON against base-v3 invariants. |
| `tools/ApexLab.Replay/Validation/PrivateValidationCommand.cs` | Run probe evaluation and two typed replays, enforce evidence/privacy/determinism rules, and return safe JSON. |
| `tools/ApexLab.Replay/Program.cs` | Route `validate`, `replay`, and `probe` without changing old routes. |
| `scripts/PrivateF125Validation.psm1` | Implement testable calculations, set selection, safe-report validation, process capture, and workflow orchestration. |
| `scripts/ValidatePrivateF125.ps1` | Validate the user-facing input, load the module, create production dependencies, run the workflow, and expose its exit code. |
| `tests/ApexLab.IntegrationTests/Replay/ReplayExecutionTests.cs` | Verify typed replay accounting, descriptor aggregation, sequence gaps, and resource cleanup. |
| `tests/ApexLab.IntegrationTests/Validation/PrivateProbeEvidenceTests.cs` | Exercise every accepted and rejected probe invariant. |
| `tests/ApexLab.IntegrationTests/Validation/PrivateValidationCommandTests.cs` | Verify argument/exit mapping, two-replay comparison, evidence failures, privacy, and cancellation. |
| `tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs` | Launch Windows PowerShell to test module functions, workflow ordering, argument arrays, cleanup, and safe output. |
| `tests/ApexLab.IntegrationTests/Validation/Fixtures/InvokePrivateF125SyntheticWorkflow.ps1` | Drive the module with fake process boundaries and a generated evidence fixture. |
| `README.md` | Document the one-command owner workflow and local-only boundary. |
| `docs/verification/v0.2.0-private-f125.md` | Provide the reviewed commit-safe result schema and live-run checklist without private measurements. |

---

### Task 1: Typed single-replay engine

**Files:**
- Create: `tools/ApexLab.Replay/Replay/ReplayExecution.cs`
- Create: `tools/ApexLab.Replay/Replay/ReplayObservationAggregator.cs`
- Modify: `tools/ApexLab.Replay/Replay/ReplayCommand.cs`
- Test: `tests/ApexLab.IntegrationTests/Replay/ReplayExecutionTests.cs`
- Test: `tests/ApexLab.IntegrationTests/Replay/ReplayCommandTests.cs`

**Interfaces:**
- Consumes: `ApplicationPaths`, `RawEvidenceCaptureId`, `RawReplayOptions`, `RawEvidenceReader`, `ReplayProtocolRegistry`, `RawReplayDatagramSource`, and `CaptureIngestionCoordinator`.
- Produces: `ReplayExecution.ExecuteAsync(ApplicationPaths, RawEvidenceCaptureId, RawReplayOptions, CancellationToken)`, returning `ReplayExecutionResult(string ProtocolId, RawReplayTimingMode TimingMode, int SpeedPermille, long RecordCount, CaptureIngestionCounters Counters, long SequenceGapCount, IReadOnlyList<ReplayDescriptorObservation> Descriptors)`.
- Produces: `ReplayUnsupportedProtocolException`, used by both command layers without revealing the recorded protocol string.

- [ ] **Step 1: Write failing typed-engine tests**

Create generated evidence with sequences `1, 2, 5`, valid F1 base-v3 Motion packets, and loopback senders. Assert the typed result has `RecordCount == 3`, complete source accounting, `SequenceGapCount == 2`, and one descriptor observation with count three. Add an empty-evidence case and a cancellation case that immediately reopens/deletes the evidence after the command returns, proving all handles were released.

```csharp
var result = await ReplayExecution.ExecuteAsync(
    temporary.Paths,
    completion.CaptureId,
    new RawReplayOptions(RawReplayTimingMode.Immediate),
    TestContext.CancellationToken);

Assert.AreEqual(3L, result.RecordCount);
Assert.AreEqual(2L, result.SequenceGapCount);
Assert.AreEqual(result.RecordCount, result.Counters.Source.DatagramsObserved);
Assert.AreEqual(result.RecordCount, result.Counters.Classifier.SourceDequeued);
Assert.AreEqual(3L, Assert.Single(result.Descriptors).Count);
```

- [ ] **Step 2: Run the focused tests and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~ReplayExecutionTests" --nologo
```

Expected: compilation fails because `ReplayExecution` and its typed result do not exist.

- [ ] **Step 3: Implement the observer and typed engine**

`ReplayObservationAggregator.Observe` must reject non-increasing sequences defensively and calculate gaps with checked arithmetic:

```csharp
if (_lastSequence is { } previous)
{
    if (observation.Sequence <= previous)
    {
        throw new InvalidDataException("Replay evidence sequence order is invalid.");
    }

    _sequenceGapCount = checked(
        _sequenceGapCount + observation.Sequence - previous - 1);
}
```

Aggregate descriptor keys as `(PacketId, PacketVersion, DatagramLength)` and return them in numeric sorted order. Move evidence opening, adapter resolution, source construction, coordinator execution, and disposal from `ReplayCommand` into `ReplayExecution`. Transfer ownership of each opened `RawEvidenceCapture` exactly once; retain the current `finally` cleanup behavior before ownership transfer.

- [ ] **Step 4: Adapt the old replay command without changing its JSON contract**

Keep `ReplayArguments`, `ReplayReport`, `ReplaySourceReport`, and `ReplayClassificationReport` unchanged. Map the typed engine result into the existing report and map `ReplayUnsupportedProtocolException` to `ReplayExitCode.UnsupportedProtocol`. Add an assertion in `ReplayCommandTests` that the property-name sequence is still exactly:

```csharp
string[] expected =
[
    "schemaVersion", "status", "protocolId", "timing",
    "speedPermille", "recordCount", "source", "classification",
];
CollectionAssert.AreEqual(
    expected,
    document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
```

- [ ] **Step 5: Run replay tests and the non-soak suite**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Replay" --nologo
dotnet test ApexLab.slnx -c Release --no-restore --filter "TestCategory!=Soak" --nologo
```

Expected: all replay tests and all non-soak tests pass; the old replay JSON remains byte-shape compatible apart from ordinary indentation.

- [ ] **Step 6: Commit the typed replay engine**

```powershell
git add tools/ApexLab.Replay/Replay tests/ApexLab.IntegrationTests/Replay
git commit -m "refactor(replay): expose typed deterministic execution"
```

---

### Task 2: Strict private probe evidence evaluation

**Files:**
- Create: `tools/ApexLab.Replay/Validation/PrivateProbeEvidence.cs`
- Test: `tests/ApexLab.IntegrationTests/Validation/PrivateProbeEvidenceTests.cs`

**Interfaces:**
- Consumes: existing `ProbeReport` schema records, `ProbeAggregator.SchemaVersion`, `F125Protocol.Id`, and `F125PacketDescriptorCatalog.Descriptors`.
- Produces: `PrivateProbeEvidence.ReadAndEvaluateAsync(string absolutePath, CancellationToken)` returning `PrivateProbeEvaluation(string ProtocolId, int MeasuredPeakDatagramsPerSecond, IReadOnlySet<PrivateDescriptorShape> ObservedDescriptors)`.
- Produces: `PrivateProbeFailureException(PrivateProbeFailureKind Kind)` where kinds are `Malformed`, `UnsupportedSchema`, `ProtocolMismatch`, `AccountingMismatch`, `RejectedTraffic`, `Regression`, `DescriptorMismatch`, and `NoCompatibleTraffic`.

- [ ] **Step 1: Write the accepted-report test and one test per failure kind**

Build JSON through `ProbeJson.Serialize` so property names match production. The accepted report must satisfy these exact equations:

```text
source.datagramsObserved = source.sourceEnqueued + source.sourceDroppedFull + source.sourceRejectedOversized
source.sourceEnqueued = classification.sourceDequeued + classification.classifierAbandonedOnTermination
classification.sourceDequeued = sum(all classification outcome counters except sourceDequeued and classifierAbandonedOnTermination)
```

Assert the peak is the maximum bucket, not an average or the last bucket:

```csharp
Assert.AreEqual(71, evaluation.MeasuredPeakDatagramsPerSecond);
```

Use data rows that independently alter each forbidden source/classifier counter, each regression counter, schema, status, protocol, empty rate buckets, zero compatible count, duplicate bucket offset, negative count, and descriptor packet/version/length. Assert each maps to its named failure kind and no exception message contains the source path or JSON.

- [ ] **Step 2: Run the probe-evidence tests and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateProbeEvidenceTests" --nologo
```

Expected: compilation fails because the validation namespace and evaluator do not exist.

- [ ] **Step 3: Implement strict parsing**

Read at most 1 MiB, require an absolute leaf file path, and deserialize with camel-case naming, case-sensitive property matching, no comments, no trailing commas, and `JsonUnmappedMemberHandling.Disallow`. Reject null collections, duplicate descriptor keys, duplicate bucket offsets, negative counts, and arithmetic overflow. Do not retain or return the input path.

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
};
```

- [ ] **Step 4: Implement exact acceptance rules**

Require schema 1, status `success`, protocol `ea-f1-25-v3`, positive compatible traffic, positive peak, zero source loss/errors, zero malformed/unsupported/unknown/invalid/unexpected classifications, and zero sequence/timestamp/session/frame regressions. Permit `ExcludedPrivacyPacket > 0` in the live probe only. For every packet shape and descriptor, resolve the packet ID in `F125PacketDescriptorCatalog` and require exact packet version and datagram length. Require descriptor counts to equal packet-shape counts per key and require the total descriptor count to equal `SourceDequeued`.

- [ ] **Step 5: Run focused and probe regression tests**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateProbeEvidenceTests|FullyQualifiedName~Probe" --nologo
```

Expected: all private evaluator and existing probe tests pass.

- [ ] **Step 6: Commit probe evaluation**

```powershell
git add tools/ApexLab.Replay/Validation/PrivateProbeEvidence.cs tests/ApexLab.IntegrationTests/Validation/PrivateProbeEvidenceTests.cs
git commit -m "feat(validation): evaluate private F1 probe evidence"
```

---

### Task 3: Typed two-replay private validation command

**Files:**
- Create: `tools/ApexLab.Replay/Validation/PrivateValidationArguments.cs`
- Create: `tools/ApexLab.Replay/Validation/PrivateValidationCommand.cs`
- Modify: `tools/ApexLab.Replay/Program.cs`
- Test: `tests/ApexLab.IntegrationTests/Validation/PrivateValidationCommandTests.cs`

**Interfaces:**
- Consumes: `ReplayExecution.ExecuteAsync` and `PrivateProbeEvidence.ReadAndEvaluateAsync`.
- Produces: `PrivateValidationArguments(string DataRoot, RawEvidenceCaptureId CaptureId, string ProbeReportPath).TryParse(...)` for exactly `validate --data-root <absolute-root> --capture-id <canonical-id> --probe-report <absolute-json>`.
- Produces: `PrivateValidationCommand.ExecuteAsync(IReadOnlyList<string>, CancellationToken)` returning `PrivateValidationCommandResult(PrivateValidationExitCode ExitCode, string Json)`.
- Produces exit values: `Success=0`, `InvalidArguments=20`, `InvalidEvidence=21`, `UnsupportedProtocol=22`, `ProbeMismatch=23`, `NondeterministicReplay=24`, `PrivacyFailure=25`, `Interrupted=26`, `UnexpectedFailure=27`.

- [ ] **Step 1: Write parser, routing, and exit-code tests**

Accept only all three required options once, reject relative roots/reports, noncanonical capture IDs, unknown options, duplicates, missing values, extra positional values, and odd argument counts. Route `validate` before the replay/probe fallback and assert existing `probe` and `replay` tests remain unchanged.

```csharp
Assert.IsTrue(PrivateValidationArguments.TryParse(
    ["validate", "--data-root", root, "--capture-id", id,
     "--probe-report", reportPath], out var parsed));
Assert.AreEqual(Path.GetFullPath(root), parsed.DataRoot);
```

- [ ] **Step 2: Write command behavior tests**

Generate valid F1 evidence with and without sequence gaps and a matching probe report. Assert success JSON has this exact allowlist and all conclusion values are `true`:

```text
schemaVersion, status, protocolId, manifestIntegrity, deterministicReplay,
sequenceGapPreservation, zeroPrivacyExcludedEvidence, probeAssumptions
```

Add cases for missing/incomplete, malformed, hash-mismatched, trailing, and unsupported evidence; probe schema/protocol/descriptor mismatch; privacy-excluded and unexpected-sender classifications; cancellation; and injected first/second typed reports differing in each stable field. Scan every returned JSON value for the capture ID, SHA-256, root, report path, sender string, session UID, and payload sentinel.

- [ ] **Step 3: Run validation command tests and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateValidationCommandTests" --nologo
```

Expected: compilation fails because the command types do not exist.

- [ ] **Step 4: Implement two typed executions and invariant checks**

Use an internal overload accepting delegates for probe evaluation and replay execution so nondeterminism and cancellation are tested without corrupting production evidence. Production calls both replays independently, reopening evidence each time. Stable equality includes protocol, timing, speed, record count, every source/classifier counter, sequence-gap count, and sorted descriptor observations.

For each replay require:

```text
recordCount = datagramsObserved = sourceEnqueued = sourceDequeued = compatible
sourceDroppedFull = sourceRejectedOversized = socketErrors = 0
all non-compatible classifier counters = 0
enqueuedAwaitingClassifier = classifierAbandonedOnTermination = 0
```

Require every replayed descriptor key to exist in the evaluated probe descriptor set. Map raw-read failures to `InvalidEvidence`, unregistered manifest protocol to `UnsupportedProtocol`, probe failures to `ProbeMismatch`, unequal typed results to `NondeterministicReplay`, any excluded/unexpected evidence to `PrivacyFailure`, owner cancellation to `Interrupted`, and all other exceptions to a generic `UnexpectedFailure` report.

- [ ] **Step 5: Serialize only the approved safe validator JSON**

All failure reports contain only `schemaVersion` and a stable `status`. The success report contains only the allowlisted aggregate conclusions. Do not serialize counts, paths, identities, hashes, rate values, exception text, or command arguments.

- [ ] **Step 6: Run focused tests and the complete non-soak suite**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateValidation|FullyQualifiedName~Replay|FullyQualifiedName~Probe" --nologo
dotnet test ApexLab.slnx -c Release --no-restore --filter "TestCategory!=Soak" --nologo
```

Expected: all focused and non-soak tests pass.

- [ ] **Step 7: Commit the private validator**

```powershell
git add tools/ApexLab.Replay/Validation tools/ApexLab.Replay/Program.cs tests/ApexLab.IntegrationTests/Validation
git commit -m "feat(validation): verify deterministic private evidence"
```

---

### Task 4: Pure PowerShell validation primitives

**Files:**
- Create: `scripts/PrivateF125Validation.psm1`
- Create: `tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs`

**Interfaces:**
- Produces: `Assert-ApexLabGameBuild([string])`, `Read-ApexLabPrivateProbePlan([string] absolutePath)`, `Select-ApexLabNewCaptureId([string[]] before, [string[]] after)`, `Get-ApexLabRateGatePlan([int] peak)`, `Assert-ApexLabSafeValidatorJson([string])`, `New-ApexLabSafeSummary(...)`, and `Invoke-ApexLabCapturedProcess([string] filePath, [string[]] argumentList, [string] stdoutPath, [string] stderrPath)`.
- Produces: `Get-ApexLabPrivateValidationExitCode`, mapping stable PowerShell stage failures to `40..49` while preserving the typed validator's `20..27` values in local diagnostic metadata only.

- [ ] **Step 1: Add a reusable PowerShell test launcher in C#**

Launch `powershell.exe` with `UseShellExecute=false`, `CreateNoWindow=true`, and `ArgumentList` entries rather than a joined command string. The helper imports the module by absolute path and writes only JSON to standard output. Assert stderr is empty on success.

- [ ] **Step 2: Write failing table tests for all pure functions**

Cover game-build empty/whitespace/control/path-separator/over-80-character input; manifest differences of zero, exactly one, and multiple; malformed and noncanonical manifest leaf names; peak `0`, `1`, `70`, and `37878`; and invalid validator JSON properties/status/conclusions. Feed `Read-ApexLabPrivateProbePlan` the accepted and rejected reports from Task 2 as JSON fixtures and require the same accounting, regression, descriptor, protocol, and positive-peak decisions before capture is allowed. For peak 70 assert only in test memory:

```text
minimum = 140
target = 154
datagramCount = 10000
```

For peak 37878 assert `minimum=75756`, `target=83332`, and `datagramCount=4999920`, which remain inside `CaptureSoak.ps1` bounds.

- [ ] **Step 3: Run the PowerShell tests and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateF125PowerShellTests" --nologo
```

Expected: tests fail because `PrivateF125Validation.psm1` is absent.

- [ ] **Step 4: Implement the pure primitives with checked bounds**

`Select-ApexLabNewCaptureId` compares ordinal leaf-name sets, requires exactly one addition matching `^[0-9a-f]{32}\.apxraw\.json$`, and returns only the 32-character ID to its caller. It must never write the ID. `Get-ApexLabRateGatePlan` uses decimal checked arithmetic and returns an in-memory object with `MinimumRate`, `TargetRate`, and `DatagramCount`; no formatting method prints it.

`Read-ApexLabPrivateProbePlan` uses `ConvertFrom-Json`, checks the exact schema-v1 property sets and integer domains, enforces the Task 2 acceptance equations, validates descriptor shapes against a literal projection of the reviewed public base-v3 catalog, and returns only an in-memory positive peak plus protocol. This deliberate pre-capture check is repeated by the typed C# validator after capture so a PowerShell parsing defect cannot pass the final gate.

`Assert-ApexLabSafeValidatorJson` requires the exact Task 3 property allowlist and boolean `true` conclusions. `New-ApexLabSafeSummary` creates only:

```text
schemaVersion, status, validationDate, gameBuild, adapterId, applicationVersion,
offlineCapture, manifestIntegrity, deterministicReplay, sequenceGapPreservation,
zeroPrivacyExcludedEvidence, probeAssumptions, rateGate2x, conclusion
```

- [ ] **Step 5: Implement safe child-process capture**

Windows PowerShell 5.1 does not expose the modern .NET `ProcessStartInfo.ArgumentList`. Initial red-green testing showed that call-operator redirection converts native stderr into formatted `NativeCommandError` records under the workflow's stop-on-error policy. The implemented boundary therefore uses `ProcessStartInfo`, a reviewed Windows command-line quoting routine, raw asynchronous stdout/stderr streams, bounded waits, and exact-owned-process termination. Adversarial tests cover empty values, quotes, trailing backslashes, apostrophes, whitespace, semicolons, dollar signs, and parentheses. Never use `Invoke-Expression`, `Start-Process -ArgumentList`, echo captured child output, or construct a command for shell evaluation.

- [ ] **Step 6: Run focused tests and PowerShell syntax validation**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateF125PowerShellTests" --nologo
powershell -NoProfile -Command "$errors=$null; [void][Management.Automation.Language.Parser]::ParseFile((Resolve-Path 'scripts/PrivateF125Validation.psm1'),[ref]$null,[ref]$errors); if($errors.Count){$errors | Out-String; exit 1}"
```

Expected: all function tests pass and the parser reports no errors.

- [ ] **Step 7: Commit the PowerShell primitives**

```powershell
git add scripts/PrivateF125Validation.psm1 tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs
git commit -m "feat(validation): add private validation workflow primitives"
```

---

### Task 5: One-command guided workflow

**Files:**
- Modify: `scripts/PrivateF125Validation.psm1`
- Create: `scripts/ValidatePrivateF125.ps1`
- Modify: `tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs`

**Interfaces:**
- Produces: `Invoke-ApexLabPrivateF125Validation([string] GameBuild, [string] RepositoryRoot, [hashtable] Dependencies)`; production dependencies perform repository inspection, child process execution, UI launch/wait, manifest enumeration, safe summary persistence, and console status emission.
- The public script accepts only mandatory `-GameBuild`; testing calls the module function directly with injected dependencies and never adds a public simulation switch.

- [ ] **Step 1: Write failing workflow-order and fail-closed tests**

Inject scriptblocks that append stage names to an in-memory list. The success order must be:

```text
preflight, probe, probeEvaluation, capture, captureSelection,
privateValidation, rateGate, safeSummary
```

For each stage, inject a failure and assert later stages are absent, the mapped exit code is nonzero, no passing summary exists, and the temporary directory is gone. Inject a cancellation exception at probe, UI wait, validator, and soak boundaries and assert cleanup with raw manifests preserved.

- [ ] **Step 2: Write failing safety/precondition tests**

Assert rejection of non-Windows execution, wrong SDK, dirty tracked worktree, detached/unresolved commit, active `ApexLab` process, occupied loopback port 20777, missing Release binaries after verification, zero/multiple new manifests, and a changed worktree commit between preflight and safe-summary creation. Ensure every console line belongs to a fixed stage-message allowlist and contains none of the private sentinels supplied by the fixture.

- [ ] **Step 3: Run workflow tests and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateF125PowerShellTests" --nologo
```

Expected: workflow tests fail because orchestration and the public script do not exist.

- [ ] **Step 4: Implement production preflight and probe stages**

Resolve repository and application-data roots with `GetFullPath`; require `%LOCALAPPDATA%`; create `%LOCALAPPDATA%\ApexLab\private-validation\run-<random>` without a reparse-point segment. Capture `git rev-parse HEAD`, require `git status --porcelain=v1 --untracked-files=no` empty, check SDK `10.0.302`, no active `ApexLab` process, and exclusive availability of loopback port 20777. Run `scripts/Verify.ps1` with captured output, then require these exact Release files:

```text
src/ApexLab.App/bin/Release/net10.0-windows/ApexLab.exe
tools/ApexLab.Replay/bin/Release/net10.0-windows/ApexLab.Replay.exe
```

Display a fixed instruction to configure F1 25 UDP on loopback and drive offline Time Trial. Run the replay executable with argument array `probe --duration-seconds 30`; require exit zero, then call `Read-ApexLabPrivateProbePlan` before launching the UI. The later typed `validate` command independently repeats this complete evaluation. Retain the bounded positive maximum rate bucket only in workflow memory for soak calculation.

- [ ] **Step 5: Implement capture, selection, validation, and soak stages**

Snapshot finalized manifest leaf names after the probe and before UI launch. Start the exact Release `ApexLab.exe`, wait for owner exit, snapshot again, and call `Select-ApexLabNewCaptureId`. Invoke:

```text
validate --data-root <LocalAppData/ApexLab> --capture-id <memory-only-id> --probe-report <private-temp/probe.json>
```

Require exit zero and validate its JSON allowlist. Calculate the rate plan and invoke `CaptureSoak.ps1` with `-DatagramCount`, `-TargetDatagramsPerSecond`, `-MeasuredRealPeakDatagramsPerSecond`, `-MinimumObservedFractionPermille 950`, and `-Configuration Release`. Capture all detailed output and never print the values or command line.

- [ ] **Step 6: Implement atomic safe summary and unconditional cleanup**

Read application version from `Version.props`, verify the repository HEAD is unchanged, create the allowlisted summary, write UTF-8 without BOM to a random file, validate it again, then atomically replace `%LOCALAPPDATA%\ApexLab\private-validation\latest-safe.json`. Print the same safe JSON and a fixed success message. In `finally`, recursively delete only the resolved `run-<random>` directory after verifying it is a direct child below the private-validation root and contains no reparse point. Never delete from `captures`.

- [ ] **Step 7: Implement the thin public script**

Use `[CmdletBinding()]`, a mandatory bounded `GameBuild`, strict mode, stop-on-error, and module import by `Join-Path $PSScriptRoot`. Expected workflow failures carry only stable `ApexLabStage`, `ApexLabExitCode`, and `ApexLabCorrection` entries in `Exception.Data`; print those allowlisted values and exit with the stable code. Any exception without that complete marker prints `unexpectedFailure` without exception text and exits 49.

- [ ] **Step 8: Run all PowerShell workflow tests**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateF125PowerShellTests" --nologo
powershell -NoProfile -Command "$errors=$null; Get-ChildItem scripts/PrivateF125Validation.psm1,scripts/ValidatePrivateF125.ps1 | ForEach-Object {[void][Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$null,[ref]$errors)}; if($errors.Count){$errors | Out-String; exit 1}"
```

Expected: all stage, failure, cleanup, output, and syntax tests pass.

- [ ] **Step 9: Commit the guided workflow**

```powershell
git add scripts/PrivateF125Validation.psm1 scripts/ValidatePrivateF125.ps1 tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs
git commit -m "feat(validation): guide the private F1 capture gate"
```

---

### Task 6: Fully synthetic end-to-end workflow test

**Files:**
- Create: `tests/ApexLab.IntegrationTests/Validation/Fixtures/InvokePrivateF125SyntheticWorkflow.ps1`
- Modify: `tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs`
- Modify: `scripts/PrivateF125Validation.psm1`

**Interfaces:**
- Consumes: the module dependency-injection contract from Task 5, an absolute generated-evidence staging root, an absolute temporary ApexLab data root, and the built `ApexLab.Replay.exe`.
- Produces: a subprocess-level test that reaches `safeSummary` using real probe JSON parsing, real finalized raw evidence, and the real typed validator while faking only Verify, UI wait, and the 60-second soak boundary.

- [ ] **Step 1: Write the failing end-to-end test**

In C#, create a valid schema-v1 probe report and finalized base-v3 evidence in a staging root using `RawEvidenceWriter`. Start the fixture script with all paths as separate `ProcessStartInfo.ArgumentList` entries. The fake UI dependency copies the already-generated `.apxraw` and `.apxraw.json` pair into the temporary data root only after the before-manifest snapshot. The validator dependency launches the actual built replay executable; the soak dependency records its argument array and returns success without waiting.

Assert exit zero, exact stage order, one safe report, no remaining run directory, unchanged staged/user evidence, and no private sentinel in stdout, stderr, or safe JSON. Assert the captured soak argument array contains the expected names and internally expected numbers but is never echoed.

- [ ] **Step 2: Run the end-to-end test and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SyntheticWorkflow" --nologo
```

Expected: the test fails because the fixture driver is absent or the production dependency seam is incomplete.

- [ ] **Step 3: Implement the fixture driver and narrow dependency corrections**

The fixture script imports only the production module, constructs explicit scriptblock dependencies, and outputs the public safe result. It must not duplicate calculation, selection, validation, cleanup, or safe-summary logic. Keep the production defaults unchanged; adjust only the dependency signatures needed to substitute process boundaries.

- [ ] **Step 4: Add forbidden-output and argument-array adversarial cases**

Use roots and game-build fixture values containing spaces, apostrophes, semicolons, dollar signs, and parentheses. Verify none is evaluated as PowerShell, no extra file is created, and each value arrives as one child argument. Inject capture ID, hash, path, sender, UID, payload, and rate sentinels into captured child stderr and verify none reaches public output.

- [ ] **Step 5: Run all validation tests and the non-soak suite**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Validation" --nologo
dotnet test ApexLab.slnx -c Release --no-restore --filter "TestCategory!=Soak" --nologo
```

Expected: the synthetic workflow and every validation test pass without real telemetry or network input.

- [ ] **Step 6: Commit the synthetic workflow proof**

```powershell
git add tests/ApexLab.IntegrationTests/Validation scripts/PrivateF125Validation.psm1
git commit -m "test(validation): prove the private workflow end to end"
```

---

### Task 7: User documentation and repository privacy enforcement

**Files:**
- Modify: `README.md`
- Create: `docs/verification/v0.2.0-private-f125.md`
- Modify: `scripts/Verify.ps1`
- Modify: `tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs`

**Interfaces:**
- Produces: the exact owner command, F1 25 settings checklist, expected stage names, failure recovery guidance, and a commit-safe verification schema.
- Produces: `Assert-PrivateValidationVerificationRecord` in `Verify.ps1`, which accepts only the approved fields/conclusions in the v0.2 record and rejects private-rate or identity-bearing property names.

- [ ] **Step 1: Write failing repository-check tests**

Extend `Test-RepositoryCheckFailurePaths` with temporary JSON records that contain each forbidden key: `captureId`, `sha256`, `path`, `sender`, `sessionUid`, `payload`, `measuredPeak`, `minimumRate`, and `targetRate`. Assert the safe allowlist passes and every forbidden property fails with `Private validation record contains a forbidden property`. Add a C# contract test that extracts the README command and verifies `ValidatePrivateF125.ps1` accepts its parameter names.

- [ ] **Step 2: Run the focused checks and confirm the red state**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
dotnet test tests/ApexLab.IntegrationTests/ApexLab.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~PrivateF125PowerShellTests" --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
```

Expected: the new test references an absent repository check or missing documentation contract.

- [ ] **Step 3: Document the exact live procedure**

README must state: start F1 25, use offline Time Trial, enable UDP to `127.0.0.1:20777` with base F1 25 v3, run the one command, drive during the 30-second probe, arm/stop one capture in the native app, close ApexLab, and wait for the rate gate. State that no SIM, phone, cloud account, API key, paid service, or internet upload is required.

The verification document contains only the safe-summary fields and explicit unchecked owner items for the future real run and clean-Windows/SmartScreen checks. It must not contain example capture IDs, hashes, paths, endpoints beyond the public loopback default, or numeric private-rate examples.

- [ ] **Step 4: Enforce the safe verification record**

Parse only `docs/verification/v0.2.0-private-f125.md` fenced JSON when it contains a completed result. Require the Task 4 allowlist and reject forbidden property names recursively, non-boolean conclusions, or a `passed` status with any false conclusion. Retain the existing tracked raw-evidence/private-validation directory rejection.

- [ ] **Step 5: Run documentation, privacy, and full verification checks**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
git diff --check
rg -n "T[B]D|T[O]DO|F[I]XME" README.md docs/verification/v0.2.0-private-f125.md scripts/ValidatePrivateF125.ps1 scripts/PrivateF125Validation.psm1 tools/ApexLab.Replay/Validation tests/ApexLab.IntegrationTests/Validation
```

Expected: Verify passes, `git diff --check` is silent, and the placeholder scan returns no matches.

- [ ] **Step 6: Commit documentation and enforcement**

```powershell
git add README.md docs/verification/v0.2.0-private-f125.md scripts/Verify.ps1 tests/ApexLab.IntegrationTests/Validation/PrivateF125PowerShellTests.cs
git commit -m "docs(validation): document the private F1 gate"
```

---

### Task 8: Frozen-head verification, review, and GitFlow integration

**Files:**
- Modify only files identified by verified review findings.

**Interfaces:**
- Consumes: all feature commits and existing repository verification/release scripts.
- Produces: a frozen reviewed head, green exact-head PR CI, merge commit on `develop`, and green exact-merge CI. It does not produce a v0.2.0 tag or GitHub Release.

- [ ] **Step 1: Run the complete local non-soak gate**

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
```

Expected: restore, format, Release build, every non-soak test, coverage collection, repository privacy checks, and generated-change checks pass.

- [ ] **Step 2: Run the opt-in measured-rate soak with public synthetic inputs**

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/CaptureSoak.ps1 -DatagramCount 10020 -TargetDatagramsPerSecond 167 -MeasuredRealPeakDatagramsPerSecond 70 -MinimumObservedFractionPermille 950 -Configuration Release
```

Expected: the soak passes for at least 60 seconds with at least 95% observation, exact accounting/order, and bounded managed-memory growth. Keep detailed artifacts ignored.

- [ ] **Step 3: Publish and smoke-test the unchanged current version**

```powershell
$env:NUGET_PACKAGES='C:\Users\ASUS\.nuget\packages'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish.ps1 -Version 0.1.0 -CleanKnownOutputs
```

Run the published `ApexLab.exe --smoke-test` contract already documented in `docs/verification/v0.1.0.md` against a new absolute temporary data root. Expected: package creation and smoke result pass; no package output becomes tracked.

- [ ] **Step 4: Freeze and independently review the exact diff**

Record `git rev-parse HEAD`, ensure `git status --short` is empty, package the diff from `origin/develop` to that hash, and request both spec-compliance and code-quality review. Resolve every important finding using red-green steps, commit it, rerun Tasks 8.1–8.3, and restart review from the new frozen hash.

- [ ] **Step 5: Push and open the feature PR**

```powershell
git push -u origin feature/private-validation-assistant
gh pr create --repo DarianGanev/apexlab-desktop --base develop --head feature/private-validation-assistant --title "feat: add private F1 validation assistant" --body "Adds the privacy-safe one-command F1 validation workflow. Automated checks use synthetic evidence only; the real F1 validation and release gate remain pending."
```

The PR body states that automated tests use only synthetic evidence and that the real F1 result is still pending. Expected: the PR head SHA equals the frozen reviewed hash.

- [ ] **Step 6: Require exact-head CI and merge through GitFlow**

Inspect all check annotations and artifacts, require green CI for the exact frozen head, merge with a merge commit into `develop`, fetch, and verify the merge has the expected two parents. Require green push CI for the exact merge commit and no annotations.

- [ ] **Step 7: Clean only the merged feature worktree and branches**

After confirming the merge and clean CI, remove `C:\Users\ASUS\Desktop\diplomna\.worktrees\private-validation-assistant`, delete the local feature branch, and delete the remote feature branch. Preserve `.worktrees/release-0.1.0` and all private captures.

- [ ] **Step 8: Leave the release gate explicitly open**

Do not create `release/0.2.0`, change `Version.props`, tag, or publish a GitHub Release. Record that the next owner action is the real one-command F1 25 run followed by review of `latest-safe.json` and the clean-Windows/SmartScreen checks.
