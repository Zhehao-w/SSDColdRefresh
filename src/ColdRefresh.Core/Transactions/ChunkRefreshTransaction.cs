using System.Buffers;
using System.Security.Cryptography;
using ColdRefresh.Core.Abstractions;

namespace ColdRefresh.Core.Transactions;

/// <summary>Safety policy only. Production storage adapters are intentionally not part of this milestone.</summary>
public sealed class ChunkRefreshTransaction(IChunkSource target, IRecoveryJournal journal, IFaultInjector? faults = null)
{
    private readonly IFaultInjector _faults = faults ?? NoFaultInjector.Instance;

    public async ValueTask<TransactionResult> ExecuteAsync(ChunkDescriptor chunk, CancellationToken cancellationToken)
    {
        chunk.Validate();
        if (cancellationToken.IsCancellationRequested)
            return new(TransactionOutcome.CancelledAtSafeBoundary);

        var rented = ArrayPool<byte>.Shared.Rent(chunk.Length);
        var verify = ArrayPool<byte>.Shared.Rent(chunk.Length);
        JournalRecord? current = null;
        var targetWriteAttempted = false;
        var targetContentVerified = false;
        try
        {
            var original = rented.AsMemory(0, chunk.Length);
            var readback = verify.AsMemory(0, chunk.Length);
            await target.ReadExactlyAsync(chunk, original, cancellationToken);
            var sourceHash = SHA256.HashData(original.Span);

            await _faults.AtAsync(FaultPoint.BeforeJournalWrite);
            await journal.WritePayloadAsync(chunk, original, cancellationToken);
            await _faults.AtAsync(FaultPoint.AfterJournalWrite);
            await journal.FlushAsync(cancellationToken);
            await _faults.AtAsync(FaultPoint.BeforeJournalVerification);
            await journal.ReadPayloadExactlyAsync(chunk, readback, cancellationToken);
            var journalHash = SHA256.HashData(readback.Span);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, journalHash))
                throw new InvalidDataException("Recovery journal read-back hash mismatch.");

            var candidate = new JournalRecord(0, TransactionState.Empty, chunk, sourceHash, journalHash);
            current = await journal.PublishStateAsync(candidate, TransactionState.Prepared);
            if (current.State != TransactionState.Prepared || current.SequenceNumber != candidate.SequenceNumber + 1 || !current.HasValidHashes)
                throw new InvalidDataException("Journal did not publish a verified PREPARED record.");
            await _faults.AtAsync(FaultPoint.AfterPrepared);

            cancellationToken.ThrowIfCancellationRequested();
            await _faults.AtAsync(FaultPoint.BeforeTargetWriteAttempt);
            cancellationToken.ThrowIfCancellationRequested();

            // The flag moves immediately before the call. From here cancellation is deliberately deferred.
            targetWriteAttempted = true;
            await target.WriteExactlyAsync(chunk, original);
            await _faults.AtAsync(FaultPoint.AfterTargetWrite);
            current = await PublishAsync(current, TransactionState.TargetWritten);
            await _faults.AtAsync(FaultPoint.BeforeTargetFlush);
            await target.FlushAsync();
            await _faults.AtAsync(FaultPoint.AfterTargetFlush);
            await _faults.AtAsync(FaultPoint.BeforeTargetVerification);
            await target.ReadExactlyAsync(chunk, readback, CancellationToken.None);
            var targetHash = SHA256.HashData(readback.Span);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                throw new InvalidDataException("Target read-back hash mismatch.");
            await _faults.AtAsync(FaultPoint.AfterTargetVerification);
            current = await PublishAsync(current, TransactionState.TargetVerified);
            targetContentVerified = true;
            await RestoreAndVerifyMetadataAsync(chunk);
            current = await PublishAsync(current, TransactionState.Committed);
            return new(TransactionOutcome.Committed);
        }
        catch (OperationCanceledException ex) when (!targetWriteAttempted && cancellationToken.IsCancellationRequested)
        {
            return current is null
                ? new(TransactionOutcome.CancelledAtSafeBoundary, ex)
                : await AbortSafelyAsync(chunk, current, verify.AsMemory(0, chunk.Length), TransactionOutcome.CancelledAtSafeBoundary, ex);
        }
        catch (Exception ex) when (current is null)
        {
            return new(TransactionOutcome.FailedBeforeTargetWrite, ex);
        }
        catch (Exception ex)
        {
            if (targetContentVerified)
                return await PreserveVerifiedContentForRecoveryAsync(current!, ex);
            if (targetWriteAttempted)
                return await RollbackAsync(chunk, current!, rented.AsMemory(0, chunk.Length), verify.AsMemory(0, chunk.Length), ex);
            return await AbortSafelyAsync(chunk, current!, verify.AsMemory(0, chunk.Length), TransactionOutcome.AbortedSafe, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, chunk.Length));
            CryptographicOperations.ZeroMemory(verify.AsSpan(0, chunk.Length));
            ArrayPool<byte>.Shared.Return(rented);
            ArrayPool<byte>.Shared.Return(verify);
        }
    }

    private async ValueTask<TransactionResult> PreserveVerifiedContentForRecoveryAsync(JournalRecord current, Exception cause)
    {
        try { await journal.PreserveForRecoveryAsync(current, cause); } catch { /* Never risk a content rewrite after TargetVerified. */ }
        return new(TransactionOutcome.RecoveryRequired, cause);
    }

    private async ValueTask<JournalRecord> PublishAsync(JournalRecord current, TransactionState state)
    {
        await _faults.AtAsync(FaultPoint.DuringStateUpdate);
        var published = await journal.PublishStateAsync(current, state);
        if (published.State != state || published.SequenceNumber != current.SequenceNumber + 1)
            throw new InvalidDataException($"Journal failed to publish authoritative {state} state.");
        return published;
    }

    private async ValueTask<TransactionResult> AbortSafelyAsync(
        ChunkDescriptor chunk,
        JournalRecord current,
        Memory<byte> readback,
        TransactionOutcome outcome,
        Exception cause)
    {
        try
        {
            await target.ReadExactlyAsync(chunk, readback, CancellationToken.None);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(readback.Span), current.OriginalChunkHash))
                throw new InvalidDataException("Target changed before the write-attempt boundary.");
            await RestoreAndVerifyMetadataAsync(chunk);
            current = await PublishAsync(current, TransactionState.AbortedSafe);
            return new(outcome, cause);
        }
        catch (Exception reconciliationError)
        {
            var combined = new AggregateException(cause, reconciliationError);
            try { await journal.PreserveForRecoveryAsync(current, combined); } catch { /* Never mask recovery-required. */ }
            return new(TransactionOutcome.RecoveryRequired, combined);
        }
    }

    private async ValueTask<TransactionResult> RollbackAsync(
        ChunkDescriptor chunk,
        JournalRecord current,
        Memory<byte> recovery,
        Memory<byte> readback,
        Exception cause)
    {
        try
        {
            if (!current.HasValidHashes) throw new InvalidDataException("Recovery record is not verified.");
            current = await PublishAsync(current, TransactionState.RollbackRequired);
            await journal.ReadPayloadExactlyAsync(chunk, recovery, CancellationToken.None);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(recovery.Span), current.OriginalChunkHash))
                throw new InvalidDataException("Recovery payload no longer matches its verified hash.");
            await _faults.AtAsync(FaultPoint.DuringRollback);
            await target.WriteExactlyAsync(chunk, recovery);
            await _faults.AtAsync(FaultPoint.AfterRollbackWrite);
            await target.FlushAsync();
            await _faults.AtAsync(FaultPoint.BeforeRollbackVerification);
            await target.ReadExactlyAsync(chunk, readback, CancellationToken.None);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(readback.Span), current.OriginalChunkHash))
                throw new InvalidDataException("Rollback verification failed.");
            await RestoreAndVerifyMetadataAsync(chunk);
            current = await PublishAsync(current, TransactionState.RollbackSucceeded);
            return new(TransactionOutcome.RollbackSucceeded, cause);
        }
        catch (Exception rollbackError)
        {
            var combined = new AggregateException(cause, rollbackError);
            try { await journal.PreserveForRecoveryAsync(current, combined); } catch { /* Never mask recovery-required. */ }
            return new(TransactionOutcome.RecoveryRequired, combined);
        }
    }

    private async ValueTask RestoreAndVerifyMetadataAsync(ChunkDescriptor chunk)
    {
        await _faults.AtAsync(FaultPoint.DuringMetadataRestore);
        await target.RestoreAndVerifyMetadataAsync(chunk);
    }
}
