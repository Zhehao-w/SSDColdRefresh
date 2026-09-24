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
        JournalRecord? prepared = null;
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
            prepared = await journal.PublishStateAsync(candidate, TransactionState.Prepared);
            if (prepared.State != TransactionState.Prepared || !prepared.HasValidHashes)
                throw new InvalidDataException("Journal did not publish a verified PREPARED record.");
            await _faults.AtAsync(FaultPoint.AfterPrepared);

            // Cancellation is intentionally not observed beyond this destructive boundary.
            await _faults.AtAsync(FaultPoint.DuringTargetWrite);
            await target.WriteExactlyAsync(chunk, original);
            await _faults.AtAsync(FaultPoint.AfterTargetWrite);
            await PublishAsync(prepared, TransactionState.TargetWritten);
            await _faults.AtAsync(FaultPoint.BeforeTargetFlush);
            await target.FlushAsync();
            await _faults.AtAsync(FaultPoint.AfterTargetFlush);
            await _faults.AtAsync(FaultPoint.BeforeTargetVerification);
            await target.ReadExactlyAsync(chunk, readback, CancellationToken.None);
            var targetHash = SHA256.HashData(readback.Span);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                throw new InvalidDataException("Target read-back hash mismatch.");
            await _faults.AtAsync(FaultPoint.AfterTargetVerification);
            var verified = await PublishAsync(prepared, TransactionState.TargetVerified);
            await PublishAsync(verified, TransactionState.Committed);
            return new(TransactionOutcome.Committed);
        }
        catch (Exception ex) when (prepared is null)
        {
            return new(TransactionOutcome.FailedBeforeTargetWrite, ex);
        }
        catch (Exception ex)
        {
            return await RollbackAsync(chunk, prepared!, rented.AsMemory(0, chunk.Length), verify.AsMemory(0, chunk.Length), ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, chunk.Length));
            CryptographicOperations.ZeroMemory(verify.AsSpan(0, chunk.Length));
            ArrayPool<byte>.Shared.Return(rented);
            ArrayPool<byte>.Shared.Return(verify);
        }
    }

    private async ValueTask<JournalRecord> PublishAsync(JournalRecord record, TransactionState state)
    {
        await _faults.AtAsync(FaultPoint.DuringStateUpdate);
        var published = await journal.PublishStateAsync(record, state);
        if (published.State != state) throw new InvalidDataException($"Journal failed to publish {state}.");
        return published;
    }

    private async ValueTask<TransactionResult> RollbackAsync(ChunkDescriptor chunk, JournalRecord prepared, Memory<byte> recovery, Memory<byte> readback, Exception cause)
    {
        try
        {
            if (!prepared.HasValidHashes) throw new InvalidDataException("Recovery record is not verified.");
            await PublishAsync(prepared, TransactionState.RollbackRequired);
            await journal.ReadPayloadExactlyAsync(chunk, recovery, CancellationToken.None);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(recovery.Span), prepared.OriginalChunkHash))
                throw new InvalidDataException("Recovery payload no longer matches its verified hash.");
            await _faults.AtAsync(FaultPoint.DuringRollback);
            await target.WriteExactlyAsync(chunk, recovery);
            await _faults.AtAsync(FaultPoint.AfterRollbackWrite);
            await target.FlushAsync();
            await _faults.AtAsync(FaultPoint.BeforeRollbackVerification);
            await target.ReadExactlyAsync(chunk, readback, CancellationToken.None);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(readback.Span), prepared.OriginalChunkHash))
                throw new InvalidDataException("Rollback verification failed.");
            await PublishAsync(prepared, TransactionState.RollbackSucceeded);
            return new(TransactionOutcome.RollbackSucceeded, cause);
        }
        catch (Exception rollbackError)
        {
            var combined = new AggregateException(cause, rollbackError);
            try { await journal.PreserveForRecoveryAsync(prepared, combined); } catch { /* Never mask recovery-required. */ }
            return new(TransactionOutcome.RecoveryRequired, combined);
        }
    }
}
