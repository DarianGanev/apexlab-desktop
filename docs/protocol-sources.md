# Protocol Sources

## EA SPORTS F1 25

- Authoritative page: <https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347>
- Accessed: 2026-07-19; rechecked: 2026-07-24 (official page Version 10.0).
- Initial compatibility target: base F1 25 UDP, packet format 2025, game year 25, v3 data output.
- Header contract: 29 bytes for the supported specification version.

The official page currently publishes the base F1 25 v3 attachment beside the distinct 2026
Season Pack attachment. ApexLab implements only the base attachment until a separate adapter is
planned, implemented, and validated.

The authoritative page may change. Each protocol-adapter release records the exact source date/version it implemented and retains independently authored conformance tests.

Local vendor attachments belong only in the ignored `vendor-specs/` directory. Production parser code, synthetic fixtures, tests, and protocol notes are independently authored from the published field definitions. Vendor files are never copied into source control or release packages.

The separately published F1 25 2026 Season Pack format would require a distinct adapter and compatibility statement if it is later purchased and supported. The base F1 25 parser never guesses how to interpret an unknown format.
