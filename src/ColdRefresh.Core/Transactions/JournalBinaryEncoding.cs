namespace ColdRefresh.Core.Transactions;

public static class JournalBinaryEncoding
{
    public const int SessionIdLength = 16;

    /// <summary>Writes a session UUID in RFC 4122/network byte order, never Guid's mixed-endian layout.</summary>
    public static void WriteSessionId(Guid sessionId, Span<byte> destination)
    {
        if (destination.Length < SessionIdLength)
            throw new ArgumentException($"Destination must contain at least {SessionIdLength} bytes.", nameof(destination));
        if (!sessionId.TryWriteBytes(destination, bigEndian: true, out var written) || written != SessionIdLength)
            throw new InvalidOperationException("Unable to encode the session ID.");
    }

    public static Guid ReadSessionId(ReadOnlySpan<byte> source)
    {
        if (source.Length != SessionIdLength)
            throw new ArgumentException($"A session ID must contain exactly {SessionIdLength} bytes.", nameof(source));
        return new Guid(source, bigEndian: true);
    }
}
