# ApexLab Data Policy

## Never committed

- Real personal telemetry sessions, raw UDP recordings, exports, and backup bundles.
- Local SQLite databases, settings, logs, crash dumps, performance profiles, and filesystem paths.
- Identity mappings, consent material, credentials, tokens, signing keys, or machine configuration.
- EA specification attachments, game files, screenshots, artwork, logos, fonts, audio, or other vendor assets.

Git LFS is not a privacy boundary and does not make private telemetry safe to commit.

## Allowed test data

The repository may contain only small independently authored synthetic packets and synthetic replay sessions needed for deterministic tests. Each fixture directory must include metadata describing:

- purpose and generation method;
- protocol/schema version;
- whether any real source contributed to it;
- redistribution basis;
- SHA-256 checksum;
- expected decoder or replay result.

A personal capture may be converted into a synthetic fixture only by rebuilding the minimum fields from an independently documented test scenario. Cropping or pseudonymizing a real packet stream does not automatically make it redistributable.

## Personal engineering data

Real sessions stay outside Git in the local ApexLab data root or an encrypted/restricted backup selected by the owner. They may be used for engineering and evaluation but remain separate from source history.

Evaluation data is divided into a development corpus and a frozen personal holdout. Thresholds and optional models are tuned only on development data. The frozen set is opened only for declared evaluation, and the result remains valid even if the learned model fails to improve the deterministic baseline.

## Deletion and export

Export is always explicit and includes a manifest, versions, and checksums. Player-only derived export is the default. A raw export preserves required F1 datagrams exactly; although Participants and Lobby Info packets are excluded because they carry names or network-player identity, other required packet types contain anonymous arrays for multiple vehicle slots. The application warns about that distinction before raw export.

Deletion resolves an exact target, communicates whether recovery is possible, and never recursively operates on an unresolved or broad path.
