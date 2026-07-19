# ApexLab Desktop

ApexLab is a local-first Windows telemetry coach for EA SPORTS F1 25 Time Trial. It captures the game's official UDP stream on the same PC, reconstructs comparable laps, identifies repeatable corner-level time loss, proposes one evidence-based driving experiment, and measures whether later laps support it.

This is a personal software-engineering diploma project, not a startup MVP. Its technical focus is reliable binary protocol handling, bounded concurrent ingestion, crash-safe persistence, deterministic replay, spatial time-series analysis, explainable coaching, uncertainty, scientific visualization, and reproducible Windows delivery.

## Status

The approved architecture and implementation roadmap are complete. The executable Windows foundation is the next milestone.

- [Product and engineering design](docs/superpowers/specs/2026-07-19-apexlab-desktop-design.md)
- [30-week roadmap](docs/apexlab-roadmap.md)
- [v0.1.0 implementation plan](docs/superpowers/plans/2026-07-19-apexlab-v0.1-foundation.md)

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

## Planned development commands

After the v0.1.0 solution is scaffolded with the pinned .NET 10 SDK:

```powershell
dotnet restore ApexLab.slnx --locked-mode
dotnet format ApexLab.slnx --verify-no-changes
dotnet build ApexLab.slnx -c Release --no-restore
dotnet test ApexLab.slnx -c Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
```

## Repository policy

Development follows GitFlow: released states live on `main`, integration on `develop`, and work on short `feature/*` branches. Releases use `release/*`, merge back to `develop`, and receive annotated semantic-version tags. Commits use the repository owner's Git identity and describe coherent, working changes.

Real telemetry, local databases, logs, backups, exports, vendor specification downloads, credentials, and signing material must never be committed. Only small synthetic fixtures with provenance and checksums may enter tests. See [data policy](docs/data-policy.md).

## Legal notice

ApexLab is an unofficial educational project. It is not affiliated with, authorized by, sponsored by, or endorsed by Electronic Arts Inc., Formula One World Championship Limited, or their affiliates. Product names and trademarks belong to their respective owners. This repository does not distribute official logos, artwork, game assets, fonts, telemetry specification attachments, or other copyrighted vendor material.

No public source-code license is granted while university and intellectual-property requirements are being confirmed.
