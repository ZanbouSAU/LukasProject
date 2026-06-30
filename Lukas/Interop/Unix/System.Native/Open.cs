// Lukas/Interop/Unix/System.Native/CreateFile.cs

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

// open(2) 的封装。Apple 平台从 libSystem 导入，其余 Unix 从 libc 导入；
// 上层统一调用 Open()，无需关心平台差异。
internal static partial class Sys
{
    [LibraryImport("libSystem", EntryPoint = "open", SetLastError = true)]
    private static unsafe partial int OpenForMacOs(byte* filename, int flags, int mode);
    
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static unsafe partial int OpenForOtherUnix(byte* filename, int flags, int mode);
    
    internal static unsafe int Open(byte* filename, int flags, int mode) 
        => OperatingSystem.IsMacOS() ? OpenForMacOs(filename, flags, mode) : OpenForOtherUnix(filename, flags, mode);
}
