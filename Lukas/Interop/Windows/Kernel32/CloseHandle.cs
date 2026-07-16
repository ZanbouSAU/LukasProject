// Lukas/Interop/Windows/Kernel32/CloseHandle.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Windows.Kernel32;

// CloseHandle：关闭内核对象句柄（文件、完成端口等）。

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
