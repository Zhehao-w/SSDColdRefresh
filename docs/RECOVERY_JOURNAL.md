# Recovery journal format (version 1 specification)

This is the normative design for a future implementation; the initial foundation intentionally contains no production serializer.

## Layout and encoding

All integers are unsigned little-endian. Offsets are absolute. Reserved bytes must be zero when written and ignored when read. The fixed file layout is:

| Offset | Length | Meaning |
|---:|---:|---|
| 0 | 4096 | Header A |
| 4096 | 4096 | Header B |
| 8192 | `ChunkLength` | Original chunk payload (capacity fixed to configured maximum, normally 64 MiB) |

Each 4096-byte header has this prefix; bytes 192..4063 are reserved and bytes 4064..4095 are `HeaderHash`.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 8 | Magic: ASCII `CRJNL001` |
| 8 | 4 | FormatVersion = 1 |
| 12 | 4 | HeaderSize = 4096 |
| 16 | 8 | SequenceNumber |
| 24 | 4 | TransactionState |
| 28 | 4 | Flags (zero in v1) |
| 32 | 16 | SessionId (GUID RFC 4122/network byte order) |
| 48 | 8 | VolumeSerialNumber |
| 56 | 16 | FileId (FILE_ID_128 bytes verbatim) |
| 72 | 8 | OriginalFileSize |
| 80 | 8 | ChunkOffset |
| 88 | 8 | ChunkLength |
| 96 | 32 | OriginalChunkHash (SHA-256) |
| 128 | 32 | JournalDataHash (SHA-256) |
| 160 | 8 | PayloadOffset = 8192 |
| 168 | 8 | PayloadCapacity |
| 176 | 16 | Reserved |
| 192 | 3872 | Reserved |
| 4064 | 32 | SHA-256 of bytes 0..4063 |

State codes are: 0 `Empty`, 1 `Prepared`, 2 `TargetWritten`, 3 `TargetVerified`, 4 `Committed`, 5 `RollbackRequired`, 6 `RollbackSucceeded`, 7 `RecoveryRequired`. Unknown codes are invalid and fail closed. `(ChunkOffset + ChunkLength)` must not overflow and must be at most `OriginalFileSize`; lengths/capacity must match the journal file bounds.

## Creating `Prepared`

Read the complete target chunk before any journal state change; compute `OriginalChunkHash`; write payload; flush; read payload back; compute `JournalDataHash`; require both hashes equal. Then write a complete `Prepared` header with a sequence greater than both valid headers into the inactive/lower-sequence slot, flush, read it back, validate all bounds and `HeaderHash`, and only then expose `Prepared`. A torn payload/header remains unusable. Payload verification occurs whenever recovery opens the journal, not merely when it was created.

## Header updates

Never update an active header in place. Copy all immutable transaction fields, increment sequence without wrapping (exhaustion fails closed), change state, compute checksum, write the inactive header, flush, read back, and validate. The valid header with greatest sequence is authoritative. Equal-sequence headers must be byte-identical or recovery is ambiguous and blocked. A committed slot may be reused only by first writing and verifying the next payload and then publishing a higher-sequence `Prepared` header.

## Runtime transitions and recovery

Normal: `Empty/Committed -> Prepared -> TargetWritten -> TargetVerified -> Committed`. Error: `Prepared/TargetWritten/TargetVerified -> RollbackRequired -> RollbackSucceeded`; any indeterminate rollback publishes `RecoveryRequired`. State publication never substitutes for data flush/read-back verification.

At startup, validate both headers, choose authority, validate identity and file size using a locked handle, and validate payload hash. For `Prepared` or `RollbackRequired`, restore payload at the recorded offset, flush, read back, and require `OriginalChunkHash`. For `TargetWritten`, read target: if it matches original, publish `TargetVerified` then `Committed`; otherwise restore. For `TargetVerified`, independently read/hash before commit. `RecoveryRequired` retries verified restoration but is never silently cleared. No matching identity means preserve and block. `Committed`, `RollbackSucceeded`, and `Empty` need no content write; `RollbackSucceeded` still ends the prior session as suspect.

The journal stores plaintext. SQLite history is neither consulted nor trusted for recovery.
