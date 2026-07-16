// Lukas/Interop/Windows/Kernel32/RemoveDirectory.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// RemoveDirectoryW：删除一个空目录（非空会失败）。对外封装先补扩展前缀以突破 MAX_PATH。

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveDirectoryPrivate(char* path);

    internal static unsafe bool RemoveDirectory(ReadOnlySpan<char> path)
    {
        path = PathInternal.EnsureExtendedPrefix(path);
        fixed (char* chars = path)
        {
            return RemoveDirectoryPrivate(chars);
        }
    }
}
