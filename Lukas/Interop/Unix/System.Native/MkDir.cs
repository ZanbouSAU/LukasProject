// Lukas/Interop/Unix/System.Native/MkDir.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// mkdir(2) 的封装：创建单级目录。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "mkdir", SetLastError = true)]
    private static unsafe partial int MkDirForMacOs(byte* path, int mode);
    
    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static unsafe partial int MkDirForOtherUnix(byte* path, int mode);
    
    internal static unsafe int MkDir(byte* path, int mode) 
        => OperatingSystem.IsMacOS() ? MkDirForMacOs(path, mode) : MkDirForOtherUnix(path, mode);
}
