// Lukas/Interop/Windows/Kernel32/ReadFile.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Windows.Kernel32;

// ReadFile：同步重载与 overlapped（异步）重载。异步重载配合 IOCP 使用。

internal static unsafe partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static unsafe partial int ReadFile(
        nint handle,
        byte* bytes,
        int numberBytesToWrite,
        out int numBytesWritten,
        nint mustBeZero);
    
    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    internal static unsafe partial int ReadFileOverlapped(
        nint handle,
        byte* bytes,
        int numberBytesToRead,
        int* numBytesRead,
        NativeOverlapped* overlapped);
}
