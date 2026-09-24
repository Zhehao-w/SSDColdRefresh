namespace ColdRefresh.Core.Models;

/// <summary>Original FILE_BASIC_INFO values, retained exactly for crash-time restoration.</summary>
public readonly record struct NtfsBasicMetadata(
    long CreationTime,
    long LastAccessTime,
    long LastWriteTime,
    long ChangeTime,
    uint FileAttributes)
{
    public const int SerializedLength = 36;
}
