// Lukas/Interop/Unix/System.Native/Write.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// write(2) 的封装：向当前偏移写入。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial int WriteForMacOs(nint fd, byte* buf, int count);
    
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial int WriteForOtherUnix(nint fd, byte* buf, int count);
    
    internal static unsafe int Write(nint fd, byte* buf, int count) => OperatingSystem.IsMacOS() ? WriteForMacOs(fd, buf, count) : WriteForOtherUnix(fd, buf, count);
}
