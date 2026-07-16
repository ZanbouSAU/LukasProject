// Lukas/Interop/Unix/Linux/Bind.cs

using System.Runtime.InteropServices;

namespace FileTransfer;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "bind", SetLastError = true)]
    internal static unsafe partial int Bind(int fd, SockAddr* addr, uint len);
}
