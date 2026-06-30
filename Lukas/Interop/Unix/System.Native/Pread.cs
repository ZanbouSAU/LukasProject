// Lukas/Interop/Unix/System.Native/Pread.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// pread(2) 的封装：从指定偏移读取，不改变文件位置。
internal static unsafe partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "pread", SetLastError = true)]
    private static partial int PreadForMacOs(nint fd, byte* buf, nuint count, long offset);

    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static partial int PreadForOtherUnix(nint fd, byte* buf, nuint count, long offset);
    
    internal static int Pread(nint fd, byte* buf, int count, long offset)
    {
        var n = (nuint)(uint)count;
        return OperatingSystem.IsMacOS()
            ? PreadForMacOs(fd, buf, n, offset)
            : PreadForOtherUnix(fd, buf, n, offset);
    }
}
