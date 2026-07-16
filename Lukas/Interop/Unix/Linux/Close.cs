// Lukas/Interop/Unix/Linux/Close.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
    internal static unsafe partial int Close(int fd);
}
