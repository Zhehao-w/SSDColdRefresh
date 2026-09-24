# Architecture

## Status and boundaries

This first milestone is deliberately non-destructive. `ColdRefresh.Core` contains platform-neutral policy, immutable identities, the explicit chunk state machine, and a coordinator expressed entirely through testable ports. `ColdRefresh.Windows` is the future Win32 boundary and currently exposes source-generated declarations and constants, but no production refresh adapter. `ColdRefresh.App` is a minimal unpackaged WinUI 3 shell. SQLite is reserved for history and never participates in active recovery.

## Layers

- **Core** owns safety decisions. `ChunkRefreshTransaction` is the only orchestration path. `IChunkSource`, `IRecoveryJournal`, and `IFaultInjector` make every durability boundary observable and injectable. Core never opens a path.
- **Windows** will open a single handle with read/write access and sharing that allows reads but denies write/delete, query `FileIdInfo` and `FILE_BASIC_INFO`, reject unsafe attributes and non-NTFS volumes, use `RandomAccess` at explicit offsets, and call `FlushFileBuffers`. Volume disk extents will compare physical disk numbers rather than drive letters.
- **App** will gate all commands on startup journal reconciliation and expose conservative settings. It will use CommunityToolkit.Mvvm and SQLite history after the destructive boundary is proven.

## Planned refresh pipeline

Scan without following reparse points; reject unsupported files; open and lock one handle; capture identity, size, and metadata; revalidate identity; optionally hash the entire file; process one 64 MiB chunk at a time; restore metadata; revalidate identity and size; hash the entire file again; record history only after success. A path is merely a discovery hint. `(volume serial, 128-bit file ID)` is identity and hard-link deduplication key.

Cancellation is observed before source acquisition and after a committed chunk only. No cancellation token is forwarded into the target write/flush/verify/rollback critical region.

## State machine

Normal transitions are `Empty -> Preparing -> Prepared -> TargetWritten -> TargetVerified -> Committed`. `Preparing` is an in-memory state only: an incomplete data write is not recoverable truth. Any failure before `Prepared` stops without touching the target. A failure after `Prepared` enters rollback. Verified rollback yields `RollbackSucceeded` and stops the session as suspect. Failed or indeterminate rollback yields `RecoveryRequired`; the journal is retained. Illegal transitions fail closed.

Startup must resolve the highest valid dual header before enabling Scan or Refresh. `Prepared` means restore; `TargetWritten` means compare target and original hash, committing only if equal and otherwise restore; `TargetVerified` may commit only after identity/content reconciliation; `RollbackRequired` means restore; `RecoveryRequired` requires recovery. `Committed`/`Empty` contain no outstanding recovery obligation.

## Deferred work

Binary journal I/O, real `SafeFileHandle` adapters, device extent discovery, NTFS screening, metadata restoration, startup recovery, sleep inhibition, full-file hashing, and SQLite history are intentionally deferred. No placeholder claims production safety.
