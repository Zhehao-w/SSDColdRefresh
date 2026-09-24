namespace ColdRefresh.Core.Models;

public readonly record struct FileIdentity(ulong VolumeSerialNumber, Guid FileId)
{
    public override string ToString() => $"{VolumeSerialNumber:X16}:{FileId:N}";
}
