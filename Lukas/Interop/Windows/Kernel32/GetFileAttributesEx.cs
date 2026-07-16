/*
 * 为支持 Str 中的 IocpEngine、Io.Pal，需引入并采用 .NET Runtime 源码
 * 见 https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/Interop/Windows/Kernel32/Interop.GetFileAttributesEx.cs
 * 本文件完全遵循 .NET Runtime 许可
 */

// Lukas/Interop/Windows/Kernel32/GetFileAttributesEx.cs

using System;
using System.Runtime.InteropServices;
using Lukas.Std;

namespace Lukas.Interop.Windows.Kernel32;

// GetFileAttributesExW：一次取回文件属性与大小等信息，用于 Exists / 查询文件长度。

internal static partial class Kernel32
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32FileAttributeData
    {
        internal int FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
    }
    
    private const int GetFileExInfoStandard = 0;

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileAttributesExPrivate(
        char* lpFileName,
        int fInfoLevelId,
        out Win32FileAttributeData lpFileInformation);

    internal static unsafe bool GetFileAttributesEx(ReadOnlySpan<char> path, out Win32FileAttributeData data)
    {
        path = PathInternal.EnsureExtendedPrefix(path);
        fixed (char* chars = path)
        {
            return GetFileAttributesExPrivate(chars, GetFileExInfoStandard, out data);
        }
    }
}
