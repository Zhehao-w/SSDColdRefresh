# Recovery journal format (version 1 specification)

This is the normative design for a future implementation; the foundation intentionally contains no production serializer. A production reader must reject any violation rather than infer missing values.

## Fixed layout and payload capacity

All integers are unsigned little-endian except signed `FILETIME` values as noted. Offsets are absolute. Reserved bytes must be zero when written and ignored when read.

| Offset | Length | Meaning |
|---:|---:|---|
| 0 | 4096 | Header A |
| 4096 | 4096 | Header B |
| 8192 | `PayloadCapacity` | Reusable original-byte payload slot |

The journal file size is exactly `8192 + PayloadCapacity`. `PayloadCapacity` is fixed when the journal is created (normally 64 MiB); creation preallocates or explicitly sets that complete file length. Reuse never truncates the file. Every valid active header requires `0 < ChunkLength <= PayloadCapacity` and `PayloadOffset == 8192`.

Only payload bytes `[0, ChunkLength)` are authoritative for the active transaction and only those bytes are read or SHA-256 hashed. Bytes `[ChunkLength, PayloadCapacity)` are ignored and may contain stale bytes from a prior, longer chunk. They must never influence validation or recovery. This rule makes a short final chunk safe without clearing the unused slot tail.

## Header fields

Each 4096-byte header uses bytes 0..4063 for fields/reserved space and bytes 4064..4095 for `HeaderHash`.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 8 | Magic: ASCII `CRJNL001` |
| 8 | 4 | FormatVersion = 1 |
| 12 | 4 | HeaderSize = 4096 |
| 16 | 8 | SequenceNumber |
| 24 | 4 | TransactionState |
| 28 | 4 | Flags (zero in v1) |
| 32 | 16 | SessionId: UUID bytes in RFC 4122/network order |
| 48 | 8 | VolumeSerialNumber |
| 56 | 16 | FileId: opaque `FILE_ID_128.Identifier[16]`, byte-for-byte |
| 72 | 8 | OriginalFileSize |
| 80 | 8 | ChunkOffset |
| 88 | 8 | ChunkLength |
| 96 | 32 | OriginalChunkHash: SHA-256 of exactly `ChunkLength` source bytes |
| 128 | 32 | JournalDataHash: SHA-256 of exactly `ChunkLength` payload read-back bytes |
| 160 | 8 | PayloadOffset = 8192 |
| 168 | 8 | PayloadCapacity |
| 176 | 8 | CreationTime: original signed 64-bit `FILE_BASIC_INFO.CreationTime` |
| 184 | 8 | LastAccessTime: original signed 64-bit `FILE_BASIC_INFO.LastAccessTime` |
| 192 | 8 | LastWriteTime: original signed 64-bit `FILE_BASIC_INFO.LastWriteTime` |
| 200 | 8 | ChangeTime: original signed 64-bit `FILE_BASIC_INFO.ChangeTime` |
| 208 | 4 | FileAttributes: original `FILE_BASIC_INFO.FileAttributes` bit field |
| 212 | 3852 | Reserved; zero on write |
| 4064 | 32 | HeaderHash: SHA-256 of bytes 0..4063 |

The four timestamp fields preserve the raw signed 64-bit Windows values without epoch conversion, normalization, or timezone conversion. `FileAttributes` preserves the raw 32-bit value. These values are captured through the locked target handle before `Prepared`, are repeated unchanged in every state header, and are sufficient for a fresh process to restore the original `FILE_BASIC_INFO`. They are recovery data, not merely in-memory bookkeeping.

`FILE_ID_128` is never parsed as a GUID and undergoes no byte swapping. Session IDs remain GUIDs, but are serialized explicitly as RFC 4122/network-order bytes: for `00112233-4455-6677-8899-aabbccddeeff`, header bytes are `00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF`. Implementations must not use the default mixed-endian `Guid.ToByteArray()` representation.

State codes are: 0 `Empty`, 1 `Prepared`, 2 `TargetWritten`, 3 `TargetVerified`, 4 `Committed`, 5 `RollbackRequired`, 6 `RollbackSucceeded`, 7 `RecoveryRequired`, 8 `AbortedSafe`. Unknown codes are invalid and fail closed. `(ChunkOffset + ChunkLength)` must not overflow and must be at most `OriginalFileSize`; capacity and journal file bounds must agree.

`Committed` means durable evidence proves the target write was flushed and its read-back matched `OriginalChunkHash`. `AbortedSafe` means logical content has been verified safe but completion of the refresh write is unproven; it is not refresh success and must never update `LastRefreshTime`.

## Creating `Prepared`

While holding the same locked target handle, capture identity, expected size, and raw `FILE_BASIC_INFO`; read the complete target chunk; compute `OriginalChunkHash`; write exactly `ChunkLength` payload bytes; flush; read back exactly those bytes; compute `JournalDataHash`; and require both hashes equal. Then write a complete `Prepared` header with a sequence exactly one greater than the authoritative header into the inactive/lower-sequence slot, flush, read it back, validate all bounds and `HeaderHash`, and only then expose the returned authoritative record. A torn payload/header remains unusable.

## Header updates and authoritative records

Never update an active header in place. Copy every immutable transaction field, increment sequence by exactly one without wrapping, change state, compute the header hash, write the inactive header, flush, read back, and validate. The valid header with greatest sequence is authoritative. Equal-sequence headers must be byte-identical or recovery is ambiguous and blocked.

Every successful publication returns that newly authoritative `JournalRecord`. The caller must supply that exact record—matching both sequence and state—to the next publication. Stale callers and illegal transitions fail closed; a journal implementation must not silently repair their sequence. A committed/aborted slot is reused only by writing and verifying the next payload and then publishing a higher-sequence `Prepared` header.

## Runtime state and write-attempt boundary

Normal: `Empty/Committed/AbortedSafe -> Prepared -> TargetWritten -> TargetVerified -> Committed`. Before invoking target write, the coordinator performs its final cancellation check and fault boundary. The in-memory `targetWriteAttempted` flag is set immediately before the write call, with no await or cancellation point between them.

If an error or caller cancellation occurs after `Prepared` but before write attempt, read/hash the still-locked target. If it matches, publish `AbortedSafe` without writing target data. If it cannot be proven unchanged, retain the journal as `RecoveryRequired`. After write attempt, partial modification is assumed: publish `RollbackRequired`, restore only verified payload bytes, flush, read back, verify, and publish `RollbackSucceeded`; indeterminate rollback preserves recovery state.

`TargetWritten` records only that the write call returned and does not prove flush or verification. `TargetVerified` is published only after flush plus matching target read-back. Only an authoritative `TargetVerified` can transition to `Committed`.

## Startup reconciliation: read before write

Before new work, a recovery reader must:

1. Validate both headers and select the unambiguous highest valid sequence.
2. Validate bounds and hash exactly the first `ChunkLength` payload bytes.
3. Open/lock and validate volume serial, opaque file ID, and expected file size.
4. Read the current target chunk before any recovery write.
5. SHA-256 exactly the current `ChunkLength` target bytes.

For `Prepared`, `TargetWritten`, `RollbackRequired`, or an automatically recoverable `RecoveryRequired`, matching target content causes **no target write** and transitions to `AbortedSafe`. If content differs, restore the verified payload, flush, read back, SHA-256 verify, restore the journaled `FILE_BASIC_INFO`, and then publish `AbortedSafe`. Content safety does not prove refresh completion.

For `TargetVerified`, independently matching target content plus the durable state is sufficient to publish `Committed`; a mismatch restores verified original bytes and becomes `AbortedSafe`. `Committed`, `AbortedSafe`, `RollbackSucceeded`, and `Empty` require no content write. No matching identity, invalid metadata, or unverifiable payload means preserve the journal and block. SQLite is neither consulted nor trusted for recovery.

The journal stores plaintext. Metadata restoration is complete only after `SetFileInformationByHandle` succeeds and subsequent inspection confirms the required values; failure stops recovery and retains diagnostics/journal state.
