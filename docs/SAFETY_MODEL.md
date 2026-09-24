# Safety model

## Invariants

1. Target modification is impossible through the coordinator until original bytes have been completely read, SHA-256 hashed, journaled, durably flushed, read back, hash-verified, and represented by an authoritative `Prepared` header.
2. Success requires target write, durable flush, read-back, and SHA-256 equality with the source.
3. Unverified or header-invalid journal bytes are never recovery truth.
4. Before the write-attempt boundary, failure/cancellation never rewrites unchanged target content and reconciles to `AbortedSafe`. Once the write call is attempted, partial modification is assumed and failure invokes rollback from verified recovery bytes. A failed rollback preserves the journal and reports `RecoveryRequired`.
5. Cancellation is honored through journal preparation and immediately before the write-attempt boundary; after it, cancellation is deferred to a safe terminal state.
6. The locked handle's volume serial, opaque 16-byte `FILE_ID_128`, and size must remain consistent. Paths cannot authorize recovery and the file ID has no GUID semantics.
7. Original `FILE_BASIC_INFO` timestamps and attributes are part of every prepared journal header, so a fresh recovery process can restore filesystem-visible metadata. `Committed`, `AbortedSafe`, and `RollbackSucceeded` cannot be published until all five fields are restored, queried back, and verified; `TargetVerified` proves content only.
8. `Committed` requires durable `TargetVerified` evidence followed by verified metadata restoration. Merely matching original content can proceed toward `AbortedSafe` only after metadata verification; it never proves refresh success or permits a `LastRefreshTime` update.
9. `RecoveryRequired` is never automatically downgraded based on content equality. It blocks for manual recovery in v1 because unresolved obligations can extend beyond content bytes.
10. Every transition consumes the latest authoritative record returned by the preceding publication. Any safety-critical anomaly stops the session; it never advances to another file.
11. Once authoritative `TargetVerified` exists, later metadata or journal failures preserve the latest state and never rewrite target content.
12. Startup recovery never overwrites mismatching content automatically. Without the original lock, a mismatch may be a legitimate post-crash edit; v1 preserves the journal and blocks for manual recovery.
13. Sequence numbers are journal-global across slot reuse, never per-chunk counters. Only the journal chooses the next sequence and it fails closed on wraparound.
14. A previous terminal header remains authoritative while a future payload is prepared. Terminal headers do not make payload bytes recovery-authoritative, and unresolved non-terminal authority can never be superseded by a new transaction.

## Protected failure classes

The design protects against process termination, many power-loss/BSOD points, torn header updates, partial target writes, hash mismatches, and rename/path-reuse races through a bounded recovery slot, two independently checksummed headers, flush/read-back verification, identity-based reopening, and in-process rollback. Automatic rollback is limited to the live transaction while its original protected handle remains open. Startup only reconciles matching content; mismatching content is preserved unchanged because its provenance is ambiguous. Protection depends on at least one valid header and readable, matching journal payload.

Separate-disk storage is recommended because loss of the target device is less likely to also destroy recovery bytes. Same-physical-disk storage is permitted with a persistent warning but cannot protect against device-wide failure, controller failure, firmware corruption, or loss of that SSD. Drive-letter difference is irrelevant; `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` must compare every backing physical disk extent. Spanned/shared extents require conservative rejection unless disjointness is proven.

## Not guaranteed

`FlushFileBuffers` asks Windows to flush buffered file data; it cannot prove that a device truthfully persisted volatile caches. SHA-256 detects corruption with extremely high probability but does not repair unrelated latent corruption. The design cannot guarantee survival of simultaneous target and journal loss, malicious kernel/firmware behavior, RAM corruption before hashing, hardware that lies about flushes, or pre-existing unreadable data. It does not repair NTFS or disks.

Assumptions are Windows 11 x64, healthy NTFS, correct Win32 identity/locking semantics, correct cryptographic primitives, and storage honoring successful writes/flushes. Original bytes may appear in plaintext in the journal. BitLocker-specific and privacy guarantees are out of scope.

## Eligibility

Reject non-NTFS, zero-length, read-only, system-protected, pagefile/hiberfil, reparse/symlink/junction, sparse, compressed, encrypted, offline/cloud-placeholder, sharing-violating, identity-ambiguous, or metadata-ambiguous files. V1 never temporarily clears `FILE_ATTRIBUTE_READONLY`. `LastWriteTime` (default 365 days) is the primary age signal; `LastAccessTime` is not. Future SQLite `LastRefreshTime` supplements selection but cannot authorize recovery.
