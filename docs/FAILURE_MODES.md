# Failure modes

The universal rule is: stop modifying data when uncertain. Benign eligibility skips can continue; journal, write, verification, identity, and rollback failures stop the entire session.

| Failure | Required behavior |
|---|---|
| Process crash / forced termination / BSOD / power loss | On restart, validate headers/payload/identity/size and hash current target bytes without writing. Matching content may reconcile metadata; mismatching content is ambiguous after the original lock disappeared and blocks manual recovery without overwrite. |
| Target SSD disconnect | Stop. Keep the journal. Report recovery required unless target is proven untouched; retry recovery only after the same volume/file identity returns. |
| Journal disk disconnect | Before `Prepared`, never write target. Afterwards stop and retain/recover whatever authoritative state remains; never guess. |
| Partial target write / target write or flush failure | Once the write call was attempted, roll back only from the verified journal, flush and read-back verify; stop even if rollback succeeds. Before the attempt boundary, verify unchanged content and abort safely without a recovery write. |
| Partial journal write / disk full | The inactive header or payload is invalid; retain the previous valid header, never touch target, and stop. |
| Permission change / sharing violation | Before transaction, report the documented skip. During one, stop and follow rollback/recovery policy. Never force access. |
| Runtime hash mismatch before authoritative `TargetVerified` | While the original protected handle is still held, stop, roll back, flush, read and hash. Report `ROLLBACK_SUCCEEDED` or `RECOVERY_REQUIRED`; never continue. A startup mismatch instead blocks without writing. |
| Source read error | Report `SOURCE_READ_ERROR`; write neither journal truth nor target data. |
| Rollback error | Report `RECOVERY_REQUIRED`, retain all journal material, and block future sessions. |
| Corrupt journal header | Ignore that copy. Use the other only if independently valid; if no valid authoritative header exists, block and require manual diagnosis. |
| Corrupt journal data | Never restore it. Block with recovery required; do not erase evidence. |
| Renamed file / hard links | Locate and authorize by volume serial plus byte-exact, opaque `FILE_ID_128`. Rename does not change identity; hard links are deduplicated to one refresh. |
| Deleted file | Delete sharing is denied while open. During restart, do not use a replacement path; unresolved identity blocks automatic recovery. |
| Path reuse | Identity mismatch prevents access and automatic recovery. |
| Unexpected size change | Sharing lock should prevent it; any mismatch stops before writes or enters recovery if modification may have occurred. |
| Metadata restoration/read-back failure | Do not publish `Committed`, `AbortedSafe`, or `RollbackSucceeded`. The prior non-terminal state remains recoverable; retain diagnostics and journal and stop the session. Content verification alone cannot close the transaction. |
| Cancellation | Before write attempt, report safe cancellation and, if already prepared, verify content and restore/verify metadata before `AbortedSafe`, without rewriting content. After write attempt, complete content and metadata verification/commit or rollback, then stop before the next chunk. |
| Application graceful shutdown | Stop scheduling chunks, safely close the current transaction, flush state, release sleep inhibition, then exit. |
| `RecoveryRequired`, even with matching content | Block for manual recovery in v1. A matching SHA-256 does not resolve journal ambiguity, identity, metadata, device, or failed-recovery obligations. |
| Read-only file | Skip as unsupported. Never clear `FILE_ATTRIBUTE_READONLY` to force a refresh. |
| Metadata or final publication failure after authoritative `TargetVerified` | Preserve the latest journal state and report recovery required. Do not roll back or otherwise rewrite already-verified target content. |
| Startup target hash mismatch in any non-terminal state | Do not restore automatically. Preserve both target and journal and block for explicit operator recovery because the mismatch may be a legitimate edit made after the lock disappeared. |
| Crash while reusing payload before new `Prepared` | The previous terminal header remains authoritative. Ignore payload mismatch against that terminal header; the unpublished transaction created no recovery obligation. |
| Attempt to prepare over non-terminal authority | Reject and block new work. Reconcile the unresolved transaction first; never replace its identity, hashes, metadata, or sequence. |
| Journal sequence exhaustion | Fail closed before publication. Never wrap or reset the journal-global sequence. |

Automatic filesystem/disk repair is forbidden. A successful rollback still marks the drive/session suspect.

Runtime rollback is automatic only while ColdRefresh still owns the original identity-validated handle and excludes external writes/deletes. Startup recovery is read-only unless target content already matches the journal original. A future manual restore UI may offer an explicit, strongly warned overwrite of the current differing chunk, but must display identity/path/session information, revalidate journal integrity, and require an affirmative operator action; it is not part of v1 startup.
