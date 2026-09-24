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
