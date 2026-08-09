# ApexLab Desktop

ApexLab is a local-first Windows telemetry coach for EA SPORTS F1 25 Time Trial. It captures the game's official UDP stream on the same PC, reconstructs comparable laps, identifies repeatable corner-level time loss, proposes one evidence-based driving experiment, and measures whether later laps support it.

This is a personal software-engineering diploma project, not a startup MVP. Its technical focus is reliable binary protocol handling, bounded concurrent ingestion, crash-safe persistence, deterministic replay, spatial time-series analysis, explainable coaching, uncertainty, scientific visualization, and reproducible Windows delivery.

## Status

The native Windows foundation is executable and tested: it includes the WPF shell, validated local
settings, single-instance lifecycle coordination, safe SQLite schema migration, headless package
smoke testing, and reproducible verification/packaging scripts. F1 telemetry product features are
the next milestone.

- [Product and engineering design](docs/superpowers/specs/2026-07-19-apexlab-desktop-design.md)
- [30-week roadmap](docs/apexlab-roadmap.md)
- [v0.1.0 implementation plan](docs/superpowers/plans/2026-07-19-apexlab-v0.1-foundation.md)
- [Changelog](CHANGELOG.md)

## Central product loop

```text
comparable laps
  -> personal reference
  -> repeated evidence-backed issue
  -> one bounded experiment
  -> matched later laps
  -> supported / inconclusive / rejected outcome
```

The coach is deterministic and evidence-first. It must abstain when data quality, context, repeatability, or confidence is insufficient. A learned personal ranker is optional and must beat the deterministic baseline before it can be used.

## Initial scope

- Native Windows 11 application using C#, WPF, and .NET 10 LTS.
- Base F1 25, official F1 25 v3 UDP, Time Trial, wheel and pedals.
- Same-PC loopback capture on `127.0.0.1:20777` by default.
- Fully local capture, SQLite metadata, binary telemetry chunks, replay, analysis, coaching, backup, and export.
- Self-contained `win-x64` package with no paid API or mandatory service.

There is no mobile/web client, backend, account system, smartwatch, IoT device, game injection, in-game overlay, or cloud dependency in version 1.0.

## Build and verification

Prerequisites are Windows 11 x64, PowerShell 5.1 or newer, and the .NET SDK version pinned in
`global.json` (currently 10.0.302). Restore remains locked to committed NuGet graphs.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
```

Verification performs the SDK pin check, locked restore, formatting check, warnings-as-errors
Release build, non-soak tests, TRX/Cobertura output, and repository hygiene checks. Generated test
evidence is written below ignored `artifacts/test-results`.

Run the desktop shell from source with:

```powershell
dotnet run --project src/ApexLab.App/ApexLab.App.csproj -c Release
```

## Package and smoke test

Create a guarded self-contained Windows package and verify the extracted executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish.ps1 -Version 0.1.0 -CleanKnownOutputs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/SmokeTest.ps1 -PackagePath artifacts/ApexLab-0.1.0-win-x64.zip
```

The publish script refuses silent overwrites, verifies `Version.props`, includes the README and
third-party notices, and writes `ApexLab-0.1.0-win-x64.zip.sha256`. The smoke script extracts to new
temporary storage, launches no window, enforces an exact-PID timeout, initializes real settings and
SQLite schema version 1, validates the result, and proves the temporary tree can be removed.

The package is currently unsigned and may show a Windows reputation/SmartScreen warning. It is a
portable ZIP, not an installer. See the [v0.1.0 verification record](docs/verification/v0.1.0.md).

## Repository policy

Development follows GitFlow: released states live on `main`, integration on `develop`, and work on short `feature/*` branches. Releases use `release/*`, merge back to `develop`, and receive annotated semantic-version tags. Commits use the repository owner's Git identity and describe coherent, working changes.

Real telemetry, local databases, logs, backups, exports, vendor specification downloads, credentials, and signing material must never be committed. Only small synthetic fixtures with provenance and checksums may enter tests. See [data policy](docs/data-policy.md).

## Legal notice

ApexLab is an unofficial educational project. It is not affiliated with, authorized by, sponsored by, or endorsed by Electronic Arts Inc., Formula One World Championship Limited, or their affiliates. Product names and trademarks belong to their respective owners. This repository does not distribute official logos, artwork, game assets, fonts, telemetry specification attachments, or other copyrighted vendor material.

No public source-code license is granted while university and intellectual-property requirements are being confirmed.
