// Lukas/Interop/Windows/Kernel32/FileFlagsAndAttributes.cs

namespace Lukas.Interop.Windows.Kernel32;

// CreateFile 的 dwFlagsAndAttributes 取值：低位是文件属性，高位是行为标志（如 OVERLAPPED 异步、无缓冲等）。

public static class FileFlagsAndAttributes
{
    internal const int FileAttributeReadOnly = 0x00000001;
    internal const int FileAttributeHidden = 0x00000002;
    internal const int FileAttributeSystem = 0x00000004;
    internal const int FileAttributeNormal = 0x00000080;
    internal const int FileAttributeTemporary = 0x00000100;
    
    internal const int FileFlagWriteThrough = unchecked((int)0x80000000);
    internal const int FileFlagOverLAppend = 0x40000000;
    
    internal const int FileFlagOverlapped = 0x40000000;
    internal const int FileFlagNoBuffering = 0x20000000;
    internal const int FileFlagRandomAccess = 0x10000000;
    internal const int FileFlagSequentialScan = 0x08000000;
    internal const int FileFlagDeleteOnClose = 0x04000000;
    internal const int FileFlagBackupSemantics = 0x02000000;
    internal const int FileFlagPosixSemantics = 0x01000000;
    internal const int FileFlagOpenReparsePoint = 0x00200000;
}
