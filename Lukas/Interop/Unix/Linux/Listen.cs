using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "listen", SetLastError = true)]
    internal static unsafe partial int Listen(int fd, int n);
}
