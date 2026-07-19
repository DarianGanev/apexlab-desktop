# ADR 0001: Windows local-first desktop product

- **Status:** Accepted
- **Date:** 2026-07-19

## Context

The owner plays F1 25 on Windows with a wheel and pedals and wants a personal diploma product with no mandatory cost. A prior phone-receiver concept added Wi-Fi, mobile lifecycle, battery, and platform constraints that did not improve the central coaching problem. The product does not require remote users, accounts, or collaboration.

The engineering priorities are reliable same-PC telemetry ingestion, reproducible analysis, rich desktop visualization, and an auditable coaching experiment loop.

## Decision

Build a Windows-only application using C#, WPF, and .NET 10 LTS. Receive F1 25 UDP through loopback by default. Run capture, persistence, replay, analysis, and coaching locally. Store structured metadata in SQLite, accepted raw datagrams in versioned compressed checksummed chunks, and canonical/analysis output as reproducible caches. Publish a self-contained `win-x64` ZIP.

Do not build a mobile/web client, backend, authentication system, or cloud dependency for version 1.0. LAN reception is an opt-in advanced mode. Game hooking, memory access, drivers, administrator privileges, and an in-game overlay are prohibited.

## Consequences

- WPF and Windows packaging become intentional platform dependencies.
- The UDP receiver, writer, analysis workers, and UI dispatcher must be isolated so the application does not affect the game.
- Local database migrations, chunk recovery, backup, import/export, and low-disk behavior become first-class reliability work.
- Dense synchronized charts and keyboard/mouse analysis can be substantially better than on a phone.
- No server operating cost, account security surface, or internet availability is required.
- A future second platform would require new UI and platform infrastructure but can reuse the pure domain model if boundaries are preserved.
