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

For a non-terminal active transaction, only payload bytes `[0, ChunkLength)` are authoritative and only those bytes are read or SHA-256 hashed. Bytes `[ChunkLength, PayloadCapacity)` are ignored and may contain stale bytes from a prior, longer chunk. For terminal authority (`Empty`, `Committed`, `AbortedSafe`, or `RollbackSucceeded`), no payload bytes are recovery-authoritative: the slot may already contain an unpublished next transaction. Startup must not compare terminal-header payload hashes. This rule makes both short final chunks and pre-publication slot reuse safe.

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

State codes are: 0 `Empty`, 1 `Prepared`, 2 `TargetWritten`, 3 `TargetVerified`, 4 `Committed`, 5 `RollbackRequired`, 6 `RollbackSucceeded`, 7 `RecoveryRequired`, 8 `AbortedSafe`. Unknown codes are invalid and fail closed. `(ChunkOffset + ChunkLength)` must not overflow and must be at most `OriginalFileSize`; capacity and journal file bounds must agree. `Empty` is the logical state of a brand-new journal with no valid header; v1 does not publish an `Empty` header or insert `Empty` between transactions.

`Committed` means durable evidence proves the target write was flushed and its read-back matched `OriginalChunkHash`, and original metadata was restored and verified. `AbortedSafe` means logical content and original metadata have been verified safe but completion of the refresh write is unproven; it is not refresh success and must never update `LastRefreshTime`. `RollbackSucceeded` likewise requires verified restored content and metadata.

## Creating `Prepared`

While holding the same locked target handle, capture identity, expected size, and raw `FILE_BASIC_INFO`; read the complete target chunk; compute `OriginalChunkHash`; write exactly `ChunkLength` payload bytes; flush; read back exactly those bytes; compute `JournalDataHash`; and require both hashes equal. Core then calls the dedicated `PublishPrepared` operation with the new immutable transaction fields and hashes. The journal—not Core—selects sequence 1 when no valid authority exists or the previous authoritative sequence plus one when reusing the slot. It writes the complete `Prepared` header into the inactive/lower-sequence slot, flushes, reads it back, validates bounds and `HeaderHash`, selects it as authority, and returns that exact record.

Before `Prepared` becomes authoritative, the previous terminal header remains authoritative even though its old payload may have been overwritten. A crash/torn header at this point leaves no new recovery obligation. New `Prepared` is permitted only with no existing authority or after `Empty`, `Committed`, `AbortedSafe`, or `RollbackSucceeded`; it is rejected after `Prepared`, `TargetWritten`, `TargetVerified`, `RollbackRequired`, or `RecoveryRequired`. Sequence wraparound is rejected. No intermediate `Empty` publication is required.

## Header updates and authoritative records

Never update an active header in place. Copy every immutable transaction field, increment sequence by exactly one without wrapping, change state, compute the header hash, write the inactive header, flush, read back, and validate. The valid header with greatest sequence is authoritative. Equal-sequence headers must be byte-identical or recovery is ambiguous and blocked.

Every successful publication returns that newly authoritative `JournalRecord`. `PublishPrepared` owns journal-global sequence selection and may replace immutable transaction fields only from an allowed resolved state. Thereafter the caller must supply that exact record—matching sequence, state, and immutable fields—to each `PublishState` transition. Stale callers and illegal transitions fail closed. Sequence numbers increase across every transaction for the lifetime of the reused journal file and never reset per chunk.

## Runtime state and write-attempt boundary

Normal: `Empty/Committed/AbortedSafe -> Prepared -> TargetWritten -> TargetVerified -> Committed`. Before invoking target write, the coordinator performs its final cancellation check and fault boundary. The in-memory `targetWriteAttempted` flag is set immediately before the write call, with no await or cancellation point between them.

If an error or caller cancellation occurs after `Prepared` but before write attempt, read/hash the still-locked target. If it matches, restore and read-back verify original metadata, then publish `AbortedSafe` without writing target data. If either content or metadata cannot be proven restored, retain a non-terminal recovery state. After write attempt but before authoritative `TargetVerified`, partial modification is assumed: publish `RollbackRequired`, restore only verified payload bytes, flush, read back, verify, restore/read-back verify metadata, and only then publish `RollbackSucceeded`; indeterminate rollback preserves recovery state.

`TargetWritten` records only that the write call returned and does not prove flush or verification. `TargetVerified` is published only after flush plus matching target read-back; it proves content only, not metadata. Once `TargetVerified` is authoritative, no later failure may cause another target content write. Metadata restoration/read-back or final-header publication failure preserves the latest authoritative state and requires recovery. From `TargetVerified`, restore and query back all original `FILE_BASIC_INFO` values before publishing `Committed`.

No durable terminal state (`Committed`, `AbortedSafe`, or `RollbackSucceeded`) may bypass metadata restoration and verification. The universal ordering is: verify safe content; restore CreationTime, LastAccessTime, LastWriteTime, ChangeTime, and FileAttributes; query them through the same locked handle; compare all required values; then publish the terminal header. A crash before terminal publication leaves the prior non-terminal state authoritative and restart repeats reconciliation.

## Startup reconciliation: read before write

Before new work, a recovery reader must:

1. Validate both headers and select the unambiguous highest valid sequence.
2. For `Prepared`, `TargetWritten`, `TargetVerified`, or `RollbackRequired`, validate bounds and hash exactly the first `ChunkLength` payload bytes. For a terminal header, ignore the payload. `RecoveryRequired` remains fail-closed and all available evidence is preserved rather than inferred from payload validity.
3. Open/lock and validate volume serial, opaque file ID, and expected file size.
4. Read the current target chunk before any recovery write.
5. SHA-256 exactly the current `ChunkLength` target bytes.

For `Prepared`, `TargetWritten`, or `RollbackRequired`, matching target content causes **no target content write**. Restore and read-back verify metadata, then publish `AbortedSafe`. If content differs, block for manual recovery without writing target bytes. After restart the old lock no longer exists, so the mismatch may be a legitimate post-crash modification even when identity and size still match.

For `TargetVerified`, independently matching target content plus the durable state permits metadata restoration/verification and only then `Committed`; a mismatch blocks for manual recovery without a content write. `RecoveryRequired` always yields `BlockForManualRecovery`, whether the target hash matches or differs. Thus every non-terminal mismatch is fail-closed. `Committed`, `AbortedSafe`, `RollbackSucceeded`, and `Empty` require no content write when already authoritative. No matching identity, invalid metadata, or unverifiable payload means preserve the journal and block. SQLite is neither consulted nor trusted for recovery.

**Runtime rollback versus startup recovery:** automatic payload restoration is allowed only inside the live transaction while the same locked, identity-validated handle still excludes external writers/deletion and content has not reached authoritative `TargetVerified`. Startup reconciliation performs no automatic content restoration. A future manual recovery UI may explicitly offer “restore verified pre-refresh chunk,” but it must warn that current differing data will be overwritten, show path/identity/session details, revalidate journal integrity, and require explicit operator action. It is deferred from this foundation.

The journal stores plaintext. Metadata restoration is complete only after `SetFileInformationByHandle` succeeds and subsequent inspection confirms all required values; failure stops recovery and retains a non-terminal journal state. Read-only files are unsupported in v1 and skipped before preparation; the implementation must not temporarily clear `FILE_ATTRIBUTE_READONLY`.
