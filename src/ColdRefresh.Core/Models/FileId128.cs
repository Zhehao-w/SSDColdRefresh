using System.Buffers.Binary;

namespace ColdRefresh.Core.Models;

/// <summary>An opaque Windows FILE_ID_128 value. It has no GUID semantics or byte swapping.</summary>
public readonly struct FileId128 : IEquatable<FileId128>
{
    public const int ByteLength = 16;
    private readonly ulong _lowBytes;
    private readonly ulong _highBytes;

    public FileId128(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException($"A file ID must contain exactly {ByteLength} bytes.", nameof(bytes));

        _lowBytes = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _highBytes = BinaryPrimitives.ReadUInt64LittleEndian(bytes[sizeof(ulong)..]);
    }

    public static FileId128 ReadFrom(ReadOnlySpan<byte> source) => new(source);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
            throw new ArgumentException($"Destination must contain at least {ByteLength} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _lowBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(ulong)..], _highBytes);
    }

    public byte[] ToByteArray()
    {
        var result = new byte[ByteLength];
        WriteTo(result);
        return result;
    }

    public bool IsZero => (_lowBytes | _highBytes) == 0;
    public bool Equals(FileId128 other) => _lowBytes == other._lowBytes && _highBytes == other._highBytes;
    public override bool Equals(object? obj) => obj is FileId128 other && Equals(other);

    // Stable across processes and runtimes; unlike HashCode, this is deliberately not randomized.
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + _lowBytes.GetHashCode();
            hash = (hash * 31) + _highBytes.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(FileId128 left, FileId128 right) => left.Equals(right);
    public static bool operator !=(FileId128 left, FileId128 right) => !left.Equals(right);
    public override string ToString() => Convert.ToHexString(ToByteArray());
}
