// Lukas/Interop/Windows/Kernel32/FindFile.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// FindFirstFileW/FindNextFileW/FindClose 的封装，用于目录遍历（递归删除）。
// WIN32_FIND_DATAW 用内联定长缓冲（fixed char）以保持 blittable，可被 LibraryImport 源生成器处理。

internal static partial class Kernel32
{
    internal const int FileAttributeDirectoryFlag = 0x10;
    internal const int FileAttributeReparsePoint = 0x400;

    private const int MaxPath = 260;
    private const int AlternateNameLength = 14;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct Win32FindDataW
    {
        internal int DwFileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint Reserved0;
        internal uint Reserved1;
        internal fixed char FileName[MaxPath];
        internal fixed char AlternateFileName[AlternateNameLength];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint FindFirstFilePrivate(char* lpFileName, Win32FindDataW* lpFindFileData);

    internal static unsafe nint FindFirstFile(ReadOnlySpan<char> pattern, out Win32FindDataW data)
    {
        pattern = PathInternal.EnsureExtendedPrefix(pattern);
        data = default;
        fixed (char* p = pattern)
        fixed (Win32FindDataW* d = &data)
        {
            return FindFirstFilePrivate(p, d);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool FindNextFilePrivate(nint handle, Win32FindDataW* lpFindFileData);

    internal static unsafe bool FindNextFile(nint handle, out Win32FindDataW data)
    {
        data = default;
        fixed (Win32FindDataW* d = &data)
        {
            return FindNextFilePrivate(handle, d);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClosePrivate(nint handle);

    internal static bool FindClose(nint handle) => FindClosePrivate(handle);
}
