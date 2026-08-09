# Protocol Sources

## EA SPORTS F1 25

- Authoritative page: <https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347>
- Accessed: 2026-07-19; source attachments retrieved and rechecked: 2026-07-24.
- Official page revision at retrieval: Version 10.0.
- Initial compatibility target: base F1 25 UDP, packet format 2025, game year 25, v3 data output.
- Header contract: 29 bytes for the supported specification version.

The official page currently publishes the base F1 25 v3 attachment beside the distinct 2026
Season Pack attachment. ApexLab implements only the base attachment until a separate adapter is
planned, implemented, and validated.

### Locked source identity

| Attachment | Bytes | SHA-256 | Official download |
| --- | ---: | --- | --- |
| `Data Output from F1 25 v3.pdf` | 659792 | `850199D1EA817B887B150118095C5CA86527A397D578D53FD5C83427636B92D5` | <https://forums.ea.com/t5/s/tghpe58374/attachments/tghpe58374/f1-games-game-info-hub-en/61/4/Data%20Output%20from%20F1%2025%20v3.pdf> |
| `F1 25 Telemetry Output Structures.txt` | 51656 | `49CCEADC60A1A6403A59620C6FC09DA377048BA004B15C199399F775B9B3FC69` | <https://forums.ea.com/t5/s/tghpe58374/attachments/tghpe58374/f1-games-game-info-hub-en/61/5/F1%2025%20Telemetry%20Output%20Structures.txt> |

The PDF is the authority for packet versions and states version 1 for every packet family in the
table below. The structure attachment independently confirms the packet IDs, packed layouts, and
datagram sizes.

### Implemented compatibility matrix

| ID | Family | Version | Datagram bytes | Evidence disposition |
| ---: | --- | ---: | ---: | --- |
| 0 | Motion | 1 | 1349 | Allowed |
| 1 | Session | 1 | 753 | Allowed |
| 2 | Lap Data | 1 | 1285 | Allowed |
| 3 | Event | 1 | 45 | Allowed |
| 4 | Participants | 1 | 1284 | Identity-bearing; excluded |
| 5 | Car Setups | 1 | 1133 | Allowed |
| 6 | Car Telemetry | 1 | 1352 | Allowed |
| 7 | Car Status | 1 | 1239 | Allowed |
| 8 | Final Classification | 1 | 1042 | Allowed |
| 9 | Lobby Info | 1 | 954 | Identity-bearing; excluded |
| 10 | Car Damage | 1 | 1041 | Allowed |
| 11 | Session History | 1 | 1460 | Allowed |
| 12 | Tyre Sets | 1 | 231 | Allowed |
| 13 | Motion Ex | 1 | 273 | Allowed |
| 14 | Time Trial | 1 | 101 | Allowed |
| 15 | Lap Positions | 1 | 1131 | Allowed |

### Field-level privacy audit

Participants is excluded because each entry contains a network-player identifier, participant name,
and online-name/telemetry visibility settings. Lobby Info is excluded because each lobby entry
contains a participant name and visibility settings. Recognizing these families is necessary for
honest diagnostics, but their payloads must never become compatible evidence.

The other base-v3 packet families contain no participant-name string or network-player identity
field. Their fixed per-vehicle arrays remain anonymous without the excluded identity mappings.
Session, weekend, frame, and vehicle-slot identifiers are retained only as protocol/session
correlation values; they do not restore the excluded mapping to a named participant. Any new packet
family or protocol revision requires this field-level audit again and defaults to exclusion until the
disposition is explicit.

The authoritative page may change. Each protocol-adapter release records the exact source date/version it implemented and retains independently authored conformance tests.

Local vendor attachments belong only in the ignored `vendor-specs/` directory. Production parser code, synthetic fixtures, tests, and protocol notes are independently authored from the published field definitions. Vendor files are never copied into source control or release packages.

The separately published F1 25 2026 Season Pack format would require a distinct adapter and compatibility statement if it is later purchased and supported. The base F1 25 parser never guesses how to interpret an unknown format.
