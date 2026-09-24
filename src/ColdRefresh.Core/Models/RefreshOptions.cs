namespace ColdRefresh.Core.Models;

public sealed record RefreshOptions
{
    public const int DefaultChunkSize = 64 * 1024 * 1024;
    public static readonly TimeSpan DefaultColdAge = TimeSpan.FromDays(365);

    public int ChunkSize { get; init; } = DefaultChunkSize;
    public TimeSpan ColdAge { get; init; } = DefaultColdAge;
    public VerificationMode Verification { get; init; } = VerificationMode.FullFileSha256;
    public int Concurrency { get; init; } = 1;

    public void Validate()
    {
        if (ChunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkSize));
        if (ColdAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ColdAge));
        if (Concurrency != 1) throw new NotSupportedException("The v1 safety model requires concurrency one.");
        if (Verification != VerificationMode.FullFileSha256) throw new NotSupportedException("Only full-file SHA-256 is currently permitted.");
    }
}

public enum VerificationMode { FullFileSha256 }
