// Lukas/Interop/Windows/Kernel32/CreateDirectory.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// CreateDirectoryW：创建单级目录。对外封装会先把路径补上扩展前缀以突破 MAX_PATH 限制。

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateDirectoryPrivate(
        char* path,
        SecurityAttributes* lpSecurityAttributes);

    internal static unsafe bool CreateDirectory(ReadOnlySpan<char> path, SecurityAttributes* lpSecurityAttributes)
    {
        path = PathInternal.EnsureExtendedPrefix(path);
        fixed (char* chars = path)
        {
            return CreateDirectoryPrivate(chars, lpSecurityAttributes);
        }
    }
}
