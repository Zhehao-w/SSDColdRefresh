namespace ColdRefresh.Core.Models;

public readonly record struct FileIdentity(ulong VolumeSerialNumber, FileId128 FileId)
{
    public override string ToString() => $"{VolumeSerialNumber:X16}:{FileId}";
}
