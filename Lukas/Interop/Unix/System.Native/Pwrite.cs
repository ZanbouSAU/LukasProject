// Lukas/Interop/Unix/System.Native/Pwrite.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// pwrite(2) 的封装：向指定偏移写入，不改变文件位置。
internal static unsafe partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "pwrite", SetLastError = true)]
    private static partial int PwriteForMacOs(nint fd, byte* buf, nuint count, long offset);
    
    [LibraryImport("libc", EntryPoint = "pwrite", SetLastError = true)]
    private static partial int PwriteForOtherUnix(nint fd, byte* buf, nuint count, long offset);
    
    internal static int Pwrite(nint fd, byte* buf, int count, long offset)
    {
        var n = (nuint)(uint)count;
        return OperatingSystem.IsMacOS()
            ? PwriteForMacOs(fd, buf, n, offset)
            : PwriteForOtherUnix(fd, buf, n, offset);
    }
}
