using ColdRefresh.Core.Transactions;

namespace ColdRefresh.Core.Abstractions;

/// <summary>A locked, identity-validated target. Implementations must perform complete reads/writes or throw.</summary>
public interface IChunkSource
{
    ValueTask ReadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken);
    ValueTask WriteExactlyAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> source);
    ValueTask FlushAsync();
    /// <summary>Restores and reads back all original FILE_BASIC_INFO fields, throwing unless they match exactly.</summary>
    ValueTask RestoreAndVerifyMetadataAsync(ChunkDescriptor chunk);
}
