# Architecture

## Status and boundaries

This first milestone is deliberately non-destructive. `ColdRefresh.Core` contains platform-neutral policy, immutable identities, the explicit chunk state machine, and a coordinator expressed entirely through testable ports. `ColdRefresh.Windows` is the future Win32 boundary and currently exposes source-generated declarations and constants, but no production refresh adapter. `ColdRefresh.App` is a minimal unpackaged WinUI 3 shell. SQLite is reserved for history and never participates in active recovery.

## Layers

- **Core** owns safety decisions. `ChunkRefreshTransaction` is the only orchestration path. `IChunkSource`, `IRecoveryJournal`, and `IFaultInjector` make every durability boundary observable and injectable. Core never opens a path. Every journal publication returns a new authoritative record; that exact sequence/state pair is the sole input to the next publication.
- **Windows** will open a single handle with read/write access and sharing that allows reads but denies write/delete, query `FileIdInfo` and `FILE_BASIC_INFO`, reject unsafe attributes and non-NTFS volumes, use `RandomAccess` at explicit offsets, and call `FlushFileBuffers`. Volume disk extents will compare physical disk numbers rather than drive letters.
- **App** will gate all commands on startup journal reconciliation and expose conservative settings. It will use CommunityToolkit.Mvvm and SQLite history after the destructive boundary is proven.

## Planned refresh pipeline

Scan without following reparse points; reject unsupported files; open and lock one handle; capture identity, size, and `FILE_BASIC_INFO`; revalidate identity; optionally hash the entire file; process one 64 MiB chunk at a time; restore metadata; revalidate identity and size; hash the entire file again; record history only after success. A path is merely a discovery hint. `(volume serial, opaque FILE_ID_128 bytes)` is identity and hard-link deduplication key. The file ID is never interpreted as a GUID.

Cancellation is observed throughout source read and journal preparation and once more immediately before the write-attempt boundary. The boundary is set immediately before calling target write. No cancellation token is forwarded into the target write/flush/verify/rollback critical region. Cancellation before that boundary is classified as safe cancellation; afterwards it is deferred.

## State machine

Normal transitions are `Empty -> Preparing -> Prepared -> TargetWritten -> TargetVerified -> Committed`. `Preparing` is an in-memory state only: an incomplete data write is not recovery truth. Before a target write is attempted, a prepared transaction verifies that content is unchanged and becomes `AbortedSafe` without rewriting it. Once a write is attempted, partial modification is assumed and failures enter rollback. Verified rollback yields `RollbackSucceeded` and stops the session as suspect. Failed or indeterminate rollback yields `RecoveryRequired`; the journal is retained. Illegal or stale-record transitions fail closed.

Startup must resolve the highest valid dual header before enabling Scan or Refresh, validate payload/identity/size, and read/hash the current chunk before deciding to write. Matching content in `Prepared`, `TargetWritten`, `RollbackRequired`, or recoverable `RecoveryRequired` becomes `AbortedSafe` without a target write. Differing content is restored and verified, then becomes `AbortedSafe`. Only durable `TargetVerified` evidence plus a matching readback may advance to `Committed`. `Committed` means the refresh write was durably flushed and verified; `AbortedSafe` means content is safe but refresh completion is unproven and `LastRefreshTime` must not change.

## Deferred work

Binary journal I/O, real `SafeFileHandle` adapters, device extent discovery, NTFS screening, metadata restoration, startup recovery, sleep inhibition, full-file hashing, and SQLite history are intentionally deferred. No placeholder claims production safety.
