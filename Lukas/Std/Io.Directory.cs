// Lukas/Io.Directory.cs

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Unicode;
using Lukas.Interop.Unix.System.Native;
using Lukas.Str;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 目录操作：判断是否存在、删除（非递归，仅空目录，对应 .NET 的 <c>Directory.Delete(path)</c>）。
    /// 路径接受 UTF-8 字节、UTF-16 字符、<see cref="Utf8StringBuilder"/> 三种形式；按平台分派系统调用。
    /// </summary>
    public static class Directory
    {
        private const int StackCap = 1024;

        /// <summary>目录是否存在（UTF-8 路径）。Windows 查属性目录位，Unix 用 stat 判 S_ISDIR。</summary>
        public static bool Exists(ReadOnlySpan<byte> path)
        {
            if (path.IsEmpty)
                return false;

            if (OperatingSystem.IsWindows())
            {
                char[]? rented = null;
                Span<char> stack = stackalloc char[StackCap];
                try
                {
                    var p = PathBuf.Utf16WithNul(path, stack, out rented);
                    return Pal.DirectoryExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                }
            }
            else
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[StackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    return Pal.DirectoryExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        public static bool Exists(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
                return false;

            if (OperatingSystem.IsWindows())
            {
                char[]? rented = null;
                Span<char> stack = stackalloc char[StackCap];
                try
                {
                    var p = PathBuf.Utf16WithNul(path, stack, out rented);
                    return Pal.DirectoryExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                }
            }
            else
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[StackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    return Pal.DirectoryExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        public static bool Exists(ref Utf8StringBuilder path)
            => Exists(path.WrittenSpan);

        /// <summary>删除一个空目录（UTF-8 路径，非递归）。失败抛 <see cref="IOException"/>（含错误码）。</summary>
        public static void Delete(ReadOnlySpan<byte> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            if (OperatingSystem.IsWindows())
            {
                char[]? rented = null;
                Span<char> stack = stackalloc char[StackCap];
                try
                {
                    var p = PathBuf.Utf16WithNul(path, stack, out rented);
                    if (Pal.RemoveDirectory(p) != 0)
                        throw new IOException($"Failed to delete directory, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                }
            }
            else
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[StackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    if (Pal.RmDir(p) != 0)
                        throw new IOException($"Failed to delete directory, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        public static void Delete(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            if (OperatingSystem.IsWindows())
            {
                char[]? rented = null;
                Span<char> stack = stackalloc char[StackCap];
                try
                {
                    var p = PathBuf.Utf16WithNul(path, stack, out rented);
                    if (Pal.RemoveDirectory(p) != 0)
                        throw new IOException($"Failed to delete directory, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                }
            }
            else
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[StackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    if (Pal.RmDir(p) != 0)
                        throw new IOException($"Failed to delete directory, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        public static void Delete(ref Utf8StringBuilder path)
            => Delete(path.WrittenSpan);

        /// <summary>
        /// 删除目录。<paramref name="recursive"/> 为 <see langword="false"/> 时仅删空目录（走原生 rmdir/RemoveDirectoryW，
        /// 与 .NET 的 <c>Directory.Delete(path)</c> 一致）；为 <see langword="true"/> 时递归删除目录内全部内容。
        /// 递归分支为原生实现：Unix 用 opendir/readdir（d_type 快速分类，未知时 lstat 兜底）+ unlink/rmdir；
        /// Windows 用 FindFirstFile/FindNextFile + DeleteFile/RemoveDirectory。符号链接只删链接本身、不跟随进入。
        /// </summary>
        public static void Delete(ReadOnlySpan<char> path, bool recursive)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            if (!recursive)
            {
                Delete(path);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                WinDeleteTree(new string(TrimTrailingSeparators(path)));
            }
            else
            {
                var trimmed = TrimTrailingSeparators(path);

                // 用 Utf8.FromUtf16 直接编码为 UTF-8（不走 Encoding.UTF8.GetBytes）；
                // 仅最终的根路径 byte[] 需要分配，递归过程中各子路径由它派生。
                var max = Encoding.UTF8.GetMaxByteCount(trimmed.Length);
                byte[]? rented = null;
                var buf = max <= StackCap ? stackalloc byte[StackCap] : rented = ArrayPool<byte>.Shared.Rent(max);
                try
                {
                    if (Utf8.FromUtf16(trimmed, buf, out _, out var written,
                            replaceInvalidSequences: false, isFinalBlock: true) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-16.");

                    UnixDeleteTree(buf[..written].ToArray());
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        // ---------- 原生递归删除实现 ----------

        // 去掉末尾的 '/'、'\'（Windows）分隔符，便于后续拼接子路径；保留根（如 "/"、"C:\"）。
        // 返回输入路径的切片视图（零分配），调用方需保证原 span 在使用期间有效。
        private static ReadOnlySpan<char> TrimTrailingSeparators(ReadOnlySpan<char> path)
        {
            var end = path.Length;
            while (end > 1)
            {
                var c = path[end - 1];
                if (c == '/' || (OperatingSystem.IsWindows() && c == '\\'))
                    end--;
                else
                    break;
            }
            return path[..end];
        }

        private static bool IsDotOrDotDot(ReadOnlySpan<byte> name)
            => name is [(byte)'.'] || name is [(byte)'.', (byte)'.'];

        private static bool IsDotOrDotDot(ReadOnlySpan<char> name)
            => name is ['.'] || name is ['.', '.'];

        // UTF-8 路径拼接：parent + '/' + name。
        private static byte[] JoinUnix(byte[] parent, ReadOnlySpan<byte> name)
        {
            var result = new byte[parent.Length + 1 + name.Length];
            parent.CopyTo(result, 0);
            result[parent.Length] = (byte)'/';
            name.CopyTo(result.AsSpan(parent.Length + 1));
            return result;
        }

        // 【Unix】递归删除：先把目录项收齐再删，避免边遍历边删的未定义行为；符号链接只 unlink、不进入。
        [UnsupportedOSPlatform("windows")]
        private static void UnixDeleteTree(byte[] path)
        {
            nint dir;
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[StackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    dir = Pal.OpenDir(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }

            if (dir == nint.Zero)
                throw new IOException($"Failed to open directory, error code: {Marshal.GetLastPInvokeError()}");

            var children = new List<(byte[] Path, int Type)>();
            try
            {
                Span<byte> nameBuf = stackalloc byte[256];
                while (Pal.ReadDir(dir, nameBuf, out var nameLen, out var dType))
                {
                    var name = nameBuf[..nameLen];
                    if (IsDotOrDotDot(name))
                        continue;
                    children.Add((JoinUnix(path, name), dType));
                }
            }
            finally
            {
                Pal.CloseDir(dir);
            }

            foreach (var (child, type) in children)
            {
                var isDir = type == Sys.DtDir;
                var isLink = type == Sys.DtLnk;

                if (type == Sys.DtUnknown)
                {
                    byte[]? rented = null;
                    Span<byte> stack = stackalloc byte[StackCap];
                    try
                    {
                        var cp = PathBuf.Utf8WithNul(child, stack, out rented);
                        if (!Pal.TryGetUnixType(cp, out isDir, out isLink))
                            isDir = isLink = false;
                    }
                    finally
                    {
                        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                    }
                }

                if (isDir && !isLink)
                    UnixDeleteTree(child);
                else
                    UnixUnlink(child);   // 文件或符号链接：只删该项，不跟随
            }

            UnixRmDir(path);
        }

        [UnsupportedOSPlatform("windows")]
        private static void UnixUnlink(byte[] path)
        {
            byte[]? rented = null;
            Span<byte> stack = stackalloc byte[StackCap];
            try
            {
                var p = PathBuf.Utf8WithNul(path, stack, out rented);
                if (Pal.Unlink(p) != 0)
                    throw new IOException($"Failed to delete file, error code: {Marshal.GetLastPInvokeError()}");
            }
            finally
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            }
        }

        [UnsupportedOSPlatform("windows")]
        private static void UnixRmDir(byte[] path)
        {
            byte[]? rented = null;
            Span<byte> stack = stackalloc byte[StackCap];
            try
            {
                var p = PathBuf.Utf8WithNul(path, stack, out rented);
                if (Pal.RmDir(p) != 0)
                    throw new IOException($"Failed to delete directory, error code: {Marshal.GetLastPInvokeError()}");
            }
            finally
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // 【Windows】递归删除：先收齐目录项再删；目录型 reparse point（符号链接/联接）只删链接、不进入。
        [SupportedOSPlatform("windows")]
        private static void WinDeleteTree(string path)
        {
            var children = new List<(string Path, int Attributes)>();

            char[]? rented = null;
            Span<char> stack = stackalloc char[StackCap];
            try
            {
                var pattern = PathBuf.Utf16WithNul((path + "\\*").AsSpan(), stack, out rented);
                Span<char> nameBuf = stackalloc char[260];

                var handle = Pal.FindFirst(pattern, nameBuf, out var nameLen, out var attrs);
                if (handle == -1)
                    throw new IOException($"Failed to enumerate directory, error code: {Marshal.GetLastPInvokeError()}");

                try
                {
                    do
                    {
                        var name = nameBuf[..nameLen];
                        if (!IsDotOrDotDot(name))
                            children.Add((path + "\\" + new string(name), attrs));
                    }
                    while (Pal.FindNext(handle, nameBuf, out nameLen, out attrs));
                }
                finally
                {
                    Pal.FindClose(handle);
                }
            }
            finally
            {
                if (rented is not null) ArrayPool<char>.Shared.Return(rented);
            }

            foreach (var (child, attrs) in children)
            {
                var isDir = (attrs & Pal.FileAttributeDirectory) != 0;
                var isReparse = (attrs & Pal.FileAttributeReparsePoint) != 0;

                if (isDir && !isReparse)
                    WinDeleteTree(child);
                else if (isDir)
                    WinRemoveDir(child);   // 目录型符号链接/联接：删链接本身，不进入
                else
                    WinDeleteFile(child);  // 普通文件或文件型符号链接
            }

            WinRemoveDir(path);
        }

        [SupportedOSPlatform("windows")]
        private static void WinDeleteFile(string path)
        {
            char[]? rented = null;
            Span<char> stack = stackalloc char[StackCap];
            try
            {
                var p = PathBuf.Utf16WithNul(path.AsSpan(), stack, out rented);
                if (Pal.DeleteFile(p) != 0)
                    throw new IOException($"Failed to delete file, error code: {Marshal.GetLastPInvokeError()}");
            }
            finally
            {
                if (rented is not null) ArrayPool<char>.Shared.Return(rented);
            }
        }

        [SupportedOSPlatform("windows")]
        private static void WinRemoveDir(string path)
        {
            char[]? rented = null;
            Span<char> stack = stackalloc char[StackCap];
            try
            {
                var p = PathBuf.Utf16WithNul(path.AsSpan(), stack, out rented);
                if (Pal.RemoveDirectory(p) != 0)
                    throw new IOException($"Failed to delete directory, error code: {Marshal.GetLastPInvokeError()}");
            }
            finally
            {
                if (rented is not null) ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
