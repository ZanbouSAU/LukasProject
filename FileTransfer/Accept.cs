// Lukas/Interop/Unix/Linux/Accept.cs

using System.Runtime.InteropServices;

namespace FileTransfer;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "accept", SetLastError = true)]
    internal static unsafe partial int Accept(int fd, SysNet.SockAddr* addr, uint* len);
}
