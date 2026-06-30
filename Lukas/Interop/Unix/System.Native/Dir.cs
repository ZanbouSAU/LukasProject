// Lukas/Interop/Unix/System.Native/Dir.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// opendir/readdir/closedir 的封装，用于目录遍历（递归删除）。
//
// 注意：dirent 的字段偏移随平台不同——
//   Linux(glibc/musl 64 位): d_type @18, d_name @19；
//   macOS(64 位 inode):       d_type @20, d_name @21。
// macOS 上 readdir/opendir 使用 $INODE64 变体以匹配上述 64 位布局。
// d_type 取值 DT_*（各平台一致）。这些偏移/符号需要在各目标平台实测确认。
internal static partial class Sys
{
    internal const int DtUnknown = 0;
    internal const int DtDir = 4;
    internal const int DtLnk = 10;

    internal static int DirentDTypeOffset => OperatingSystem.IsMacOS() ? 20 : 18;
    internal static int DirentDNameOffset => OperatingSystem.IsMacOS() ? 21 : 19;

    [LibraryImport("libSystem", EntryPoint = "opendir$INODE64", SetLastError = true)]
    private static unsafe partial nint OpenDirForMacOs(byte* path);

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static unsafe partial nint OpenDirForOtherUnix(byte* path);

    internal static unsafe nint OpenDir(byte* path)
        => OperatingSystem.IsMacOS() ? OpenDirForMacOs(path) : OpenDirForOtherUnix(path);

    // readdir 在读到目录末尾时返回 NULL 且不置 errno，因此不开启 SetLastError。
    [LibraryImport("libSystem", EntryPoint = "readdir$INODE64")]
    private static unsafe partial byte* ReadDirForMacOs(nint dir);

    [LibraryImport("libc", EntryPoint = "readdir")]
    private static unsafe partial byte* ReadDirForOtherUnix(nint dir);

    internal static unsafe byte* ReadDir(nint dir)
        => OperatingSystem.IsMacOS() ? ReadDirForMacOs(dir) : ReadDirForOtherUnix(dir);

    [LibraryImport("libSystem", EntryPoint = "closedir", SetLastError = true)]
    private static partial int CloseDirForMacOs(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static partial int CloseDirForOtherUnix(nint dir);

    internal static int CloseDir(nint dir)
        => OperatingSystem.IsMacOS() ? CloseDirForMacOs(dir) : CloseDirForOtherUnix(dir);
}
