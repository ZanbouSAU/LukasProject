// Lukas/Interop/Unix/System.Native/Close.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// close(2) 的封装：关闭文件描述符。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseForMacOs(nint fd);
    
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseForOtherUnix(nint fd);
    
    internal static int Close(nint fd)
        => OperatingSystem.IsMacOS() ? CloseForMacOs(fd) : CloseForOtherUnix(fd);
}
