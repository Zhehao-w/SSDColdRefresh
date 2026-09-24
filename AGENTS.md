# ColdRefresh agent safety rules

These rules apply to the entire repository and are non-negotiable.

1. Never modify target bytes unless a flushed, read-back, SHA-256-verified recovery copy is already authoritative as `Prepared` in the binary journal.
2. Keep one locked `SafeFileHandle` open from identity validation through metadata restoration. Never replace a target file and never trust a path as identity.
3. After target modification begins, cancellation is deferred until verification, commit, or a verified rollback completes.
4. A write, flush, hash, journal, identity, metadata, or rollback anomaly stops the session. Only documented benign skips may continue.
5. Never recover from unverified journal data. Never discard a journal when recovery is required or rollback is unverified.
6. Use SHA-256 for safety decisions. Optimizations must not weaken recoverability, determinism, verification, or testability.
7. Do not add unsupported v1 features (reparse, sparse, compressed, EFS, cloud/offline, non-NTFS, raw disk, firmware, TRIM, BitLocker-specific, encryption, or automatic repair).
8. Production filesystem writes remain disabled until the journal implementation, startup recovery, identity locking, metadata preservation, and fault-injection tests have been reviewed.
9. Treat `FILE_ID_128` as 16 opaque bytes, never as a GUID. Carry the exact authoritative journal record returned by each state publication into the next transition.
10. `Committed` requires durable `TargetVerified` evidence. Content that is merely known safe after an uncertain transaction is `AbortedSafe` and must not update `LastRefreshTime`.
11. Publish `Committed`, `AbortedSafe`, or `RollbackSucceeded` only after restoring and read-back verifying every original `FILE_BASIC_INFO` field. A metadata failure remains non-terminal and recoverable.
12. Skip read-only files in v1. Never clear and later restore `FILE_ATTRIBUTE_READONLY` as part of refresh.
13. Once authoritative `TargetVerified` exists, later metadata or journal failures must preserve recovery state without another target content write.
14. After restart, never overwrite mismatching target content automatically. The original lock is gone, so preserve the journal and require explicit manual recovery rather than overwriting possibly legitimate post-crash edits.
15. Journal sequence numbers are global across slot reuse and never reset per chunk. A terminal authority remains durable while the next payload is prepared, and no new `Prepared` may supersede unresolved work.
