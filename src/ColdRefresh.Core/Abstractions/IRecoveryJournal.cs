using ColdRefresh.Core.Transactions;

namespace ColdRefresh.Core.Abstractions;

public interface IRecoveryJournal
{
    ValueTask WritePayloadAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> original, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
    ValueTask ReadPayloadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken);
    /// <summary>
    /// Publishes a new transaction after its payload has been durably verified. The journal owns the global
    /// sequence and may start only when no existing authority has an unresolved recovery obligation.
    /// </summary>
    ValueTask<JournalRecord> PublishPreparedAsync(
        ChunkDescriptor chunk,
        ReadOnlyMemory<byte> originalChunkHash,
        ReadOnlyMemory<byte> journalDataHash);
    /// <summary>
    /// Publishes <paramref name="state"/> from the supplied authoritative record and returns the new authority.
    /// Implementations must reject stale sequence/state pairs and illegal transitions, never compensate for them.
    /// </summary>
    ValueTask<JournalRecord> PublishStateAsync(JournalRecord record, TransactionState state);
    ValueTask PreserveForRecoveryAsync(JournalRecord record, Exception cause);
}
