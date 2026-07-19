# Security Policy

## Supported development state

Until version 1.0, only the newest tagged release on `main` is considered supported. Development snapshots on `develop` are not releases.

## Reporting a problem

Do not open a public issue containing personal telemetry, local filesystem paths, private IP addresses, database files, captures, logs with private data, credentials, signing material, or exploit details. Contact the repository owner privately through the email listed on the owner's GitHub profile.

## Security boundaries

F1 UDP is unauthenticated input. ApexLab listens only on loopback by default and validates packet source policy, format, version, type, length, field bounds, and rate. LAN binding is an explicit advanced option; an IP allowlist is filtering, not authentication.

The application must remain bounded and safe when it receives malformed, random, duplicated, reordered, oversized, or high-rate datagrams. It requires no administrator privileges, driver, game-memory access, process injection, or game hook.

Local capture bundles are also untrusted input. Import validates paths, file counts, expanded sizes, schemas, and checksums in a temporary directory before any file enters the application data root.

Version 1.0 performs no silent network upload and collects no crash telemetry. Diagnostic reports are user-initiated and redacted by default.
