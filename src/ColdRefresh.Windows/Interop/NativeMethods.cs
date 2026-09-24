using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ColdRefresh.Windows.Interop;

internal static partial class NativeMethods
{
    internal const uint FileIdInfo = 18;
    internal const uint FileBasicInfo = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        uint fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        uint fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle fileHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint input,
        uint inputSize,
        nint output,
        uint outputSize,
        out uint bytesReturned,
        nint overlapped);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FileId128Native
{
    // Opaque bytes. Copy directly into Core FileId128; never marshal through Guid.
    internal fixed byte Identifier[16];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FileIdInfoNative
{
    internal ulong VolumeSerialNumber;
    internal FileId128Native FileId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfoNative
{
    internal long CreationTime;
    internal long LastAccessTime;
    internal long LastWriteTime;
    internal long ChangeTime;
    internal uint FileAttributes;
}
