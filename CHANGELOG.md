# Changelog

All notable changes to ApexLab are documented in this file.

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
