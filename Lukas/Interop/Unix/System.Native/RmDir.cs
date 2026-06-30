// Lukas/Interop/Unix/System.Native/RmDir.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// rmdir(2) 的封装：删除一个空目录（非空返回 ENOTEMPTY）。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "rmdir", SetLastError = true)]
    private static unsafe partial int RmDirForMacOs(byte* path);

    [LibraryImport("libc", EntryPoint = "rmdir", SetLastError = true)]
    private static unsafe partial int RmDirForOtherUnix(byte* path);

    internal static unsafe int RmDir(byte* path)
        => OperatingSystem.IsMacOS() ? RmDirForMacOs(path) : RmDirForOtherUnix(path);
}
