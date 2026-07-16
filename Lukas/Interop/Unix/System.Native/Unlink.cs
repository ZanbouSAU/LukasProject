// Lukas/Interop/Unix/System.Native/Unlink.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// unlink(2) 的封装：删除文件（或符号链接）。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "unlink", SetLastError = true)]
    private static unsafe partial int UnlinkForMacOs(byte* path);
    
    [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static unsafe partial int UnlinkForOtherUnix(byte* path);
    
    internal static unsafe int Unlink(byte* path)
        => OperatingSystem.IsMacOS() ? UnlinkForMacOs(path) : UnlinkForOtherUnix(path);
}
