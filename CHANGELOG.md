# Changelog

All notable changes to ApexLab are documented in this file.

## [0.2.0] - 2026-08-09

### Added

- Strict base-F1-25 v3 header and packet-envelope validation with bounds-checked little-endian
  decoding and an explicit descriptor catalog.
- Same-PC loopback UDP ingestion with bounded buffering, sender policy, exact source/classifier
  accounting, and privacy-safe live traffic probing.
- Integrity-bound raw evidence capture with canonical manifests, atomic finalization, bounded storage
  limits, corruption detection, and deterministic replay.
- Native Drive workflow controls for arming, stopping, draining, finalizing, and inspecting aggregate
  capture status without retaining privacy-excluded packet classes.
- Guided private F1 25 validation covering a real offline Time Trial probe and capture, two matching
  deterministic replays, sequence-gap preservation, protocol assumptions, manifest integrity, and an
  enforced synthetic rate gate of at least twice the privately observed peak.

### Safety

- Malformed, oversized, unsupported, privacy-excluded, unexpected-sender, saturated, interrupted,
  corrupted, and reordered inputs have explicit bounded outcomes and automated adversity coverage.
- Capture lifecycle ownership is serialized across start, stop, cancellation, host shutdown, and
  deferred cleanup so terminal evidence is not reported before finalization completes.
- The private validation result committed to Git is limited to a fixed 14-field safe schema; payloads,
  capture identities, hashes, paths, sender/session identifiers, screenshots, and exact rates remain
  local.

### Known limitations

- This release supports base F1 25 v3 telemetry from offline Time Trial on the same Windows PC; it
  does not support the Season Pack protocol, multiplayer, another simulator, or remote telemetry.
- Evidence uses one bounded uncompressed raw file. Compression, chunk rotation, recovery, indexing,
  and quota management remain scheduled for v0.4.
- Lap reconstruction, comparable-lap analysis, corner findings, and coaching experiments begin with
  the provisional Bahrain vertical slice in v0.3.
- Distribution remains an unsigned portable ZIP with no installer, signing certificate, or
  auto-updater.

## [0.1.0] - 2026-07-20

### Added

- Native Windows 11 WPF application shell on .NET 10 with validated local settings and explicit
  application identity.
- Single-instance lifecycle coordination with bounded startup, cancellation, and shutdown behavior.
- Crash-safe SQLite schema version 1, guarded migrations, schema inspection, and rollback protection.
- Headless application smoke mode that initializes real settings and persistence without constructing
  the interactive shell.
- Locked restore, formatting, warnings-as-errors build, 177 automated tests, TRX evidence, and Cobertura
  coverage collection.
- Reproducible self-contained `win-x64` ZIP packaging with README, third-party notices, and SHA-256.
- GitHub Actions verification, packaging, smoke testing, and exact release-artifact upload.

### Safety

- Repository checks for conflict markers, credential-shaped content, personal-data paths, oversized
  fixtures, and generated source-tree changes.
- Destructive release cleanup is confined to exact ignored artifact paths and rejects tracked content,
  tracked descendants, and filesystem reparse points.
- Package smoke execution uses fresh data/result targets, bounded exact-process cleanup, path-safe
  arguments, atomic result publication, and primary-error-preserving cleanup.
- Self-contained package evidence includes a successful smoke run with no PATH-visible `dotnet.exe`,
  invalid global .NET roots, and multilevel runtime lookup disabled.

### Known limitations

- This foundation release does not yet capture or analyze F1 25 telemetry.
- Distribution is an unsigned portable ZIP; there is no installer, signing certificate, or auto-updater.
- No public source-code license is granted while university and intellectual-property requirements are
  being confirmed.
