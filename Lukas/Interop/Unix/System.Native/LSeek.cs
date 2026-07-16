// Lukas/Interop/Unix/System.Native/LSeek.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// lseek(2) 的封装，外加 SEEK_SET/CUR/END 常量。常用于查询文件大小（seek 到末尾）。
internal static partial class Sys
{
    internal const int SeekSet = 0;
    internal const int SeekCur = 1;
    internal const int SeekEnd = 2;

    [LibraryImport("libSystem", EntryPoint = "lseek", SetLastError = true)]
    private static partial long LSeekForMacOs(nint fd, long offset, int whence);

    [LibraryImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static partial long LSeekForOtherUnix(nint fd, long offset, int whence);

    internal static long LSeek(nint fd, long offset, int whence)
        => OperatingSystem.IsMacOS() ? LSeekForMacOs(fd, offset, whence) : LSeekForOtherUnix(fd, offset, whence);
}
