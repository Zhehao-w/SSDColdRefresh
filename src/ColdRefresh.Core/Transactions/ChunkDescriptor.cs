using ColdRefresh.Core.Models;

namespace ColdRefresh.Core.Transactions;

public sealed record ChunkDescriptor(
    Guid SessionId,
    FileIdentity FileIdentity,
    long OriginalFileSize,
    long Offset,
    int Length)
{
    public void Validate()
    {
        if (SessionId == Guid.Empty) throw new ArgumentException("A session identity is required.", nameof(SessionId));
        if (FileIdentity.FileId == Guid.Empty) throw new ArgumentException("A file identity is required.", nameof(FileIdentity));
        if (OriginalFileSize <= 0 || Offset < 0 || Length <= 0 || Offset > OriginalFileSize - Length)
            throw new ArgumentOutOfRangeException(nameof(Length), "Chunk must be wholly within the original file.");
    }
}
