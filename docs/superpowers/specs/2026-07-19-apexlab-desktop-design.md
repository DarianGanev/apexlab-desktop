# ApexLab Desktop Product and Engineering Design

**Date:** 2026-07-19

**Status:** Approved for implementation

**Product:** ApexLab Desktop
**Diploma title:** Design and Evaluation of an Explainable, Closed-Loop Coaching System for Sim-Racing Telemetry

## 1. Product definition

ApexLab Desktop is a Windows application for personal F1 25 Time Trial coaching. It receives the game's official UDP telemetry on the same PC, reconstructs trustworthy laps, locates repeated corner-level time loss, proposes one measurable driving experiment, and evaluates whether later laps support that experiment.

It is not just a telemetry dashboard and it is not a generic AI chat interface. Its defining loop is:

1. Capture comparable, valid laps.
2. Build a repeatable personal reference.
3. Detect one recurring and measurable technique difference.
4. Show the evidence, uncertainty, and possible confounders.
5. Prescribe one small experiment.
6. Measure later laps under matched conditions.
7. Verify that the requested technique change was actually attempted.
8. Mark the experiment as supported, rejected, inconclusive, not followed, or expired.

The Windows application is the complete product. F1 25 is an external telemetry source. The replay command-line tool is part of the engineering and demonstration system, not a second user-facing product. Personal use defines the product audience; the diploma defines how its engineering and evidence are evaluated. No participant study, multi-user service, or academic administration system is part of the product.

## 2. Fixed product boundary

### Required

- Windows 11 desktop application written in C# with WPF on .NET 10 LTS.
- Development and testing on the owner's Windows PC.
- Compatibility with the base EA SPORTS F1 25 game and the official F1 25 v3 UDP protocol.
- Same-PC capture through `127.0.0.1:20777` by default.
- Time Trial as the first supported game mode.
- Game-normalized steering, throttle, and brake telemetry produced while using the owner's wheel and pedals; ApexLab does not connect to the hardware directly.
- Dry, context-matched lap comparison for the first release.
- Local capture, persistence, replay, analysis, coaching, backup, and export.
- A deterministic evidence engine before any learned ranking.
- Zero mandatory operating cost and no paid service dependency.
- Self-contained `win-x64` distribution suitable for a clean Windows machine.

### Explicit non-goals for version 1.0

- Mobile, web, browser, or macOS clients.
- Smartwatch, IoT, embedded, or custom hardware integration.
- Authentication, accounts, cloud sync, hosted backend, or subscription services.
- Game memory reading, process injection, kernel drivers, administrator rights, or an in-game overlay.
- Live spoken corner instructions.
- Race strategy, pit strategy, setup generation, multiplayer stewarding, or opponent surveillance.
- Support for every simulator, F1 release, car class, weather condition, or game mode.
- A large language model that invents coaching explanations.
- Deep learning without an adequate independent dataset.
- Claims that a telemetry correlation proves causation.
- Redistribution of EA files, Formula 1 assets, team logos, track artwork, game audio, or copyrighted screenshots.

The separately published F1 25 2026 Season Pack data format may be added later as another protocol adapter if the pack is purchased. It cannot delay or weaken the base F1 25 implementation. ApexLab makes no claim about a distinct future game until its product and protocol are officially available and verified.

## 3. Users and primary scenarios

The initial user is the owner of the application: an F1 25 player using a wheel and pedals who wants more rigorous feedback than a lap-time leaderboard or raw trace viewer provides.

### First-run setup

The application checks whether UDP port 20777 is available, explains the exact F1 25 telemetry settings, and waits for a compatible packet. It reports distinct states for no traffic, wrong port, incompatible packet format, malformed traffic, another process using the port, and an unexpected sender. Loopback is selected by default; LAN reception is a deliberate advanced setting.

### Live capture

The user arms capture before returning to F1 25. ApexLab automatically recognizes compatible session boundaries and records without requiring the main window to remain visible. A tray state and configurable system-wide start/stop shortcut are core workflow features. Before a stint, the application presents the one active experiment; after a lap it may emit a neutral, optional Windows notification or sound without giving mid-corner advice. An always-on-top window is useful only in borderless/windowed play and is never represented as working over exclusive fullscreen.

When visible, the compact view shows connection health, packet rate, current lap, validity, elapsed time, storage health, and the active experiment. It receives a throttled immutable snapshot at 5-10 Hz; it never renders the raw packet stream.

### Analysis workbench

After a lap completes, the user can compare selected laps through synchronized speed, throttle, brake, steering, gear, RPM, and delta traces. A generated track polyline colors time gain/loss. A shared crosshair, zoom, keyboard navigation, resizable panels, and selectable track regions make the evidence inspectable.

### Coach Lab

A coaching card states the corner and phase, factual observation in physical units, reference behavior, localized time effect, support count, confidence components, confounders, and exactly one experiment. It never hides the source traces. After the requested later laps, the application reports the measured outcome and retains an audit history.

### Replay and diagnostics

Recorded raw datagram envelopes and small synthetic fixtures can be replayed through the same decoder and canonical pipeline with a deterministic clock. Diagnostic modes can simulate loss, duplication, corruption, and reordering without requiring the game during a demonstration.

## 4. Main application surfaces

1. **Drive** - connection guide, port health, armed/live capture, lap validity, and the one pre-stint instruction.
2. **Review** - session library, selected-lap evidence, synchronized traces, delta, track heatmap, and annotations.
3. **Coach** - baseline progress, prioritized issue, experiment definition, adherence, and measured result.
4. **Data & Settings** - loopback/LAN policy, port, units, storage, backup, import/export, and legal notice.

Corner editing, replay, fault injection, packet counters, logs, and diagnostic reports are advanced tools reached from Review or Data & Settings, not equal top-level destinations. The core user flow is `Connect -> Build baseline -> Review one issue -> Run experiment -> See result`.

## 5. Technology choices

| Area | Choice | Reason |
|---|---|---|
| UI | WPF with MVVM | Mature Windows desktop framework, strong data binding, testable view models, and suitable dense desktop layouts. |
| Runtime | .NET 10 LTS | Supported beyond the diploma period and can publish a self-contained Windows build. |
| Language | C# with nullable references | Strong binary, asynchronous, numeric, and testing support. |
| Concurrency | `System.Threading.Channels` and cancellation tokens | Bounded asynchronous ingestion with explicit backpressure and clean shutdown. |
| Database | SQLite through `Microsoft.Data.Sqlite` | Local transactional metadata, migrations, backup support, and no server process. |
| Source storage | Versioned compressed raw-datagram chunks | Parser corrections can reprocess the original accepted evidence through the complete pipeline. |
| Derived storage | SQLite metadata plus reproducible caches | Fast browsing without treating a derived representation as irreplaceable source evidence. |
| Charts | ScottPlot WPF | Interactive scientific plots suitable for long telemetry series. |
| Hosting | .NET Generic Host | Dependency injection, lifecycle ownership, configuration, and background services. |
| Tests | MSTest plus dedicated integration and architecture tests | Microsoft-supported tooling with no additional assertion-library licensing concern. |
| CI | Budget-aware GitHub Actions on `windows-latest` | Pull-request/release checks build the actual WPF target; identical local scripts remain authoritative when hosted minutes or features are unavailable. |

Dependencies are pinned centrally and restored from lock files. Nullable reference checking, deterministic builds, analyzers, and warnings-as-errors are enabled. A dependency is added only when it removes meaningful risk or complexity.

## 6. Solution architecture

```text
F1 25
  -> UDP loopback receiver
  -> bounded Channel<DatagramEnvelope>
  -> accepted-packet filter and compressed raw chunk writer
  -> strict F1 25 v3 decoder
  -> canonical event/frame assembler
       -> 5-10 Hz immutable live snapshot
       -> session and lap state machine
  -> completed comparable lap
  -> spatial alignment and track/corner model
  -> personalized reference and factual findings
  -> deterministic detectors
  -> confidence, confounder, and abstention policy
  -> closed-loop experiment engine
  -> WPF evidence and coaching views
```

### Repository layout

```text
src/
  ApexLab.App/                     WPF shell, views, view models, composition root
  ApexLab.Application/             UI-independent use cases, options, and lifecycle contracts
  ApexLab.Domain/                  pure canonical and product domain model
  ApexLab.Analysis/                pure lap, spatial, track, corner, and feature algorithms
  ApexLab.Coaching/                pure findings, confidence, experiments, and outcomes
  ApexLab.Telemetry.Abstractions/  datagram/protocol/capture contracts without sockets
  ApexLab.Telemetry/               UDP transport, bounded ingestion, canonical assembly
  ApexLab.Protocols.F125/          strict base F1 25 v3 decoder only
  ApexLab.Persistence/             SQLite migrations, repositories, raw chunks, caches
tools/
  ApexLab.Replay/                  deterministic replay and diagnostic CLI
tests/
  ApexLab.Domain.Tests/
  ApexLab.Analysis.Tests/
  ApexLab.Coaching.Tests/
  ApexLab.Telemetry.Tests/
  ApexLab.Protocols.F125.Tests/
  ApexLab.Persistence.Tests/
  ApexLab.IntegrationTests/
benchmarks/
  ApexLab.Benchmarks/
docs/
  adr/
  protocol/
  verification/
  superpowers/
```

Project references point inward and keep transport separate from protocol decoding:

```text
ApexLab.App -> Application + Telemetry + Protocols.F125 + Persistence
Application -> Domain + Analysis + Coaching + Telemetry.Abstractions
Analysis -> Domain
Coaching -> Domain + Analysis
Telemetry -> Application + Domain + Telemetry.Abstractions
Protocols.F125 -> Domain + Telemetry.Abstractions
Persistence -> Application + Domain + Telemetry.Abstractions
Replay -> Application + Telemetry + Protocols.F125 + Persistence
```

`ApexLab.Application`, `ApexLab.Domain`, `ApexLab.Analysis`, and `ApexLab.Coaching` have no WPF, socket, filesystem, database, or wall-clock implementation dependency. `Telemetry.Abstractions` contains envelopes, canonical protocol results, and adapter contracts but no socket implementation. Application owns use-case orchestration and its infrastructure ports; Telemetry and Persistence implement those ports. WPF and Replay are two composition roots over the same Application workflows, so replay cannot duplicate capture/analysis orchestration and a future protocol adapter cannot duplicate UDP transport.

### Principal boundaries

- `IDatagramSource` (Application port): starts one source and emits bounded `DatagramEnvelope` values; UDP transport implements it.
- `ITelemetryProtocolAdapter` (Telemetry.Abstractions): validates and decodes one versioned datagram into canonical events.
- `ICaptureWorkflow` (Application use case): owns capture state transitions, source, adapter, writers, counters, and shutdown.
- `IRawDatagramChunkStore` (Application port): appends accepted envelopes, compresses, checksums, finalizes, reads, and recovers source chunks; Persistence implements it.
- `ISessionRepository` (Application port): owns structured session, lap, insight, experiment, and analysis-job metadata; Persistence implements it.
- `ILapAnalyzer`: turns a completed lap into spatial and corner metrics.
- `ICoachEngine`: creates eligible findings and selects at most one experiment.
- `IExperimentEvaluator`: compares later matched laps and produces a measured outcome.
- `IReplayClock`: makes playback timing deterministic and testable.

## 7. Concurrency and lifecycle

The socket receive loop, decoder, chunk writer, analysis workers, and WPF dispatcher are isolated. A slow chart cannot block packet reception or disk persistence.

- The receiver rents fixed buffers where safe and immediately copies only the accepted datagram length into an owned envelope.
- A bounded channel protects memory. Its capacity and overflow policy are explicit and tested.
- Packet validation and canonicalization happen away from the UI thread.
- Persistence uses one ordered writer per active capture.
- Completed-lap analysis runs after durable lap finalization.
- The UI observes immutable snapshots through a throttled publisher.
- Start and stop are idempotent. Shutdown has a bounded drain period and then records an interrupted session honestly.
- A single-instance guard prevents two ApexLab processes competing for the same port and data directory.
- Sleep, resume, clock changes, process termination, low disk, and Windows shutdown are explicit state transitions.

## 8. Telemetry correctness

- Bind `127.0.0.1:20777` by default. Binding all interfaces requires an explicit advanced setting and warning.
- Use F1 `sessionTime` for game chronology and `Stopwatch` monotonic time for arrival diagnostics.
- Validate packet format, game year, packet ID, packet version, expected size, player index, and bounds before decoding fields.
- Decode little-endian primitives through one bounds-checked reader.
- Treat the 29-byte F1 25 header as a versioned contract covered by golden tests.
- Deduplicate with protocol-appropriate session, packet, and frame identifiers.
- Track malformed, unsupported, duplicate, reordered, missing, unexpected-sender, socket-error, and application-dropped counts by packet type.
- Treat session UID changes, flashbacks, restarts, and frame regression as explicit timeline boundaries.
- Reject the Participants packet because names are unnecessary. Required packet types contain fixed arrays for all vehicle slots; raw accepted datagrams therefore retain those anonymous slots unchanged, while decoding and analysis select only the player entry.
- Never interpolate silently across a material gap. Lower quality/confidence or exclude the affected interval.
- Persist every accepted datagram type needed by the supported analysis, together with arrival time, sender classification, and capture sequence, before treating decoded/derived data as durable evidence. This permits parser corrections and complete reanalysis.

Supported packet types are introduced only when a feature needs them. The initial path prioritizes Session, Lap Data, Motion, Car Telemetry, Car Status, Car Setup, Event, Car Damage, and Time Trial packets.

## 9. Persistence and data model

The application data root is `%LOCALAPPDATA%\ApexLab`. The database contains metadata and derived indexes, not one row per telemetry sample.

### Primary metadata

- `Session`: identity, protocol, track, game mode, context fingerprint, start/end state, counters, and schema versions.
- `Lap`: session, lap number, official time, validity, quality, exclusion reasons, context, and chunk ranges.
- `RawDatagramChunk`: relative path, sequence, arrival/frame range, compression/schema, packet count, size, and SHA-256 checksum.
- `TrackModel`: generated polyline, coordinate transform, source sessions, and algorithm version.
- `CornerRevision`: user-reviewed boundaries, phases, labels, and revision history.
- `AnalysisRun`: input fingerprint, algorithm/configuration versions, status, output checksum, and errors.
- `Finding`: factual metric difference, support, effect, repeatability, confidence, and confounders.
- `Experiment`: hypothesis, one requested change, baseline selection, acceptance criteria, status, and outcome.
- `Annotation`: user note or tag linked to a session, lap, corner, or experiment.

SQLite runs in WAL mode with foreign keys, a busy timeout, explicit migrations, and a single application writer. Migration tests open databases from every retained schema version.

Accepted raw datagram envelopes for the packet types required by the supported analysis are written unchanged into schema-versioned, compressed five-second chunks. The Participants packet is rejected, so player names are not stored; however, required F1 packet layouts contain anonymous slots for other cars and those bytes necessarily remain in an authentic raw datagram. Decoding and derived caches use only the player slot. A chunk is created with a `.partial` suffix, written sequentially, flushed, closed, hashed, and atomically renamed before its database index becomes committed. Startup recovery validates the final partial file, discards only an invalid tail, and marks the session interrupted.

Canonical samples and analysis outputs are versioned, reproducible caches. They may be stored for fast browsing, but raw accepted chunks remain the source for parser upgrades and deterministic replay. Retention quotas are configurable, and no fixed storage-rate promise is adopted before measuring the real F1 25 packet mix, compression ratio, and disk throughput.

Import/export uses a ZIP bundle containing a manifest, relative paths, schema and algorithm versions, hashes, and optional annotations. Player-only derived export is the default. Exporting authentic raw chunks is a separate explicit action that warns that required packet arrays contain anonymous other-car slots even though participant names are excluded. Import validates file count, uncompressed size, paths, hashes, schema versions, and duplicate identity before extraction to a temporary directory. Path traversal and archive bombs are rejected.

## 10. Lap validity and comparison

Only context-compatible laps may be used for coaching. A context value is never invented when it is absent. Its provenance is stored with the fingerprint:

| Context field | Source |
|---|---|
| Protocol/game format | Validated packet header and selected adapter |
| Track/layout and session mode | Session packet, with unsupported values rejected |
| Setup | Normalized player entry in the Car Setup packet and a versioned hash |
| Assists | Published Session packet assist fields; any unavailable option is explicitly unknown |
| Weather and temperatures | Session packet |
| Tyre compound and vehicle state | Car Status and Car Damage packets |
| Flashback/restart boundaries | Event packets plus verified session/frame regression rules |
| Steering, throttle, brake, gear, RPM | Player entry in Car Telemetry; these are game-normalized values, not direct hardware reads |
| Controller class and owner profile | User-entered local profile, never inferred from steering traces |
| Car/performance assumptions | Protocol field where published, otherwise an explicit Time Trial profile confirmation |

An unknown required field prevents a comparison that depends on it. The initial reviewed coaching track is Bahrain; Silverstone and Hungary are transfer tracks after the Bahrain vertical slice is proven.

The lap state machine handles starts, restarts, invalidations, incomplete laps, pits, pause/resume, flashbacks, session changes, and missing boundary packets. False acceptance of a contaminated lap is worse than conservative rejection. Every exclusion has a visible reason.

Its frozen validation corpus contains normal laps plus isolated and combined invalidation, flashback, restart, pause, setup/context change, session UID change, missing-boundary, duplicate, reordering, and material-gap cases. The gate reports a confusion matrix and individual reasons: zero false acceptance is required in the frozen safety cases, while false rejection is measured and minimized rather than hidden.

Samples are aligned by lap distance on a one-metre grid with channel-aware interpolation. Substantial gaps remain gaps. Cumulative delta is computed from spatially aligned travel time, and its finish value must reproduce the lap-time difference within a documented tolerance. Reference selection uses a medoid or robust aggregate of the user's repeatable fastest clean laps, not a single lucky best lap.

A personal reference can expose only behavior that varies within the user's own evidence. If the driver repeats the same inefficient habit on every lap, the comparative coach cannot identify it as a loss and must abstain. It never presents an absent contrast as a diagnosis.

## 11. Track, corner, and phase analysis

Track geometry is reconstructed from repeated clean world-position samples. Heading and curvature propose corner regions. Reviewed corner metadata can override automatic boundaries without overwriting the generated source. Each supported coaching region also has reviewed driver-facing landmarks—corner number, braking board, curb start/end, turn-in marker, or another reproducible visual anchor—stored as text and reconstructed coordinates without vendor artwork.

Each region is divided into approach, braking, rotation, apex, and exit phases. Derived features include:

- brake onset, peak, release profile, and trail distance;
- minimum speed and its position;
- coasting duration and distance;
- throttle commit point and stability;
- steering magnitude, rate, corrections, and smoothness;
- gear selection and shift positions;
- path and apex displacement;
- entry, apex, exit, and segment speed;
- localized time loss and exit carry into the next segment;
- lap-to-lap consistency and evidence coverage.

Every metric records its units, spatial window, source channels, sample coverage, and algorithm version.

## 12. Explainable coaching

The first coach uses deterministic, personalized detectors. Candidate detectors include early/over-slow braking, excessive coasting, delayed throttle, exit-speed loss, inconsistent braking, unstable steering correction, and a line deviation correlated with exit loss.

A detector cannot directly issue advice. It first produces a factual finding. Eligibility then considers:

- context match;
- source quality and coverage;
- effect magnitude in milliseconds and physical units;
- recurrence over multiple laps;
- personal variability through robust median/MAD statistics;
- competing explanations and contradictory channels;
- safety and actionability;
- whether another active experiment already exists.

The coach abstains when evidence is weak, sparse, contradictory, or confounded. Every card links back to the exact trace region and contributing laps.

An experiment changes one measurable variable, for example beginning brake release near the 50 m board and completing it by the curb start, corresponding to a measured 5-10 m earlier release, without lowering minimum speed beyond a stated guard. The pre-stint card always pairs analytical coordinates with a reviewed driver-visible landmark; an instruction without a reproducible cue is ineligible. Its immutable definition includes baseline laps, target corner/phase, directional adherence metric, instruction bounds, minimum valid attempts, primary success metric, practical-effect floor, uncertainty rule, guard metrics, no-cue verification, and expiry.

The default decision protocol requires at least five comparable baseline laps, at least three intervention attempts, and at least three no-cue verification attempts. An intervention is adherent only when the target technique moves in the requested direction by the larger of its versioned physical floor or half the baseline MAD, remains inside the safety bound, and does so on at least two thirds of valid attempts. Exact per-technique floors are calibrated and versioned before evaluation.

`Supported` requires adequate adherence plus a no-cue median target-segment improvement larger than the configured practical floor and combined measurement uncertainty, without a guard metric worsening beyond its allowed baseline variability. `Rejected` means adequate adherence produced no useful effect or a meaningful adverse effect. `NotFollowed` means adherence was inadequate, `Inconclusive` means quality/coverage or results were insufficient or conflicting, and `Expired` means the required attempts were not completed in the defined window. Practice, order, fatigue proxy, and session/context changes are recorded as confounders. Outcome evaluation never rewrites the hypothesis or conflates failure to follow it with evidence against it.

## 13. Post-1.0 machine-learning path

Machine learning is not on the version 1.0 critical path. The only defensible later learned component is a local personal outcome ranker that predicts which already-eligible deterministic experiment is most likely to help under the current context. It uses engineered telemetry features and past experiment outcomes; it does not generate diagnoses or explanations.

The learned path is allowed only when:

- a predeclared minimum number of independent, adherent experiment outcomes exists across enough track/corner/technique groups;
- training, validation, and evaluation avoid leakage from later laps;
- rolling-origin evaluation accounts for skill drift and groups correlated attempts from the same experiment;
- it beats deterministic ranking on predeclared usefulness and calibration metrics;
- its decision can still cite deterministic evidence;
- inference is local, versioned, fast, and reproducible;
- deterministic coaching remains available as a fallback.

Candidate models are regularized logistic regression, a generalized additive model, or a shallow tree ensemble. If no model passes the gate, the deterministic ranker remains the product and the negative result is reported honestly.

### Exploration without a detected contrast

A separately labelled exploration mode may offer a manually reviewed, bounded technique perturbation when the personal reference contains no contrast. It states that no problem was detected and that the purpose is to learn the driver's response. Exploration hypotheses use the same landmark, adherence, guard, and outcome machinery but cannot be counted as detector-confirmed coaching.

## 14. Single-driver diploma evaluation

The product can be personally useful with three attempts per block, but the diploma does not treat that minimum as effectiveness evidence. Detector and experiment versions freeze at `v0.8.0` before confirmatory collection. The owner then completes at least 18 repeated experiments clustered within at least nine sessions on different days: six Bahrain experiments, six Silverstone experiments, and six Hungary experiments. These are not independent observations because they come from one driver; session/day is the analysis cluster. Each evaluated experiment uses at least five clean baseline laps, five valid intervention attempts, and five no-cue verification laps; inadequate-adherence experiments remain results but are analyzed separately from technique-effect estimates.

### Outcomes and uncertainty

- **Primary product outcome:** within-experiment change in median target-segment time from baseline to no-cue verification, in milliseconds and percent.
- **Technique outcome:** change in the detector's physical adherence metric, proving the requested action changed.
- **Guard outcomes:** whole-lap time, valid-lap rate, target-corner exit speed, and the experiment-specific safety/quality guard.
- **Individual classification:** the frozen practical-effect, measurement-tolerance, adherence, and baseline-variability rules classify each experiment. Five-lap blocks do not justify a per-experiment 95% confidence interval.
- **Aggregate uncertainty:** report session-level effect summaries, then resample whole session/day clusters—with every experiment in a selected cluster kept together—to obtain an explicitly exploratory 95% bootstrap interval. The small number of one-driver clusters is stated alongside the interval.
- **Controls:** at least three frozen observation-only sessions estimate ordinary practice trend; non-target segments on each lap act as negative-control regions; harmful/null synthetic scenarios verify that the evaluator does not manufacture improvement.
- **Order and retention:** session order, time-on-task, attempt number, rest interval, and prior track exposure are recorded. No-cue verification is interpreted as short-term retention, not a perfect untreated counterfactual.

### Detector validation and transfer

At least 150 stratified corner passages are randomized and reviewed blind to detector output under a frozen observable-label rubric using synchronized traces and, when available, in-game replay/video. Precision, recall, balanced accuracy, and expert-agreement claims require at least one independent experienced sim-racing reviewer or supervisor who did not implement the detector. Cohen's kappa is reported between the independent labels and detector/author labels. If an independent reviewer cannot be obtained, repeated blinded author annotation may measure only rubric conformance and repeatability; it is not presented as external diagnostic accuracy.

Detector thresholds are developed on Bahrain only, then frozen before Silverstone and Hungary transfer evaluation. Coverage, agreement, false positives, and experiment effects are reported separately by track; no retuning is hidden inside transfer results.

This is a repeated N-of-1 evaluation of one driver. Conclusions concern this implementation's reliability, traceability, and usefulness for the owner. They do not claim population-wide driving improvement, expert equivalence, or causality beyond the limits of the controls and repeated design.

## 15. Security and privacy

- No administrator privileges, game hooking, process inspection, or hidden background collection.
- Loopback-only ingress by default; LAN mode exposes a clear unauthenticated-input warning and optional sender allowlist.
- Malformed or hostile UDP input must not crash the process, allocate without bound, or write outside the data root.
- Configuration, logs, database, captures, and exports contain no credentials.
- Real captures, local databases, logs, backup archives, and personal notes are ignored by Git.
- Diagnostic logs avoid player names, private IP addresses, and raw packet payloads by default.
- No network egress exists in the core release except explicit GitHub links opened by the user.
- Imported bundles are untrusted input and use bounded validation before extraction.
- Deletion requires an explicit target and offers recoverability where practical.

## 16. Reliability and performance budgets

Initial budgets are engineering targets and are revised only with recorded evidence:

- Zero bounded-channel or writer drops during a two-hour realistic capture on the development PC; socket/kernel gaps and source frame gaps are counted separately and never attributed to the application without evidence.
- Stable synthetic ingestion at least twice the observed F1 25 peak datagram rate.
- Headless/tray capture averages no more than 5% of total machine CPU capacity and 300 MB working set on the documented development PC.
- No more than the current five-second chunk lost after forced termination.
- Live UI updates at 5-10 Hz with p95 dispatcher work below 50 ms and no receiver-channel growth.
- Completed-lap analysis finishes within 10 seconds on the development PC.
- Raw compressed storage rate, compression ratio, and disk-write latency are measured from real traffic before choosing the default quota.
- In matched F1 benchmark runs, enabling headless capture changes median FPS by less than 2% and 1% low FPS by less than 5%; methodology and variance are saved with the result.
- Startup recovery completes without manual database editing.
- Replaying identical input with identical versions produces identical canonical and derived output.

Benchmarks record hardware, build, fixture checksum, packet rate, duration, memory, CPU, disk throughput, drops, and software version. A performance claim without a saved result is not accepted.

## 17. Error handling and diagnostics

User-facing errors state what failed, whether data is safe, and the next action. Internal exceptions retain a causal chain and correlation identifier.

Important states include:

- port unavailable or access denied;
- no packets, wrong port, or wrong destination;
- unsupported format/version or unexpected packet length;
- sender change or LAN policy violation;
- receive/channel/writer drops;
- database busy, corrupt, or migration failure;
- partial-chunk recovery;
- low disk or quota reached;
- import validation failure;
- interrupted analysis or stale algorithm output;
- incompatible old capture requiring migration/reanalysis.

The diagnostics page can copy a redacted report containing application version, OS/runtime, configuration without private paths, counters, schemas, and recent structured errors.

## 18. Accessibility and desktop behavior

- Complete keyboard navigation and visible focus.
- Text alternatives for color-coded gain/loss and packet-health states.
- Color-blind-safe palettes with configurable contrast.
- DPI-aware layouts and chart rendering at Windows scaling levels.
- Resizable panes, saved layouts, and readable minimum window sizes.
- Reduced-motion behavior and no essential information conveyed only by animation.
- Clear confirmation for destructive actions and progress/cancellation for long analysis.
- Single-instance behavior and graceful tray/window lifecycle.

## 19. Testing strategy

Production behavior is developed test-first. The required test layers are:

- Pure unit tests for binary readers, packet decoders, state machines, resampling, delta, metrics, detectors, confidence, and experiments.
- Golden packet tests based on small independently authored synthetic fixtures.
- Early real-traffic validation that captures opaque accepted datagrams after the header slice, then checks observed lengths, packet mix, invariants, decoded ranges, and in-game values before the remaining packet family is implemented.
- Property and fuzz tests for malformed/truncated/random datagrams and hostile imports.
- Temporary-database migration, transaction, recovery, and backup tests.
- Loopback UDP integration tests with loss, duplication, ordering, cancellation, and port conflicts.
- Deterministic replay tests comparing canonical and derived checksums.
- View-model tests for commands, navigation, cancellation, validation, and error states.
- WPF smoke tests for startup, main navigation, DPI-sensitive layouts, and packaging.
- Long capture/replay soak tests with saved performance evidence.

Local verification runs before every commit. Budget-aware Windows CI runs on completed feature pull requests, integration/release commits, and tags; it restores locked dependencies, verifies formatting, builds Release with warnings as errors, runs all non-soak tests, reports coverage for Domain/Analysis/Coaching/Protocols, publishes `win-x64`, starts the packaged application in smoke mode, and verifies tag-to-binary version consistency. Foundation CI records coverage without a gate; behavior milestones require at least 80% line and 70% branch coverage in those pure/protocol assemblies, excluding generated code and XAML.

## 20. Versioning, compatibility, and packaging

Application releases use semantic versions and annotated Git tags. Capture schemas, database schemas, protocol adapters, analysis algorithms, and coaching rules have independent explicit versions.

The default release artifact is a self-contained `win-x64` folder compressed as ZIP plus a SHA-256 checksum. Single-file publishing is adopted only after native charting and SQLite dependencies pass clean-machine tests. Code signing and MSIX are optional because this is a zero-cost personal application. Clean-account verification covers ZIP extraction, Mark-of-the-Web/SmartScreen behavior, the expected unsigned-publisher warning, and checksum instructions. Before version 1.0, Windows Sandbox or a VM with no .NET 10 runtime confirms genuine runtime independence; a fresh account on the SDK-equipped development PC is not accepted as that proof.

Older supported captures are migrated or reanalyzed explicitly. Unsupported future protocol traffic is rejected with a useful message; the decoder never guesses a layout.

## 21. Git and delivery model

- The reviewed documentation bootstrap is the one pre-release commit permitted on `main`; after `v0.1.0`, every `main` tip is a released/tagged state.
- `develop` is the integration branch.
- Work occurs on short `feature/*` branches and merges to `develop` with `--no-ff`.
- `release/x.y.z` branches contain only release hardening, version, changelog, and documentation changes.
- Releases merge into `main`, receive an annotated tag, and merge back into `develop`.
- `hotfix/x.y.z` begins from `main`, merges and tags there, then merges back.
- Conventional Commits describe coherent behavior. Empty, cosmetic, backdated, knowingly broken, and artificial history is prohibited.
- All commits use `DarianGanev <darianganev83@gmail.com>` as both author and committer with no bot or co-author trailer.

## 22. Milestones

| Version | Demonstrable outcome |
|---|---|
| `v0.1.0` | Repository, CI, architecture, tests, settings, diagnostics foundation, and navigable WPF shell. |
| `v0.2.0` | Strict header/core decoder plus early opaque real-traffic capture, health counters, and fixture/real invariant validation. |
| `v0.3.0` | Provisional Bahrain engineering slice over manually audited replay: one reviewed braking issue, landmark cue, experiment, adherence, and measured outcome. |
| `v0.4.0` | Remaining required F1 25 packets, robust live capture, SQLite metadata, crash recovery, and session library. |
| `v0.5.0` | Clean-lap reconstruction, context matching, spatial alignment, reference, and delta. |
| `v0.6.0` | Interactive telemetry workbench, track reconstruction, corners, phases, and editor. |
| `v0.7.0` | Explainable findings, confidence/confounders, abstention, and evidence-linked coaching. |
| `v0.8.0` | Complete baseline-intervention-verification experiment loop and outcome history. |
| `v0.9.0` | Backup/import/export, accessibility, diagnostics, performance hardening, and packaging. |
| `v1.0.0` | Verified personal Windows release, clean-machine artifact, checksums, documentation, and diploma demonstration. |

## 23. Critical risks and responses

| Risk | Consequence | Response |
|---|---|---|
| Advice is merely plausible, not valid | Product becomes an attractive dashboard with weak academic contribution | Evidence-first findings, abstention, N-of-1 experiments, guard metrics, and outcome history. |
| Protocol mistakes silently corrupt analysis | All later results become untrustworthy | Strict lengths/versions, opaque real capture after the header slice, fixture/real cross-checks, fault injection, counters, replay checksums, and no guessed layouts. |
| Invalid laps contaminate the reference | Coaching rewards artifacts | Conservative state machine, visible exclusions, context fingerprints, and manual audit tools. |
| UI work consumes the schedule | Deep engine remains incomplete | Headless vertical slices first; thin UI over proven services; defer decorative features. |
| One driver provides too little ML data | Overfit model presented as AI | Deterministic coach is complete; learned ranker is gated and a negative result is acceptable. |
| Charts or analysis affect game performance | Capture is unusable | Bounded asynchronous pipeline, throttled UI, post-lap analysis, budgets, and realistic soak tests. |
| Local files become corrupt or incompatible | Evidence and sessions are lost | WAL, atomic chunks, hashes, migrations, recovery, backups, and schema/version manifests. |
| Future/Season Pack protocol work distracts from base F1 25 | Incomplete core and duplicated parser effort | Add the verified F1 25 2026 Season Pack format only post-1.0 through the shared adapter contract and conformance tests. |

## 24. Definition of version 1.0

Version 1.0 is complete only when a clean Windows machine can run the packaged application; the user can configure F1 25, arm capture before entering fullscreen, capture a real session, recover an interrupted one, inspect trustworthy lap evidence, receive an explainable experiment, record matched later laps, verify adherence, and see a reproducible supported/rejected/inconclusive/not-followed/expired outcome. The same flow must be demonstrable without the game through committed synthetic replay fixtures.

All automated tests, clean-build checks, packaging smoke tests, identity audits, schema migrations, dependency scans, and release checks must pass. The repository must contain no secrets, personal captures, generated build output, placeholder production behavior, unresolved `TODO`/`TBD` scope decisions, or uncommitted files.

## 25. Why this is a strong diploma project

ApexLab combines strict binary protocol engineering, high-rate concurrent ingestion, crash-safe local persistence, deterministic replay, time-series and spatial algorithms, interactive scientific visualization, explainable decision logic, uncertainty, experimental evaluation, security, accessibility, packaging, and reproducible delivery. Its originality lies in closing and auditing the coaching loop rather than merely visualizing telemetry or wrapping an AI model. The architecture makes every claim testable and every recommendation traceable to recorded evidence.
