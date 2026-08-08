# Private F1 25 Validation Assistant Design

**Date:** 2026-08-07
**Status:** Approved for implementation planning
**Milestone:** ApexLab v0.2.0 private evidence gate

## Purpose

The validation assistant turns the remaining private F1 25 release gate into one guided PowerShell
command. It coordinates the existing live probe, the native ApexLab capture UI, deterministic raw
replay, and the measured 2x synthetic rate gate without uploading telemetry or exposing evidence
identity in committed output.

The assistant is release engineering for a personal desktop application. It does not become a
second product, certify driving-coach effectiveness, or replace the requirement that the owner
actually drives a short offline F1 25 Time Trial.

## Selected approach

The user-facing surface is `scripts/ValidatePrivateF125.ps1`. PowerShell owns interactive process
orchestration and strict cleanup. A new typed `validate` command in the existing
`tools/ApexLab.Replay` diagnostic executable owns evidence verification and deterministic comparison.

This hybrid is preferred over implementing all validation logic in PowerShell because binary
evidence rules, replay accounting, and privacy invariants remain testable C# behavior over the same
application pipeline. It is preferred over an in-app wizard because v0.2 remains a narrow capture
evidence milestone and should not add a release-engineering workflow to the product UI.

## Invocation and preconditions

The normal invocation is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ValidatePrivateF125.ps1 -GameBuild "<build shown by F1 25>"
```

`GameBuild` is required, bounded, single-line text and may not contain a path separator or control
character. The assistant uses the standard `%LOCALAPPDATA%\ApexLab` data root. Supporting arbitrary
data roots, LAN capture, Season Pack telemetry, multiple captures per run, and unattended game
control are outside this design.

Before live work, the assistant requires:

- Windows, the repository-pinned .NET SDK, and the expected repository layout;
- a clean tracked worktree at one exact commit;
- no active ApexLab process;
- the default loopback endpoint `127.0.0.1:20777` to be available;
- the existing full non-soak verification to pass.

Detailed child-process output is captured rather than echoed because build tools can include local
paths and usernames. A failed preflight reports only its stage and exit code; the owner can run the
named underlying command separately for full local diagnostics.

## Guided workflow

The command executes these stages in order:

1. **Preflight** — validate inputs and repository state, run `scripts/Verify.ps1`, locate the exact
   Release binaries, and snapshot the leaf names of existing finalized manifests.
2. **Probe** — display the required base-F1-25 loopback settings and run the existing privacy-safe
   30-second probe while the owner drives in offline Time Trial.
3. **Probe evaluation** — parse typed schema-versioned JSON, require compatible base-v3 traffic,
   validate accounting and regression invariants, and calculate the maximum one-second rate bucket.
4. **Capture** — launch the native Release build and wait for it to exit. The owner arms capture,
   drives a short Time Trial, stops capture, confirms the finalized UI state, and closes ApexLab.
5. **Capture selection** — compare manifest leaf-name sets and require exactly one new finalized
   manifest. The capture ID derived from that leaf name remains in process memory and is never
   printed.
6. **Private validation** — invoke the typed `validate` command for the selected evidence and the
   private probe report.
7. **Rate gate** — invoke `CaptureSoak.ps1` with the measured real peak. That script derives the 2x
   minimum, requires at least 110% target headroom, at least 60 seconds, at least 95% observation,
   exact source accounting, preserved ordering, and bounded managed-memory growth.
8. **Safe summary** — emit only the approved commit-safe conclusions and finish with success only
   when every preceding stage passed.

Stages never continue after failure. Rerunning the assistant begins a new probe and capture; there
is no resumable private state in v0.2.

## Probe acceptance rules

The assistant accepts a live probe only when all of the following hold:

- report schema and status are supported and the protocol is `ea-f1-25-v3`;
- source and classifier accounting equations are exact;
- compatible traffic and a positive peak rate exist;
- there are no full-channel drops, oversized rejects, socket errors, malformed headers, wrong
  format/year, unknown packet IDs, unsupported versions, invalid lengths, or unexpected senders;
- source sequence, monotonic timestamp, UTC timestamp, session-time, and frame identifiers do not
  regress;
- observed packet descriptor shapes agree with the reviewed base-v3 registry.

Privacy-excluded Participants and Lobby Info traffic may be observed and counted by the probe. It
must never enter raw evidence and therefore must be zero in both evidence replays.

The measured real peak is the maximum count among the probe's one-second rate buckets. The first and
last partial buckets may be smaller and cannot lower that maximum. The value is kept private and is
not a compatibility claim until the derived synthetic rate gate passes.

## Typed private validation command

The diagnostic executable gains this internal engineering surface:

```text
validate --data-root <absolute-root> --capture-id <private-id> --probe-report <private-json>
```

Its argument parser rejects duplicates, missing values, unsafe roots, unsupported schema versions,
and noncanonical capture identities before opening evidence. It returns schema-versioned JSON and a
distinct nonzero exit code for invalid arguments, invalid evidence, unsupported protocol, probe
mismatch, nondeterministic replay, privacy failure, interruption, and unexpected failure.

The command:

1. opens evidence through `RawEvidenceReader`, which verifies structure, limits, footer, canonical
   completion manifest, and SHA-256 integrity;
2. resolves the recorded adapter from the manifest instead of trusting a caller-supplied protocol;
3. replays the same evidence twice in immediate mode through `RawReplayDatagramSource` and
   `CaptureIngestionCoordinator`;
4. compares typed replay reports rather than reparsing its own formatted JSON;
5. checks manifest record count, source/classifier equations, compatible counts, terminal pending
   counts, sequence-gap aggregate, and the absence of privacy-excluded or otherwise rejected
   evidence records;
6. compares the private probe's protocol and descriptor assumptions with the replayed evidence;
7. returns only aggregate conclusions and counts permitted by the data policy.

The existing `replay` command remains backward compatible. Shared replay execution is factored once;
the validator may not duplicate evidence opening, adapter selection, or ingestion orchestration.

## Determinism and sequence gaps

Replay comparison covers every stable typed field: protocol, timing mode, record count, complete
source/classifier counters, and sequence-gap aggregate. JSON whitespace is not part of the equality
contract.

For increasing sequences, the gap aggregate is:

```text
sum(currentSequence - previousSequence - 1)
```

Both replays must reproduce the aggregate implied by the evidence records. A duplicate or regressed
record sequence is invalid evidence and never becomes a replay comparison result.

## Privacy boundary

Raw datagrams remain under `%LOCALAPPDATA%\ApexLab\captures`. The assistant does not upload, copy,
rename, modify, or delete them. It reads only the one capture newly finalized during its owned UI
run.

The following values must never appear in assistant console output, committed documentation, or the
safe summary:

- capture ID or evidence/manifest hash;
- raw or normalized filesystem path and username;
- sender address or port;
- actual session UID;
- payload bytes or decoded identity-bearing data;
- the exact measured peak or its exactly derived threshold.

The probe JSON is held in a randomly named private temporary directory below the application data
root only as long as required. The selected capture ID remains memory-only. A `finally` path removes
the temporary directory after success, failure, or cancellation. Child output that could contain
local paths is captured there and is not forwarded to the console.

`CaptureSoak.ps1` retains its detailed JSON under ignored repository `artifacts/`; that local result
can reveal the private peak by ratio and must never be committed. Repository verification continues
to reject tracked telemetry, hashes, private-validation content, and build/test output.

## Safe result

On success the assistant prints and locally writes a commit-safe report containing only:

- schema version and `passed` status;
- validation date;
- supplied F1 25 game build;
- adapter ID and application version;
- pass/fail conclusions for offline capture, manifest integrity, deterministic replay, sequence-gap
  preservation, zero privacy-excluded evidence records, probe assumptions, and the 2x rate gate;
- final conclusion.

The local safe report contains no capture or sender identity and no exact private rate. Updating the
committed v0.2 verification record remains an explicit reviewed repository change after the owner
approves this local result.

## Failure model

Every stage has a stable stage name, a stable exit code, and one corrective message. Failures are
fail-closed:

- no or incompatible probe traffic never launches capture;
- no new manifest or more than one new manifest never selects by newest timestamp;
- incomplete, corrupt, unsupported, or unsafe evidence never reaches comparison;
- replay disagreement or accounting mismatch never reaches the rate gate;
- a rate-gate failure never writes a passing safe summary;
- cancellation cleans temporary files but preserves every raw capture.

The assistant never catches an unexpected exception and converts it to success. It emits a generic
unexpected-failure result without exception text that could disclose a local path.

## Verification strategy

### C# tests

- argument parsing and unique exit-code mapping;
- valid evidence with and without source sequence gaps;
- missing/incomplete, corrupt, hash-mismatched, trailing, and unsupported evidence;
- two typed replay reports that match and deliberately differ;
- probe schema/protocol/descriptor mismatch and every rejected counter class;
- privacy-excluded evidence and unexpected sender rejection;
- safe JSON property allowlist and forbidden-value scan;
- cancellation and disposal with no retained evidence handles.

### PowerShell and integration tests

- fixed process exit propagation and stage ordering;
- exact set-difference selection for zero, one, and multiple new manifests;
- peak-rate and soak-parameter calculation at lower and upper feasible bounds;
- temporary-directory cleanup on success, child failure, and cancellation;
- console and safe-summary forbidden-pattern scans;
- child command arguments passed as arrays without evaluation;
- a fully synthetic end-to-end run using generated evidence and fake process boundaries, never real
  telemetry.

The branch must additionally pass full verification, the opt-in soak, package publication and smoke
testing, repository privacy checks, independent frozen-diff review, exact-head PR CI, and exact merge
CI.

## Release boundary

The assistant makes the private gate reproducible; it does not make it synthetic. Version `v0.2.0`
remains blocked until the owner completes the live probe and UI capture, the assistant returns a
complete pass, the commit-safe record is reviewed, and the remaining clean-Windows release checks
pass. Only then may a release branch be merged and the exact verified main commit receive an
annotated tag and GitHub Release.
