# Safety model

## Invariants

1. Target modification is impossible through the coordinator until original bytes have been completely read, SHA-256 hashed, journaled, durably flushed, read back, hash-verified, and represented by an authoritative `Prepared` header.
2. Success requires target write, durable flush, read-back, and SHA-256 equality with the source.
3. Unverified or header-invalid journal bytes are never recovery truth.
4. Once target modification is possible, failure invokes rollback from the verified recovery bytes. A failed rollback preserves the journal and reports `RecoveryRequired`.
5. Cancellation is honored only before a transaction or after a safe terminal state.
6. The locked handle's volume serial, file ID, and size must remain consistent. Paths cannot authorize recovery.
7. Any safety-critical anomaly stops the session; it never advances to another file.

## Protected failure classes

The design protects against process termination, many power-loss/BSOD points, torn header updates, partial target writes, hash mismatches, and rename/path-reuse races through a bounded recovery slot, two independently checksummed headers, flush/read-back verification, identity-based reopening, and rollback. Protection depends on at least one valid header and readable, matching journal payload.

Separate-disk storage is recommended because loss of the target device is less likely to also destroy recovery bytes. Same-physical-disk storage is permitted with a persistent warning but cannot protect against device-wide failure, controller failure, firmware corruption, or loss of that SSD. Drive-letter difference is irrelevant; `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` must compare every backing physical disk extent. Spanned/shared extents require conservative rejection unless disjointness is proven.

## Not guaranteed

`FlushFileBuffers` asks Windows to flush buffered file data; it cannot prove that a device truthfully persisted volatile caches. SHA-256 detects corruption with extremely high probability but does not repair unrelated latent corruption. The design cannot guarantee survival of simultaneous target and journal loss, malicious kernel/firmware behavior, RAM corruption before hashing, hardware that lies about flushes, or pre-existing unreadable data. It does not repair NTFS or disks.

Assumptions are Windows 11 x64, healthy NTFS, correct Win32 identity/locking semantics, correct cryptographic primitives, and storage honoring successful writes/flushes. Original bytes may appear in plaintext in the journal. BitLocker-specific and privacy guarantees are out of scope.

## Eligibility

Reject non-NTFS, zero-length, system-protected, pagefile/hiberfil, reparse/symlink/junction, sparse, compressed, encrypted, offline/cloud-placeholder, sharing-violating, identity-ambiguous, or metadata-ambiguous files. `LastWriteTime` (default 365 days) is the primary age signal; `LastAccessTime` is not. Future SQLite `LastRefreshTime` supplements selection but cannot authorize recovery.
