# Failure modes

The universal rule is: stop modifying data when uncertain. Benign eligibility skips can continue; journal, write, verification, identity, and rollback failures stop the entire session.

| Failure | Required behavior |
|---|---|
| Process crash / forced termination / BSOD / power loss | On restart, validate both headers and payload, select the highest valid sequence, reconcile by identity, then restore or commit as specified; block new work until resolved. |
| Target SSD disconnect | Stop. Keep the journal. Report recovery required unless target is proven untouched; retry recovery only after the same volume/file identity returns. |
| Journal disk disconnect | Before `Prepared`, never write target. Afterwards stop and retain/recover whatever authoritative state remains; never guess. |
| Partial target write / target write or flush failure | Roll back only from the verified journal, flush and read-back verify; stop even if rollback succeeds. |
| Partial journal write / disk full | The inactive header or payload is invalid; retain the previous valid header, never touch target, and stop. |
| Permission change / sharing violation | Before transaction, report the documented skip. During one, stop and follow rollback/recovery policy. Never force access. |
| Hash mismatch | Stop, roll back, flush, read and hash. Report `ROLLBACK_SUCCEEDED` or `RECOVERY_REQUIRED`; never continue. |
| Source read error | Report `SOURCE_READ_ERROR`; write neither journal truth nor target data. |
| Rollback error | Report `RECOVERY_REQUIRED`, retain all journal material, and block future sessions. |
| Corrupt journal header | Ignore that copy. Use the other only if independently valid; if no valid authoritative header exists, block and require manual diagnosis. |
| Corrupt journal data | Never restore it. Block with recovery required; do not erase evidence. |
| Renamed file / hard links | Locate and authorize by volume serial plus file ID. Rename does not change identity; hard links are deduplicated to one refresh. |
| Deleted file | Delete sharing is denied while open. During restart, do not use a replacement path; unresolved identity blocks automatic recovery. |
| Path reuse | Identity mismatch prevents access and automatic recovery. |
| Unexpected size change | Sharing lock should prevent it; any mismatch stops before writes or enters recovery if modification may have occurred. |
| Metadata restoration failure | Content is verified or rolled back, but the session fails and stops; retain diagnostics and journal until the operator acknowledges unresolved metadata state. |
| Cancellation | Record request, complete verification/commit or rollback, then stop before the next chunk. |
| Application graceful shutdown | Stop scheduling chunks, safely close the current transaction, flush state, release sleep inhibition, then exit. |

Automatic filesystem/disk repair is forbidden. A successful rollback still marks the drive/session suspect.
