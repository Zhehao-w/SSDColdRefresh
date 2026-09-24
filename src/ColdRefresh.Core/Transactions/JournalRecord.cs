namespace ColdRefresh.Core.Transactions;

public sealed record JournalRecord(
    ulong SequenceNumber,
    TransactionState State,
    ChunkDescriptor Chunk,
    byte[] OriginalChunkHash,
    byte[] JournalDataHash)
{
    public bool HasValidHashes =>
        OriginalChunkHash.Length == 32 &&
        JournalDataHash.Length == 32 &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(OriginalChunkHash, JournalDataHash);
}
