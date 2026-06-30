// Lukas/Interop/Windows/Kernel32/DeleteFile.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// DeleteFileW：删除文件。

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "DeleteFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeleteFilePrivate(char* path);

    internal static unsafe bool DeleteFile(ReadOnlySpan<char> path)
    {
        path = PathInternal.EnsureExtendedPrefix(path);
        fixed (char* chars = path)
        {
            return DeleteFilePrivate(chars);
        }
    }
}
