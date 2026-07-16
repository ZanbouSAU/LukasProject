// Lukas/Interop/Windows/Kernel32/CreateFile.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// CreateFileW：打开/创建文件。两个公开重载分别用于普通同步句柄，以及带 overlapped 的异步句柄。
// 调用前统一通过 PathInternal 处理长路径前缀。

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint CreateFilePrivate(
        char* lpFileName,
        int dwDesiredAccess,
        FileShare dwShareMode,
        SecurityAttributes* lpSecurityAttributes,
        FileMode dwCreationDisposition,
        int dwFlagsAndAttributes,
        nint hTemplateFile);
    
    internal static unsafe nint CreateFile(
        ReadOnlySpan<char> lpFileName,
        int dwDesiredAccess,
        FileShare dwShareMode,
        SecurityAttributes* lpSecurityAttributes,
        FileMode dwCreationDisposition,
        int dwFlagsAndAttributes,
        nint hTemplateFile)
    {
        lpFileName = PathInternal.EnsureExtendedPrefixIfNeeded(lpFileName);
        fixed (char* chars = lpFileName)
        {
            return CreateFilePrivate(chars, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition,
                dwFlagsAndAttributes, hTemplateFile);
        }
    }

    internal static unsafe nint CreateFile(
        ReadOnlySpan<char> lpFileName,
        int dwDesiredAccess,
        FileShare dwShareMode,
        FileMode dwCreationDisposition,
        int dwFlagsAndAttributes)
    {
        lpFileName = PathInternal.EnsureExtendedPrefixIfNeeded(lpFileName);
        fixed (char* chars = lpFileName)
        {
            return CreateFilePrivate(chars, dwDesiredAccess, dwShareMode, null, dwCreationDisposition,
                dwFlagsAndAttributes, IntPtr.Zero);
        }
    }
}
