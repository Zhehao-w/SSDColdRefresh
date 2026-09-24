# Architecture

## Status and boundaries

This first milestone is deliberately non-destructive. `ColdRefresh.Core` contains platform-neutral policy, immutable identities, the explicit chunk state machine, and a coordinator expressed entirely through testable ports. `ColdRefresh.Windows` is the future Win32 boundary and currently exposes source-generated declarations and constants, but no production refresh adapter. `ColdRefresh.App` is a minimal unpackaged WinUI 3 shell. SQLite is reserved for history and never participates in active recovery.

## Layers

- **Core** owns safety decisions. `ChunkRefreshTransaction` is the only orchestration path. `IChunkSource`, `IRecoveryJournal`, and `IFaultInjector` make every durability boundary observable and injectable. Core never opens a path. Every journal publication returns a new authoritative record; that exact sequence/state pair is the sole input to the next publication.
- **Windows** will open a single handle with read/write access and sharing that allows reads but denies write/delete, query `FileIdInfo` and `FILE_BASIC_INFO`, reject unsafe attributes and non-NTFS volumes, use `RandomAccess` at explicit offsets, and call `FlushFileBuffers`. Volume disk extents will compare physical disk numbers rather than drive letters.
- **App** will gate all commands on startup journal reconciliation and expose conservative settings. It will use CommunityToolkit.Mvvm and SQLite history after the destructive boundary is proven.

## Planned refresh pipeline

Scan without following reparse points; reject unsupported files (including `FILE_ATTRIBUTE_READONLY`); open and lock one handle; capture identity, size, and `FILE_BASIC_INFO`; revalidate identity; optionally hash the entire file; process one 64 MiB chunk at a time; restore metadata; revalidate identity and size; hash the entire file again; record history only after success. Read-only is not temporarily cleared/restored in v1. A path is merely a discovery hint. `(volume serial, opaque FILE_ID_128 bytes)` is identity and hard-link deduplication key. The file ID is never interpreted as a GUID.

Cancellation is observed throughout source read and journal preparation and once more immediately before the write-attempt boundary. The boundary is set immediately before calling target write. No cancellation token is forwarded into the target write/flush/verify/rollback critical region. Cancellation before that boundary is classified as safe cancellation; afterwards it is deferred.

## State machine

Normal transitions are `Empty -> Preparing -> Prepared -> TargetWritten -> TargetVerified -> Committed`. `Preparing` is an in-memory state only: an incomplete data write is not recovery truth. Before a target write is attempted, a prepared transaction verifies that content is unchanged, restores/verifies metadata, and becomes `AbortedSafe` without rewriting content. Once a write is attempted, partial modification is assumed and failures enter rollback. Verified content plus restored/verified metadata yields `RollbackSucceeded` and stops the session as suspect. Failed or indeterminate rollback yields `RecoveryRequired`; the journal is retained. Illegal or stale-record transitions fail closed.

Startup must resolve the highest valid dual header before enabling Scan or Refresh, validate payload/identity/size, and read/hash the current chunk before deciding to write. `RecoveryRequired` always blocks for manual recovery in v1, regardless of a matching content hash. Matching content in `Prepared`, `TargetWritten`, or `RollbackRequired` avoids a content write; differing content is restored and verified. In either case, original metadata must then be restored and queried back before `AbortedSafe` is published. Only durable `TargetVerified` evidence plus matching content may proceed toward `Committed`, and metadata must be restored/read-back verified first. `Committed` means content and metadata were verified; `AbortedSafe` means content and metadata are safe but refresh completion is unproven and `LastRefreshTime` must not change.

`Committed`, `AbortedSafe`, and `RollbackSucceeded` are terminal states. The required ordering for all three is: reach a verified-safe content state, restore original CreationTime/LastAccessTime/LastWriteTime/ChangeTime/FileAttributes, query those values through the same locked handle, verify them, and only then publish the terminal header. `TargetVerified` proves content only. A crash or failure before metadata verification leaves the preceding non-terminal journal state authoritative.

## Deferred work

Binary journal I/O, real `SafeFileHandle` adapters, device extent discovery, NTFS screening, metadata restoration, startup recovery, sleep inhibition, full-file hashing, and SQLite history are intentionally deferred. No placeholder claims production safety.
