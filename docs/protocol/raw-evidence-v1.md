# ApexLab Raw Evidence Format v1

Status: normative for ApexLab v0.2  
Data extension: `.apxraw`  
Completion-manifest extension: `.apxraw.json`  
Byte order: little-endian unless a field explicitly says otherwise

## 1. Purpose and security statement

Raw Evidence v1 is a bounded, uncompressed, append-only container for the exact UDP datagrams that
passed ApexLab's sender and protocol privacy policy. It preserves enough receive metadata to replay
the same datagrams through the same Application classifier.

A capture is **complete and integrity-verified** only when its final manifest exists and all
validation in this document succeeds. The SHA-256 evidence digest binds every data-file byte and
every canonical manifest byte other than the digest value itself. It detects accidental or
post-capture modification; it does not authenticate the producer and does not make a file trusted.
A malicious party able to replace both files can produce a new matching digest.

Version 1 deliberately does not provide compression, signatures, encryption, crash recovery, or a
durable directory-fsync guarantee. Those are deferred to the v0.4 evidence container. ApexLab keeps
v1 evidence under the private local application-data root and never uploads it automatically.

## 2. Normative vocabulary and primitive encoding

The words MUST, MUST NOT, SHOULD, and MAY are normative.

- `u8`, `u16`, `u32`, and `u64` are unsigned integers of the stated width.
- `i64` is a two's-complement signed 64-bit integer.
- All integer fields are little-endian.
- ASCII fields contain exactly the stated number of bytes and have no terminator unless specified.
- UTC tick fields use .NET ticks: 100 ns intervals since `0001-01-01T00:00:00Z`.
- Arrival tick fields are absolute values from `Stopwatch.GetTimestamp()`, interpreted using the
  positive `Stopwatch.Frequency` recorded in the file header.
- Reserved fields and flag fields MUST be zero. A reader MUST reject a nonzero value.
- Arithmetic used for offsets, lengths, durations, and replay timing MUST be checked. An overflow is
  invalid input, not a reason to wrap or truncate.

The v1 absolute bounds are:

| Item | Bound |
|---|---:|
| Data header | 72 bytes |
| Record header | 60 bytes |
| Footer | 64 bytes |
| UDP payload | 1-65,507 bytes |
| Final data file | 136-536,870,912 bytes (512 MiB) |
| Capture duration | at most 900,000 ms (15 minutes) |
| Manifest | at most 4,096 UTF-8 bytes |
| Protocol ID | 1-64 ASCII bytes |
| Stopwatch frequency | 1-10,000,000,000 ticks/second |

A configuration MAY lower the payload, file, or duration bound and MAY raise the free-space floor.
`maximumFileBytes` MUST remain in `[136, 536870912]`; other configured limits MUST remain within
their absolute v1 bounds.

## 3. Capture identity and generated names

The writer generates a random RFC 4122 version-4 UUID and encodes it as exactly 32 lowercase ASCII
hexadecimal characters without hyphens (`Guid` format `N`). The UUID version and variant bits MUST
identify an RFC 4122 version-4 value; the all-zero value is invalid.

For capture ID `<id>`, the only valid names are:

| Purpose | Generated file name |
|---|---|
| Data staging | `<id>.apxraw.partial` |
| Final data | `<id>.apxraw` |
| Manifest staging | `<id>.apxraw.json.partial` |
| Final completion manifest | `<id>.apxraw.json` |

All four paths MUST be generated under `ApplicationPaths.RawCapturesDirectory`. Public APIs accept a
capture ID, never an absolute or relative file path. A capture ID that is not exactly 32 lowercase
hexadecimal characters with valid version-4 and RFC variant bits is rejected before path generation.

### 3.1 Windows filesystem trust and handle rules

The trust anchor is the local, per-user `ApplicationPaths.RootDirectory`. Its OS-managed parent is
trusted to enforce the user's ACL. A test or command that injects a custom root explicitly assumes
the same trust for that root's parent. Network, device-namespace, and filesystem-root paths are
already forbidden by `ApplicationPaths`.

Path inspection followed by ordinary `FileStream(path, ...)` is not sufficient. The Windows
implementation MUST:

1. Open the application root and capture directory with `CreateFileW`,
   `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`, and share read/write but not delete.
2. Query the opened handles, reject `FileAttributes.ReparsePoint`, verify their normalized final
   paths, and retain both directory handles for the entire writer or reader lifetime. Denying delete
   sharing prevents either trusted directory identity from being replaced during the operation.
3. Create staging files with `CREATE_NEW`; open final files with `OPEN_EXISTING`. Both operations use
   `FILE_FLAG_OPEN_REPARSE_POINT`, generated leaf names, and the retained directory identity.
4. Query each opened file handle and reject a reparse point or a hard-link count other than one.
   Convert that same `SafeFileHandle` to a `FileStream`; never validate one handle and reopen by
   pathname.
5. Open reader handles with read sharing but without write/delete sharing. Open writer staging
   handles with the access needed for write, readback, flush, and handle-based rename, while still
   denying write/delete sharing to other handles.
6. Rename an open staging file with `SetFileInformationByHandle` using a no-replace
   `FILE_RENAME_INFO` operation. The final leaf name is generated and the retained capture-directory
   handle is the rename root. A pathname-based `File.Move` is not permitted.

If the required no-follow, handle-query, sharing, or handle-based-rename capability is unavailable,
the operation fails as an unsafe-path/platform failure. There is no path-based fallback.

## 4. Data-file layout

The final data file is exactly:

```text
DataHeader (72 bytes)
Record[0..recordCount-1] (variable, uncompressed)
Footer (64 bytes)
```

No padding, trailing data, or second footer is permitted. Generated names contain no colon and open
only the unnamed `$DATA` stream; v1 does not require enumeration of unrelated named streams.

### 4.1 Data header

| Offset | Width | Type | Field | Required value or rule |
|---:|---:|---|---|---|
| 0 | 8 | bytes | magic | `41 50 58 52 41 57 31 00` (`APXRAW1\0`) |
| 8 | 2 | u16 | formatVersion | `1` |
| 10 | 2 | u16 | headerLength | `72` |
| 12 | 4 | u32 | flags | `0` |
| 16 | 32 | ASCII | captureId | The generated lowercase capture ID |
| 48 | 8 | i64 | stopwatchFrequency | 1-10,000,000,000 |
| 56 | 8 | i64 | createdUtcTicks | A valid .NET UTC tick value |
| 64 | 4 | u32 | maximumPayloadBytes | 1-65,507 |
| 68 | 4 | u32 | reserved | `0` |

`createdUtcTicks` records when the evidence writer created the staging file. Wall-clock time can be
adjusted during capture; record UTC ticks therefore do not have a monotonicity requirement.

### 4.2 Record header and payload

Every record starts with this 60-byte header and is followed immediately by `payloadLength` bytes.

| Relative offset | Width | Type | Field | Required value or rule |
|---:|---:|---|---|---|
| 0 | 4 | bytes | recordMagic | `52 45 43 31` (`REC1`) |
| 4 | 4 | u32 | recordLength | Exactly `60 + payloadLength` |
| 8 | 8 | i64 | sequence | Positive and strictly greater than the prior sequence |
| 16 | 8 | i64 | arrivalTimestamp | Nonnegative and not less than the prior arrival timestamp |
| 24 | 8 | i64 | receivedUtcTicks | A valid .NET UTC tick value |
| 32 | 1 | u8 | addressFamily | `4` for IPv4 or `6` for IPv6 |
| 33 | 1 | u8 | reserved | `0` |
| 34 | 2 | u16 | senderPort | 0-65,535 |
| 36 | 16 | bytes | senderAddress | Encoding below |
| 52 | 4 | u32 | senderScopeId | IPv6 scope ID; zero for IPv4 |
| 56 | 4 | u32 | payloadLength | 1 through header `maximumPayloadBytes` |
| 60 | payloadLength | bytes | payload | Exact owned UDP payload bytes |

IPv4 addresses use four network-order address bytes followed by twelve zero bytes. IPv6 addresses
use all sixteen network-order address bytes. `senderScopeId` is a 32-bit IPv6 zone index; values
outside that range cannot be represented by v1 and MUST be rejected by the writer.

Sequence gaps are valid and preserve datagrams rejected or dropped before evidence admission.
Duplicate or regressed sequences are invalid. Equal arrival timestamps are valid; regressed arrival
timestamps are invalid. The reader does not infer packet loss from game-frame identifiers.

The writer MUST check, before writing a record, that:

```text
currentLength + recordLength + footerLength <= configuredMaximumFileBytes
```

The footer is always reserved. A record that would exceed the configured file or duration bound is
not partially written.

For a nonempty capture, duration is validated without floating-point arithmetic:

```text
(lastArrivalTimestamp - firstArrivalTimestamp) * 1000
    <= maximumDurationMilliseconds * stopwatchFrequency
```

Both sides are evaluated with checked `Int128` arithmetic.

### 4.3 Footer

The footer is the final 64 bytes of the file.

| Relative offset | Width | Type | Field | Required value or rule |
|---:|---:|---|---|---|
| 0 | 8 | bytes | footerMagic | `41 50 58 46 54 52 31 00` (`APXFTR1\0`) |
| 8 | 2 | u16 | formatVersion | `1` |
| 10 | 2 | u16 | footerLength | `64` |
| 12 | 4 | u32 | flags | `0` |
| 16 | 8 | u64 | recordCount | Number of complete records |
| 24 | 8 | i64 | firstSequence | First record sequence, or `-1` when empty |
| 32 | 8 | i64 | lastSequence | Last record sequence, or `-1` when empty |
| 40 | 8 | u64 | recordsByteLength | Sum of all record lengths |
| 48 | 8 | i64 | firstArrivalTimestamp | First arrival timestamp, or `-1` when empty |
| 56 | 8 | i64 | lastArrivalTimestamp | Last arrival timestamp, or `-1` when empty |

The footer starts at offset `72 + recordsByteLength`, and:

```text
finalDataLength = 72 + recordsByteLength + 64
```

For an empty file, the four first/last sentinel fields MUST all be `-1`. For a nonempty file, they
MUST equal the first and last decoded record values. `recordCount` and `recordsByteLength` MUST
exactly match the streamed records.

## 5. Completion manifest

The manifest is both the completion marker and the integrity metadata. It is UTF-8 without BOM,
contains no leading/trailing whitespace or trailing newline, and is at most 4,096 bytes.

### 5.1 Canonical JSON

The root object MUST contain these properties exactly once and in this order:

1. `schemaVersion` - JSON integer `1`
2. `dataFormatVersion` - JSON integer `1`
3. `captureId` - the 32-byte lowercase capture ID
4. `protocolId` - 1-64 characters matching `[a-z0-9][a-z0-9.-]{0,63}`
5. `dataFileName` - exactly `<captureId>.apxraw`, with no path separator
6. `dataLengthBytes` - positive integer equal to the final data-file length
7. `sha256` - exactly 64 lowercase hexadecimal characters
8. `recordCount` - nonnegative integer equal to the footer
9. `firstSequence` - `null` when empty, otherwise the footer integer
10. `lastSequence` - `null` when empty, otherwise the footer integer
11. `firstArrivalTimestamp` - `null` when empty, otherwise the footer integer
12. `lastArrivalTimestamp` - `null` when empty, otherwise the footer integer
13. `stopwatchFrequency` - integer equal to the data header
14. `createdUtcTicks` - integer equal to the data header
15. `finalizedUtcTicks` - a valid .NET UTC tick value
16. `limits` - the limits object below

The `limits` object MUST contain these properties exactly once and in this order:

1. `maximumDurationMilliseconds`
2. `maximumFileBytes`
3. `minimumFreeSpaceBytes`
4. `maximumPayloadBytes`

All limit values are JSON integers in the `i64` domain. Payload and duration are positive and within
their v1 absolute bounds; file bytes are in `[136, 536870912]`; free-space bytes are in
`[0, 9223372036854775807]`. `minimumFreeSpaceBytes` is integrity-bound historical write-policy
metadata, not a property a reader can re-measure from the completed data. The manifest
`maximumPayloadBytes` MUST equal the data header, and the data MUST satisfy the declared payload,
file, and duration limits.

Canonical integers use base-10 digits with no leading plus sign or leading zero except the value
zero. All permitted strings are ASCII and are emitted without optional escaping. The JSON uses no
insignificant whitespace. Unknown, duplicate, missing, reordered, noncanonical, fractional, or
exponent-form values are invalid.

### 5.2 Evidence-digest domain

The writer first serializes a canonical **manifest preimage** containing every final manifest value
but using exactly 64 ASCII zero characters for the `sha256` value. The evidence digest is:

```text
SHA256(all .apxraw bytes || canonical manifest-preimage bytes)
```

The data portion begins with the data magic and ends with the final footer byte. Concatenation has no
separator because the manifest preimage begins exactly after the manifest-declared
`dataLengthBytes`, which MUST equal the data-file length.

The final canonical manifest is byte-for-byte the preimage except that the 64 zero characters are
replaced in place by the lowercase 64-character digest. The fixed-width replacement cannot change
manifest length, property order, or any other encoded value.

A reader parses and validates the final canonical manifest, reconstructs its preimage by replacing
only the validated `sha256` value with 64 zero characters, and hashes the retained data handle
followed by those exact preimage bytes. Hash comparison MUST use fixed-time comparison of decoded
32-byte values. This binds `protocolId`, finalization time, filenames, counts, limits, and all other
manifest metadata as well as the complete data file.

### 5.3 Illustrative shape

Line breaks below are for readability and are not valid canonical output:

```json
{
  "schemaVersion": 1,
  "dataFormatVersion": 1,
  "captureId": "0123456789abcdef0123456789abcdef",
  "protocolId": "ea-f1-25-v3",
  "dataFileName": "0123456789abcdef0123456789abcdef.apxraw",
  "dataLengthBytes": 136,
  "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
  "recordCount": 0,
  "firstSequence": null,
  "lastSequence": null,
  "firstArrivalTimestamp": null,
  "lastArrivalTimestamp": null,
  "stopwatchFrequency": 10000000,
  "createdUtcTicks": 638000000000000000,
  "finalizedUtcTicks": 638000000000000001,
  "limits": {
    "maximumDurationMilliseconds": 900000,
    "maximumFileBytes": 536870912,
    "minimumFreeSpaceBytes": 1073741824,
    "maximumPayloadBytes": 65507
  }
}
```

The zero hash is illustrative only and would not verify this example.

## 6. Writer state and finalization

The writer has one owner and the states `Created`, `Writing`, `Finalizing`, `Finalized`, and
`Faulted`. It MUST NOT append after finalization begins. Concurrent finalization callers share the
same task; a failed writer cannot be reused.

Default limits are 15 minutes, 512 MiB final data length, a 1 GiB remaining-free-space floor, and a
65,507-byte payload ceiling. Before every operation that can grow either staging file, let
`pendingGrowthBytes` be the exact bytes about to be written across both files. The operation is
allowed only when this checked `Int128` inequality holds:

```text
availableFreeSpaceBytes
    >= minimumFreeSpaceBytes + pendingGrowthBytes
```

At finalization, `pendingGrowthBytes` includes the 64-byte footer and the exact canonical manifest
length. A space check is advisory because another process can consume space afterward; write and
flush failures remain authoritative.

Finalization order is:

1. Stop accepting records.
2. Write the footer to `<id>.apxraw.partial`.
3. Call `FileStream.Flush(flushToDisk: true)` but retain the original data staging handle.
4. Seek and stream the entire data file through SHA-256 using that same handle while enforcing the
   file bound and recording its handle identity.
5. Create the complete canonical manifest preimage, append its bytes to the digest, and replace its
   zero digest field in place to create final canonical manifest bytes.
6. Create `<id>.apxraw.json.partial` with create-new/no-follow semantics, write the final manifest,
   and call `FileStream.Flush(flushToDisk: true)` while retaining that staging handle.
7. Re-query the data handle identity and require it to match step 4, then rename that open handle to
   `<id>.apxraw` with the no-replace handle operation.
8. Rename the still-open manifest handle to `<id>.apxraw.json` with the no-replace handle operation,
   last.
9. Close the renamed handles and retained directory handles.

The final manifest name is the only completion marker. A final data file without a final manifest is
incomplete and MUST NOT be replayed. A staging file or an orphan left by interruption is not
silently recovered by v1. Cleanup is best-effort and cleanup failures are reported with the primary
failure.

`Flush(flushToDisk: true)` requests file-content durability. .NET does not expose a portable
directory fsync for the rename entries; therefore v1 does not claim crash-atomic directory-entry
durability.

## 7. Reader validation order

The reader uses generated capture-ID paths and performs bounded streaming validation:

1. Validate the capture ID and generated names.
2. Establish and retain the no-follow directory/file handles required by section 3.1.
3. Require the final manifest; reject either staging file.
4. Check manifest file length before allocating or reading it.
5. Parse and enforce the exact canonical manifest schema.
6. Require the derived data filename and final data file.
7. Check the data length against the manifest and configured/absolute file bounds.
8. Use the retained data handle to stream the header, records, and footer.
9. Enforce every fixed field, length, sequence, arrival, address, duration, count, and sentinel rule.
10. Reconstruct canonical manifest-preimage bytes, hash the same streamed data bytes followed by
    that preimage, and compare the decoded SHA-256 in fixed time.
11. Rewind the retained handle and publish records only after complete structural and hash
    verification succeeds.

No file-controlled count or length is used for allocation before it is checked against a v1 bound.
The reader may rent one fixed 65,507-byte payload buffer and copy a validated record into exact-owned
memory. It MUST NOT size an array from `recordCount`, `recordsByteLength`, `recordLength`, or manifest
length before validating the relevant bound.

The retained data handle prevents a second pathname lookup or a file replacement between integrity
verification and replay. If the platform cannot provide no-write/no-delete sharing and seek on the
same handle, the reader MUST reject replay rather than verify one object and publish another.

Reader failures are typed at least as: missing/incomplete, unsafe path, unsupported version,
malformed structure, declared-limit violation, truncated data, trailing data, and hash mismatch.
Errors shown outside diagnostic logs MUST NOT expose payload bytes or private local paths.

## 8. Deterministic replay

Replay preserves each recorded sequence value and sender value. It never renumbers gaps.

Immediate mode admits each record with an awaited write to the replay channel. It does not use the
live source's nonblocking saturation policy and therefore cannot introduce scheduler-dependent live
channel drops.

Recorded-time mode accepts an integer `speedPermille` in `[100, 100000]` (0.1x-100x), injects a
`TimeProvider`, and awaits `Task.Delay(delay, timeProvider, cancellationToken)`. For each record,
target elapsed `TimeSpan` ticks from the first record are calculated with checked `Int128` arithmetic
and half-up rounding:

```text
numerator =
  (arrivalTimestamp - firstArrivalTimestamp)
  * TimeSpan.TicksPerSecond
  * 1000

denominator = stopwatchFrequency * speedPermille

targetTicks = (numerator + denominator / 2) / denominator
```

The delay before a record is `max(0, targetTicks - replayElapsedTicks)`. Equal arrival timestamps
require no delay. Very large delays remain cancellable. Tests use virtual time and never rely on
wall-clock sleeps.

The writer MUST copy the exact `ITelemetryProtocolAdapter.ProtocolId` used for live classification
into the manifest. Protocol IDs are semantic version identifiers: an adapter whose classification
contract changes incompatibly MUST use a new ID.

Before replay, the composition root resolves the integrity-bound manifest `protocolId` through an
exact, ordinal protocol-adapter registry lookup and verifies that the resolved adapter reports that
same ID. No match is an `UnsupportedProtocol` failure; the CLI does not fall back to its current
default adapter.

Replay then invokes the same `CaptureIngestionCoordinator`, sender policy, and protocol adapter
identity as live capture. A valid evidence file therefore produces the classification contract
identified at capture time for the same payloads; replay-source accounting remains distinct from
live UDP admission accounting.

## 9. Privacy and fixture rules

Version 1 stores sender metadata and exact payload bytes and is private evidence. Logs, CLI JSON,
exceptions, and UI status MUST NOT include payloads, session UID values, sender endpoints, usernames,
or absolute evidence paths.

No `.apxraw`, `.apxraw.json`, or private-validation file is committed. Format tests independently
author records in memory or under a test-owned temporary directory and delete them after validation.
Committed synthetic packet-byte fixtures remain subject to the repository data-policy metadata.
