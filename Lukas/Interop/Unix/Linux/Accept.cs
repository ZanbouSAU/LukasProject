// Lukas/Interop/Unix/Linux/Accept.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "accept", SetLastError = true)]
    internal static unsafe partial int Accept(int fd, SockAddr* addr, uint* len);
}
