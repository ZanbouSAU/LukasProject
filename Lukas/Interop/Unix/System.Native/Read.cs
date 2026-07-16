// Lukas/Interop/Unix/System.Native/Read.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// read(2) 的封装：从当前偏移读取。
internal static unsafe partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "read", SetLastError = true)]
    private static partial int ReadForMacOs(nint fd, byte* buf, int count);
    
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial int ReadForOtherUnix(nint fd, byte* buf, int count);
    
    internal static int Read(nint fd, byte* buf, int count)
        => OperatingSystem.IsMacOS() ? ReadForMacOs(fd, buf, count) : ReadForOtherUnix(fd, buf, count);
}
