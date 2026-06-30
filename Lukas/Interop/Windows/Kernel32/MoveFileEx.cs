// Lukas/Interop/Windows/Kernel32/MoveFileEx.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// MoveFileExW：重命名/移动文件或目录。允许跨卷（COPY_ALLOWED）；可选覆盖已存在目标（REPLACE_EXISTING）。
// 两个路径都先补扩展前缀以突破 MAX_PATH。

internal static partial class Kernel32
{
    internal const int MoveFileReplaceExisting = 0x1;
    internal const int MoveFileCopyAllowed = 0x2;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool MoveFileExPrivate(char* existingFileName, char* newFileName, int flags);

    internal static unsafe bool MoveFileEx(ReadOnlySpan<char> existingFileName, ReadOnlySpan<char> newFileName, int flags)
    {
        existingFileName = PathInternal.EnsureExtendedPrefix(existingFileName);
        newFileName = PathInternal.EnsureExtendedPrefix(newFileName);
        fixed (char* existing = existingFileName)
        fixed (char* renamed = newFileName)
        {
            return MoveFileExPrivate(existing, renamed, flags);
        }
    }
}
