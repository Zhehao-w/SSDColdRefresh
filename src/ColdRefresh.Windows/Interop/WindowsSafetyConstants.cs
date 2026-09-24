namespace ColdRefresh.Windows.Interop;

public static class WindowsSafetyConstants
{
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const FileAttributes UnsupportedAttributes =
        FileAttributes.ReparsePoint |
        FileAttributes.SparseFile |
        FileAttributes.Compressed |
        FileAttributes.Encrypted |
        FileAttributes.Offline |
        FileAttributes.System;
}
