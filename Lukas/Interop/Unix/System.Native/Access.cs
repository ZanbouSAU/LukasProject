// Lukas/Interop/Unix/System.Native/Access.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// access(2) 的封装：探测路径是否存在/可访问（F_OK）。
internal static partial class Sys
{
    internal const int FOk = 0;

    [LibraryImport("libSystem", EntryPoint = "access", SetLastError = true)]
    private static unsafe partial int AccessForMacOs(byte* path, int mode);

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true)]
    private static unsafe partial int AccessForOtherUnix(byte* path, int mode);

    internal static unsafe int Access(byte* path, int mode)
        => OperatingSystem.IsMacOS() ? AccessForMacOs(path, mode) : AccessForOtherUnix(path, mode);
}
