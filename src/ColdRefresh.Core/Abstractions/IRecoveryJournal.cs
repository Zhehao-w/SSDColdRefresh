using ColdRefresh.Core.Transactions;

namespace ColdRefresh.Core.Abstractions;

public interface IRecoveryJournal
{
    ValueTask WritePayloadAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> original, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
    ValueTask ReadPayloadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken);
    ValueTask<JournalRecord> PublishStateAsync(JournalRecord record, TransactionState state);
    ValueTask PreserveForRecoveryAsync(JournalRecord record, Exception cause);
}
