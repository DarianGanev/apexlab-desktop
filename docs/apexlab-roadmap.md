# ApexLab Desktop 30-Week Roadmap

This roadmap fits the product into roughly seven months and reserves weeks 29-30 for contingency. Every milestone ends with working, tagged software and saved verification evidence. The original feature—the measured coaching experiment loop—appears in a narrow Bahrain vertical slice by week 9, before a generalized dashboard can consume the schedule.

## Weeks 1-2: Repository and Windows foundation

**Build**

- Create the private GitHub repository and GitFlow branches under the owner's Git identity.
- Install and pin the .NET 10 SDK.
- Establish solution boundaries, dependency locking, formatting, analyzers, tests, coverage collection, and budget-aware Windows CI.
- Build the WPF shell around the primary flow: Drive, Review, Coach, and Data & Settings.
- Add settings/data-root contracts, single-instance lifecycle, schema foundation, and packaged smoke-test mode.
- Publish a self-contained `win-x64` ZIP.

**Gate**

- A clean clone restores, formats, builds, tests, and publishes without machine-local files.
- The self-contained package passes a fresh-extraction smoke test and its unsigned/SmartScreen behavior is documented; definitive no-.NET-10 verification is scheduled in Windows Sandbox/VM before version 1.0.
- `main`, `develop`, short feature branches, identity audit, local `--no-ff` merges, and release procedure are exercised.

**Release:** `v0.1.0`

## Weeks 3-4: Header, raw evidence, and real-traffic cross-check

**Build**

- Bounds-checked little-endian primitive reader and exact 29-byte F1 25 header decoder.
- Loopback UDP receiver, bounded channel, packet-size/rate counters, and safe cancellation.
- One bounded, uncompressed opaque evidence file with arrival metadata and deterministic replay;
  production compression, chunk rotation, recovery, indexing, and quota management remain v0.4.
- Deterministic raw replay and small independently authored header fixtures.
- F1 configuration wizard with distinct port/no-traffic/unsupported-format states.

**Real validation gate**

- Capture a private F1 25 Time Trial sample immediately after the header slice.
- Compare observed packet identifiers, lengths, versions, rates, frame behavior, and header invariants to the published specification.
- Confirm selected decoded header values against the active game session.
- Truncated, oversized, unknown-version, invalid-index, and random datagrams never crash or allocate without bound.

This gate prevents fixtures and code derived from the same mistaken interpretation from validating each other.

**Release:** `v0.2.0`

**Implementation plan:** [`2026-07-24-apexlab-v0.2-capture-evidence.md`](superpowers/plans/2026-07-24-apexlab-v0.2-capture-evidence.md)

## Weeks 5-9: Provisional Bahrain coaching vertical slice

**Frozen slice contract:** [`v0.3-bahrain-slice-v1.md`](contracts/v0.3-bahrain-slice-v1.md)

**Build only what the slice needs**

- Decode only the Session, Lap Data, Motion, Car Telemetry, and Event fields required by one private recorded session.
- Replay the v0.2 raw evidence through a minimal canonical cache; production compression, recovery, and library behavior remain in v0.4.
- Manually audit and mark comparable Bahrain Time Trial laps under one fixed, documented setup/context.
- Use one-metre alignment, a robust personal reference, delta, one fixed reviewed Bahrain corner, and reviewed driver-visible landmarks.
- One evidence-linked braking detector with confidence and abstention.
- One immutable baseline/intervention/no-cue experiment with adherence, guard metrics, and supported/rejected/inconclusive/not-followed/expired outcomes.
- A basic foreground Review/Coach flow; tray capture, global shortcuts, notification, automatic validity, and generalized navigation are deliberately excluded from this slice.

**Gate**

- A manually audited raw-replayed flow builds at least five comparable baseline laps, issues the landmark-anchored braking experiment, imports later manually audited attempts, measures adherence, and produces a reproducible provisional outcome without database editing.
- Every claim links to exact laps, trace region, units, algorithm version, and source-chunk hashes.
- Failure to follow the instruction cannot be mislabeled as rejection of the hypothesis.
- The UI and verification record label this an engineering vertical slice, not final coaching-validity or effectiveness evidence.

**Release:** `v0.3.0`

## Weeks 10-12: Robust recorder and required protocol breadth

**Build**

- Complete the required fields for all supported packet types and canonical player-car model.
- Add compressed five-second raw chunks, checksums, atomic finalization, SQLite indexes, startup recovery, and deterministic cache rebuilding.
- Packet-quality accounting for malformed, unsupported, duplicate, reordered, missing, unexpected-sender, socket-gap, channel-drop, and writer-drop events.
- Full lap state machine for invalidation, restarts, incomplete laps, pits, pause, flashbacks, session changes, and missing boundaries.
- Session library, annotations, storage quota/low-disk behavior, migration tests, and deterministic reanalysis.
- Replay fault injection for loss, duplication, corruption, and reordering.
- Add tray-armed capture, automatic compatible-session boundaries, pre-stint instruction, configurable global start/stop shortcut, and optional neutral lap-complete notification.

**Gate**

- The frozen lap corpus covers normal, invalidation, flashback, restart, pause, setup/context change, session change, missing-boundary, duplicate, reordering, and gap cases; it reports a confusion matrix and accepts no contaminated safety case as clean.
- A two-hour realistic capture has zero bounded-channel/writer drops; source/socket gaps remain separately reported.
- Forced termination loses no more than the current five-second chunk.
- Raw replay reproduces canonical checksums for a fixed parser version.

**Release:** `v0.4.0`

## Weeks 13-15: Trustworthy spatial comparison

**Build**

- Complete context fingerprint with protocol/derived/user-entered provenance and unknown states.
- Channel-aware one-metre resampling that preserves material gaps.
- Robust medoid or trimmed personal reference instead of a single lucky best lap.
- Cumulative delta, segment loss, comparison coverage, uncertainty, and reproducible analysis jobs.
- Lap-quality audit and explicit exclusion reasons.

**Gate**

- Finish delta reproduces the official lap difference within 20 ms or one source sample.
- Injected gaps lower coverage/confidence instead of being silently filled.
- Fixed raw input and all version inputs produce identical derived checksums.
- False rejection in the frozen lap corpus is measured and explained.

**Release:** `v0.5.0`

## Weeks 16-18: Analysis workbench and transferable corner model

**Build**

- Synchronized speed, throttle, brake, steering, gear, RPM, and delta plots.
- Generated track polyline and gain/loss heatmap.
- Curvature-based corner proposals and approach/braking/rotation/apex/exit phases.
- Multi-lap selection, shared crosshair, zoom, keyboard navigation, and track-region selection.
- Revisioned advanced corner editor.
- Reviewed Bahrain definitions plus Silverstone and Hungary transfer definitions.

**Gate**

- Chart interaction remains responsive on full sessions and cannot block capture.
- Automatic boundaries are measured against reviewed annotations on all three tracks.
- Corrections persist as revisions and never destroy generated evidence.
- Charts expose gaps and source quality honestly.

**Release:** `v0.6.0`

## Weeks 19-21: Generalized explainable coach

**Build**

- Versioned phase features with units, windows, coverage, and provenance.
- Additional personalized detectors for over-slow braking, coasting, delayed throttle, exit-speed loss, inconsistency, and steering/line evidence.
- Robust median/MAD baselines, cold-start states, confounders, and contradiction handling.
- Ranking that allows only one active experiment and abstains when no candidate is defensible.
- Evidence-linked Review and Coach workflow across supported corners/tracks.

**Gate**

- Every finding is reproducible from visible traces and contributing raw chunks.
- Invalid or provenance-incomplete comparisons never produce advice.
- Sparse, contradictory, or confounded evidence produces a factual observation or abstention.
- A detector without a defensible physical interpretation is removed.

**Release:** `v0.7.0`

## Weeks 22-23: Experiment-engine hardening

**Build**

- Versioned adherence floors and practical-effect thresholds per experiment type.
- Minimum attempts, expiry, no-cue verification, guard metrics, and context-change termination.
- Practice/order/fatigue proxies and immutable audit history.
- Outcome comparison with uncertainty and distinct not-followed/inconclusive states.
- Experiment history and reproducible reevaluation under newer algorithms without rewriting original results.

**Gate**

- Synthetic and real N-of-1 scenarios cover beneficial, harmful, null, non-adherent, contradictory, expired, and context-changed outcomes.
- Original hypotheses, thresholds, selections, and results remain immutable and inspectable.
- The evaluated detector, adherence, practical-effect, guard, and outcome versions freeze with this release before any confirmatory D2 collection.

**Release:** `v0.8.0`

## Weeks 24-27: Frozen evaluation plus product and security hardening

**Build**

- Backup/restore and hardened import/export bundles.
- Low-disk, sleep/resume, shutdown, partial-data, database-busy, and migration UX.
- Accessibility, DPI, keyboard, contrast, focus, resizing, notification, and cancellation review.
- Redacted diagnostic report and saved performance evidence.
- Mark-of-the-Web/SmartScreen documentation and clean-account package testing.
- Collect the frozen D2 evaluation across at least nine session-days: at least 18 repeated experiments, six per track, with five clean laps in each baseline/intervention/no-cue block.
- Schedule at least three observation-only sessions and preserve non-target segments as negative-control regions.
- Obtain at least one independent experienced reviewer for blinded passage labels if detector-accuracy claims will be made; otherwise label the result rubric-conformance only.

Confirmatory collection uses the exact v0.8 detector/experiment versions. A release-blocking bug that changes an evaluated result invalidates affected D2 sessions and must be documented and recollected; product/UI/security fixes that cannot affect outputs may continue.

**Gate**

- Import rejects traversal, archive bombs, corrupt schemas, unsafe paths, and checksum failures.
- Headless capture meets the documented CPU/memory/UI budgets.
- Matched F1 benchmark runs meet the declared median and 1% low FPS impact thresholds.
- Core workflows are keyboard-operable at common Windows scale factors.
- All D2 experiments are treated as repeated measures clustered by session/day, not as independent observations.

**Release:** `v0.9.0`

## Week 28: Version 1.0 and diploma evidence

- Keep the v0.8 evaluated detector/experiment rules frozen and freeze all remaining product scope and schemas.
- Complete clean-machine self-contained packaging and tag/binary/version checks.
- Verify every retained database, raw chunk, and derived-cache migration.
- Create sanitized synthetic replay fixtures and a no-game fallback demonstration.
- Analyze the already-collected frozen personal holdout without retuning.
- Use frozen per-experiment product classifications and session/day-clustered aggregate uncertainty; do not claim per-experiment confidence intervals from five-lap blocks.
- Report observation-only practice trend, non-target negative controls, independent blinded detector agreement (or rubric-conformance fallback), and separate track-transfer results.
- Finalize architecture, protocol cross-validation, state-machine, experimental method, results, limitations, and threat-to-validity material.
- Rehearse the live personal workflow: arm capture, play F1 25, inspect one issue, run an experiment, verify adherence, and inspect the outcome.

**Release:** `v1.0.0`

## Weeks 29-30: Contingency

Use only for verified protocol corrections, reliability bugs, migration issues, diploma revisions, or demonstration rehearsal. Do not add another simulator, backend, model family, platform, or game mode.

## Post-1.0 candidates

- F1 25 2026 Season Pack protocol adapter if the pack is purchased.
- Additional reviewed tracks and wet-condition context handling.
- A local personal outcome ranker only after enough independent, adherent experiments exist for grouped rolling-origin evaluation.
- Grand Prix analysis as a separate product mode with new validity rules.
- Signed installer if a free or university code-signing route becomes available.

## Dataset boundaries

- **D0 fixtures:** small independently authored synthetic protocol/replay inputs safe to commit.
- **D1 personal engineering corpus:** real personal captures, private and ignored by Git.
- **D2 frozen personal holdout:** selected sessions untouched by detector threshold or model tuning.

Real telemetry remains outside Git even when Git LFS is available.

## Weekly operating rhythm

- Monday: define one issue-sized behavior and its acceptance test.
- Tuesday-Thursday: test-driven work and coherent green commits on a short `feature/*` branch.
- Friday: raw replay/real-game verification, performance evidence, documentation, independent review, and a local `--no-ff` merge to `develop` after the exact feature SHA passes CI.
- Every milestone: create a release branch, clean-build and package, merge locally to `main`, wait for CI on that exact merge SHA, annotate the semantic-version tag, publish tag-built artifacts, and merge the release back to `develop`.
- From week 8 onward: write the corresponding diploma sections while evidence and decisions are fresh.

Hosted Windows CI is deliberately limited to completed feature pull requests, integration/release commits, and tags. Identical local scripts run before every commit so the project remains zero-cost even if private-repository minutes or optional GitHub security features are unavailable.
