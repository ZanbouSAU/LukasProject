// Lukas/Interop/Unix/System.Native/Rename.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// rename(2) 的封装：原子地把 oldPath 改名/移动到 newPath（同一文件系统内）。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "rename", SetLastError = true)]
    private static unsafe partial int RenameForMacOs(byte* oldPath, byte* newPath);

    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static unsafe partial int RenameForOtherUnix(byte* oldPath, byte* newPath);

    internal static unsafe int Rename(byte* oldPath, byte* newPath)
        => OperatingSystem.IsMacOS() ? RenameForMacOs(oldPath, newPath) : RenameForOtherUnix(oldPath, newPath);
}
