// Lukas/Interop/Unix/Linux/TcpSocket.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "socket", SetLastError = true)]
    internal static unsafe partial int Socket(int domain, int type, int protocol);
}
