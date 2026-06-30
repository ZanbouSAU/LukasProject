// Lukas/Interop/Unix/Linux/Signal.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    [LibraryImport("libc.so.6", EntryPoint = "signal", SetLastError = true)]
    internal static partial nint Signal(int signum, nint handler);
}
