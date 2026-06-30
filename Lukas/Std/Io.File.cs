// Lukas/Io.File.cs

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using Lukas.Interop.Unix.System.Native;
using Lukas.Str;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 同步文件句柄，直接基于平台原语（Windows 句柄 / Unix fd）实现，不依赖 <see cref="System.IO"/>。
    ///
    /// 实例方法（Open/Read/Write/Close）操作单个已打开的文件，读写分别复用 <see cref="InBase"/>/<see cref="OutBase"/>，
    /// 同一实例同一时刻只用于读或只用于写。另提供一组<b>静态</b>工具方法做文件系统操作：
    /// 递归建目录、删除、复制、判断存在、取大小/文件名、整体读取（字节/文本/按行）。
    /// 路径同时接受 UTF-8 字节、UTF-16 字符和 <see cref="Utf8StringBuilder"/> 三种形式。
    /// </summary>
    public class File : IDisposable
    {
        private nint _handle = InvalidHandle;
        private readonly Lock _lock = new();
        private bool _disposed;

        private OutBase? _writer;
        private InBase? _reader;

        private static readonly nint InvalidHandle = new(-1);

        /// <summary>
        /// 打开文件（路径为 UTF-8 字节）。会先释放当前已打开的句柄。
        /// Windows 下先把路径转 UTF-16 再调 CreateFile，其余平台补 NUL 后调 open。
        /// </summary>
        public void Open(ReadOnlySpan<byte> filename, Flags flags = Flags.Append)
        {
            lock (_lock)
            {
                ReleaseLocked();

                if (OperatingSystem.IsWindows())
                {
                    Span<char> chars = stackalloc char[filename.Length + 1];
                    if (Utf8.ToUtf16(filename, chars, out _, out var written) != OperationStatus.Done)
                        throw new IOException("File name contains invalid UTF-8.");

                    chars[written] = '\0';
                    CompleteOpenLocked(Pal.CreateFile(chars[..(written + 1)], flags), flags);
                }
                else
                {
                    Span<byte> bytes = stackalloc byte[filename.Length + 1];
                    filename.CopyTo(bytes);
                    bytes[filename.Length] = 0;
                    CompleteOpenLocked(Pal.Open(bytes, flags), flags);
                }
            }
        }

        /// <summary>打开文件（路径为 UTF-16 字符）。语义同字节版重载。</summary>
        public void Open(ReadOnlySpan<char> filename, Flags flags = Flags.Append)
        {
            lock (_lock)
            {
                ReleaseLocked();

                if (OperatingSystem.IsWindows())
                {
                    Span<char> chars = stackalloc char[filename.Length + 1];
                    filename.CopyTo(chars);
                    chars[filename.Length] = '\0';
                    CompleteOpenLocked(Pal.CreateFile(chars, flags), flags);
                }
                else
                {
                    Span<byte> bytes = stackalloc byte[Encoding.UTF8.GetMaxByteCount(filename.Length) + 1];
                    if (Utf8.FromUtf16(filename, bytes, out _, out var written) != OperationStatus.Done)
                        throw new IOException("File name contains invalid UTF-16.");

                    bytes[written] = 0;
                    CompleteOpenLocked(Pal.Open(bytes[..(written + 1)], flags), flags);
                }
            }
        }

        // 以下 Write/WriteLine/Read/ReadLine 系列都是加锁后转发给已打开的写端/读端的薄封装。
        public void Write(ReadOnlySpan<byte> contents, bool isLine = false)
        {
            lock (_lock)
            {
                EnsureWritableLocked();
                _writer!.Write(contents, isLine);
            }
        }

        public void WriteLine(ReadOnlySpan<byte> contents)
        {
            lock (_lock)
            {
                EnsureWritableLocked();
                _writer!.Write(contents, isLine: true);
            }
        }

        public void Write(ReadOnlySpan<char> contents, bool isLine = false)
        {
            lock (_lock)
            {
                EnsureWritableLocked();
                _writer!.Write(contents, isLine);
            }
        }

        public void WriteLine(ReadOnlySpan<char> contents)
        {
            lock (_lock)
            {
                EnsureWritableLocked();
                _writer!.Write(contents, isLine: true);
            }
        }
        
        public int ReadToEnd(ref Utf8StringBuilder sb)
        {
            lock (_lock)
            {
                EnsureReadableLocked();
                return _reader!.Read(ref sb);
            }
        }
        
        public bool ReadLine(ref Utf8StringBuilder sb)
        {
            lock (_lock)
            {
                EnsureReadableLocked();
                return _reader!.ReadLine(ref sb);
            }
        }
        
        public string? ReadLine()
        {
            lock (_lock)
            {
                EnsureReadableLocked();
                return _reader!.ReadLine();
            }
        }
        
        public int Read(Span<byte> buffer)
        {
            lock (_lock)
            {
                EnsureReadableLocked();
                return _reader!.Read(buffer);
            }
        }
        
        public int Read(Span<char> buffer)
        {
            lock (_lock)
            {
                EnsureReadableLocked();
                return _reader!.Read(buffer);
            }
        }
        
        public int Read()
        {
            lock (_lock)
            {
                EnsureReadableLocked();
                return _reader!.Read();
            }
        }
        
        /// <summary>关闭文件并释放读/写端与底层句柄；可重复调用。</summary>
        public void Close()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                ReleaseLocked();
            }
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }
        
        // 终结器兜底：未经 Dispose 时也要把原始句柄还给系统。
        ~File()
        {
            if (_handle != InvalidHandle)
            {
                if (OperatingSystem.IsWindows())
                {
                    Pal.CloseHandle(_handle);
                }
                else
                {
                    Pal.Close(_handle);
                }
                _handle = InvalidHandle;
            }
        }
        
        private void ReleaseLocked()
        {
            _writer?.Dispose();
            _writer = null;

            _reader?.Dispose();
            _reader = null;

            if (_handle != InvalidHandle)
            {
                if (OperatingSystem.IsWindows())
                {
                    Pal.CloseHandle(_handle);
                }
                else
                {
                    Pal.Close(_handle);
                }
                _handle = InvalidHandle;
            }
        }
        
        // 打开成功后记录句柄并按 flags 建立读端或写端；失败（句柄无效）则带 errno 抛出。
        private void CompleteOpenLocked(nint handle, Flags flags)
        {
            if (handle == InvalidHandle)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException($"File open or create failed, error code: {error}");
            }

            _handle = handle;

            if (flags == Flags.Read)
                _reader = new InBase(_handle);
            else
                _writer = new OutBase(_handle);

            _disposed = false;
        }
        
        private void EnsureWritableLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(File));
            if (_writer == null)
                throw new InvalidOperationException("File is not open for writing.");
        }

        private void EnsureReadableLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(File));
            if (_reader == null)
                throw new InvalidOperationException("File is not open for reading.");
        }
        
        private const int LinuxEExist = 17;
        private const int WindowsErrorAlreadyExists = 183;
        
        private const int LinuxENoEnt = 2;
        private const int WindowsErrorFileNotFound = 2;
        private const int WindowsErrorPathNotFound = 3;
        
        /// <summary>递归创建目录（路径为 UTF-8 字节），逐级 mkdir，已存在的层级视为成功。</summary>
        public static void CreateDirectories(ReadOnlySpan<byte> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            var isWindows = OperatingSystem.IsWindows();
            EnumerateAndMake(path, isWindows);
        }
        
        public static void CreateDirectories(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            var isWindows = OperatingSystem.IsWindows();
            EnumerateAndMakeChars(path, isWindows);
        }
        
        public static void CreateDirectories(ref Utf8StringBuilder path)
        {
            CreateDirectories(path.WrittenSpan);
        }
        
        private static void EnumerateAndMake(ReadOnlySpan<byte> path, bool isWindows)
        {
            const int stackCap = 1024;
            var capacity = path.Length + 1;

            if (capacity <= stackCap)
            {
                Span<byte> buf = stackalloc byte[stackCap];
                MakeWalk(path, buf, isWindows);
            }
            else
            {
                Span<byte> heap = new byte[capacity];
                MakeWalk(path, heap, isWindows);
            }
        }

        // 沿路径逐字符扫描，每遇到分隔符就对「到此为止的前缀」调用一次 mkdir，从而自顶向下建出整条路径。
        // Windows 下单独的盘符段（如 "C:"）跳过不建。
        private static void MakeWalk(ReadOnlySpan<byte> path, Span<byte> buf, bool isWindows)
        {
            var len = 0;
            var hasPendingSegment = false;

            for (var i = 0; i <= path.Length; i++)
            {
                var atEnd = i == path.Length;
                var b = atEnd ? (byte)0 : path[i];
                var isSep = !atEnd && (b == (byte)'/' || (isWindows && b == (byte)'\\'));

                if (atEnd || isSep)
                {
                    if (hasPendingSegment)
                    {
                        var current = buf[..len];

                        var isDriveOnly = isWindows
                            && current.Length == 2
                            && current[1] == (byte)':'
                            && IsAsciiLetter(current[0]);

                        if (!isDriveOnly)
                            MkOneBytes(current, isWindows);

                        hasPendingSegment = false;
                    }

                    if (isSep)
                    {
                        buf[len++] = isWindows ? (byte)'\\' : (byte)'/';
                    }
                }
                else
                {
                    buf[len++] = b;
                    hasPendingSegment = true;
                }
            }
        }
        
        private static void EnumerateAndMakeChars(ReadOnlySpan<char> path, bool isWindows)
        {
            if (!isWindows)
            {
                Span<byte> bytes = stackalloc byte[1024];
                var max = Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
                if (max <= bytes.Length)
                {
                    if (Utf8.FromUtf16(path, bytes, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-16.");
                    EnumerateAndMake(bytes[..written], false);
                }
                else
                {
                    var heap = new byte[max];
                    if (Utf8.FromUtf16(path, heap, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-16.");
                    EnumerateAndMake(heap.AsSpan(0, written), false);
                }
                return;
            }
            
            const int stackCap = 1024;
            var capacity = path.Length + 2;
            if (capacity <= stackCap)
            {
                Span<char> buf = stackalloc char[stackCap];
                MakeWalkChars(path, buf);
            }
            else
            {
                var heap = new char[capacity];
                MakeWalkChars(path, heap.AsSpan());
            }
        }

        private static void MakeWalkChars(ReadOnlySpan<char> path, Span<char> buf)
        {
            var len = 0;
            var hasPendingSegment = false;

            for (var i = 0; i <= path.Length; i++)
            {
                var atEnd = i == path.Length;
                var c = atEnd ? '\0' : path[i];
                var isSep = !atEnd && (c == '/' || c == '\\');

                if (atEnd || isSep)
                {
                    if (hasPendingSegment)
                    {
                        var current = buf[..len];

                        var isDriveOnly = current.Length == 2
                            && current[1] == ':'
                            && IsAsciiLetter((byte)current[0]);

                        if (!isDriveOnly)
                        {
                            if (len + 1 > buf.Length)
                                throw new IOException("Path segment too long.");
                            buf[len] = '\0';
                            MkOneChars(buf[..(len + 1)]);
                        }

                        hasPendingSegment = false;
                    }

                    if (isSep)
                    {
                        buf[len++] = '\\';
                    }
                }
                else
                {
                    buf[len++] = c;
                    hasPendingSegment = true;
                }
            }
        }

        private static bool IsAsciiLetter(byte b)
            => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

        private static unsafe void MkOneBytes(ReadOnlySpan<byte> dir, bool isWindows)
        {
            int rc;
            if (isWindows)
            {
                Span<char> chars = stackalloc char[1024];
                var max = dir.Length + 1;
                if (max <= chars.Length)
                {
                    if (Utf8.ToUtf16(dir, chars, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-8.");
                    chars[written] = '\0';
                    rc = Pal.CreateDirectory(chars[..(written + 1)]);
                }
                else
                {
                    Span<char> heap = new char[max];
                    if (Utf8.ToUtf16(dir, heap, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-8.");
                    heap[written] = '\0';
                    rc = Pal.CreateDirectory(heap[..(written + 1)]);
                }
            }
            else
            {
                Span<byte> withNul = stackalloc byte[1024];
                if (dir.Length + 1 <= withNul.Length)
                {
                    dir.CopyTo(withNul);
                    withNul[dir.Length] = 0;
                    rc = Pal.MkDir(withNul[..(dir.Length + 1)]);
                }
                else
                {
                    Span<byte> heap = new byte[dir.Length + 1];
                    dir.CopyTo(heap);
                    heap[dir.Length] = 0;
                    rc = Pal.MkDir(heap);
                }
            }

            CheckMkResult(rc, dir, isWindows);
        }

        private static unsafe void MkOneChars(ReadOnlySpan<char> dirWithNul)
        {
            var rc = Pal.CreateDirectory(dirWithNul);
            CheckMkResultWin(rc, dirWithNul);
        }

        // 判定 mkdir 返回值：成功或「已存在」都算 OK，其余 errno 视为失败抛出。
        private static void CheckMkResult(int rc, ReadOnlySpan<byte> dir, bool isWindows)
        {
            if (rc == 0)
                return;

            var err = Marshal.GetLastPInvokeError();
            var alreadyExists = isWindows
                ? err == WindowsErrorAlreadyExists
                : err == LinuxEExist;

            if (!alreadyExists)
            {
                var name = Encoding.UTF8.GetString(dir);
                throw new IOException($"Failed to create directory \"{name}\", error code: {err}");
            }
        }

        private static void CheckMkResultWin(int rc, ReadOnlySpan<char> dirWithNul)
        {
            if (rc == 0)
                return;

            var err = Marshal.GetLastPInvokeError();
            if (err == WindowsErrorAlreadyExists)
                return;
            
            var name = dirWithNul[^1] == '\0' ? dirWithNul[..^1].ToString() : dirWithNul.ToString();
            throw new IOException($"Failed to create directory \"{name}\", error code: {err}");
        }
        
        /// <summary>删除文件（UTF-8 路径）。文件本就不存在时视为成功，不抛异常。</summary>
        public static void DeleteFile(ReadOnlySpan<byte> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            var isWindows = OperatingSystem.IsWindows();
            int rc;

            if (isWindows)
            {
                const int stackCap = 1024;
                var max = path.Length + 1;
                if (max <= stackCap)
                {
                    Span<char> chars = stackalloc char[stackCap];
                    if (Utf8.ToUtf16(path, chars, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-8.");
                    chars[written] = '\0';
                    rc = Pal.DeleteFile(chars[..(written + 1)]);
                }
                else
                {
                    var heap = new char[max];
                    if (Utf8.ToUtf16(path, heap, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-8.");
                    heap[written] = '\0';
                    rc = Pal.DeleteFile(heap.AsSpan(0, written + 1));
                }
            }
            else
            {
                const int stackCap = 1024;
                var max = path.Length + 1;
                if (max <= stackCap)
                {
                    Span<byte> bytes = stackalloc byte[stackCap];
                    path.CopyTo(bytes);
                    bytes[path.Length] = 0;
                    rc = Pal.Unlink(bytes[..max]);
                }
                else
                {
                    var heap = new byte[max];
                    path.CopyTo(heap);
                    heap[path.Length] = 0;
                    rc = Pal.Unlink(heap.AsSpan());
                }
            }

            CheckDeleteResult(rc, path, isWindows);
        }
        
        public static void DeleteFile(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            var isWindows = OperatingSystem.IsWindows();
            int rc;

            if (isWindows)
            {
                const int stackCap = 1024;
                var max = path.Length + 1;
                if (max <= stackCap)
                {
                    Span<char> chars = stackalloc char[stackCap];
                    path.CopyTo(chars);
                    chars[path.Length] = '\0';
                    rc = Pal.DeleteFile(chars[..max]);
                }
                else
                {
                    var heap = new char[max];
                    path.CopyTo(heap);
                    heap[path.Length] = '\0';
                    rc = Pal.DeleteFile(heap.AsSpan());
                }
            }
            else
            {
                const int stackCap = 1024;
                var max = Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
                if (max <= stackCap)
                {
                    Span<byte> bytes = stackalloc byte[stackCap];
                    if (Utf8.FromUtf16(path, bytes, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-16.");
                    bytes[written] = 0;
                    rc = Pal.Unlink(bytes[..(written + 1)]);
                }
                else
                {
                    var heap = new byte[max];
                    if (Utf8.FromUtf16(path, heap, out _, out var written) != OperationStatus.Done)
                        throw new IOException("Path contains invalid UTF-16.");
                    heap[written] = 0;
                    rc = Pal.Unlink(heap.AsSpan(0, written + 1));
                }
            }

            CheckDeleteResultChars(rc, path, isWindows);
        }
        
        public static void DeleteFile(ref Utf8StringBuilder path)
        {
            DeleteFile(path.WrittenSpan);
        }

        // 判定删除返回值：成功或「文件不存在」都算 OK，其余 errno 抛出。
        private static void CheckDeleteResult(int rc, ReadOnlySpan<byte> path, bool isWindows)
        {
            if (rc == 0)
                return;

            var err = Marshal.GetLastPInvokeError();
            var notFound = isWindows
                ? (err == WindowsErrorFileNotFound || err == WindowsErrorPathNotFound)
                : err == LinuxENoEnt;

            if (notFound)
                return;

            var name = Encoding.UTF8.GetString(path);
            throw new IOException($"Failed to delete file \"{name}\", error code: {err}");
        }

        private static void CheckDeleteResultChars(int rc, ReadOnlySpan<char> path, bool isWindows)
        {
            if (rc == 0)
                return;

            var err = Marshal.GetLastPInvokeError();
            var notFound = isWindows
                ? (err == WindowsErrorFileNotFound || err == WindowsErrorPathNotFound)
                : err == LinuxENoEnt;

            if (notFound)
                return;

            throw new IOException($"Failed to delete file \"{path.ToString()}\", error code: {err}");
        }
        
        /// <summary>复制文件（UTF-8 路径）。自动创建目标父目录；源与目标必须不同。</summary>
        public static void Copy(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination)
        {
            if (source.IsEmpty)
                throw new ArgumentException("Source path is empty.", nameof(source));
            if (destination.IsEmpty)
                throw new ArgumentException("Destination path is empty.", nameof(destination));
            if (source.SequenceEqual(destination))
                throw new ArgumentException("Source and destination must differ.", nameof(destination));
            
            var parent = GetParentBytes(destination, OperatingSystem.IsWindows());
            if (!parent.IsEmpty)
                CreateDirectories(parent);

            using var src = new File();
            using var dst = new File();
            src.Open(source, Flags.Read);
            dst.Open(destination, Flags.Create);

            CopyLoop(src, dst);
        }
        
        public static void Copy(ReadOnlySpan<char> source, ReadOnlySpan<char> destination)
        {
            if (source.IsEmpty)
                throw new ArgumentException("Source path is empty.", nameof(source));
            if (destination.IsEmpty)
                throw new ArgumentException("Destination path is empty.", nameof(destination));
            if (source.SequenceEqual(destination))
                throw new ArgumentException("Source and destination must differ.", nameof(destination));

            var parent = GetParentChars(destination);
            if (!parent.IsEmpty)
                CreateDirectories(parent);

            using var src = new File();
            using var dst = new File();
            src.Open(source, Flags.Read);
            dst.Open(destination, Flags.Create);

            CopyLoop(src, dst);
        }
        
        public static void Copy(ref Utf8StringBuilder source, ref Utf8StringBuilder destination)
        {
            Copy(source.WrittenSpan, destination.WrittenSpan);
        }
        
        // 用 8 KiB 栈缓冲在源/目标之间循环搬运，直到读到 EOF。
        private static void CopyLoop(File src, File dst)
        {
            const int bufSize = 8 * 1024;
            Span<byte> buf = stackalloc byte[bufSize];

            while (true)
            {
                var n = src.Read(buf);
                if (n <= 0) break;
                dst.Write(buf[..n]);
            }
        }
        
        // 取路径的父目录段：无分隔符返回空；根 "/" 返回 "/"；Windows 盘符根 "C:\" 保留盘符。
        private static ReadOnlySpan<byte> GetParentBytes(ReadOnlySpan<byte> path, bool isWindows)
        {
            var lastSep = -1;
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var b = path[i];
                if (b == (byte)'/' || (isWindows && b == (byte)'\\'))
                {
                    lastSep = i;
                    break;
                }
            }

            switch (lastSep)
            {
                case < 0:
                    return ReadOnlySpan<byte>.Empty;
                case 0:
                    return path[..1];
            }

            if (isWindows && lastSep == 2
                          && path[1] == (byte)':'
                          && IsAsciiLetter(path[0]))
            {
                return path[..3];
            }

            return path[..lastSep];
        }
        
        private static ReadOnlySpan<char> GetParentChars(ReadOnlySpan<char> path)
        {
            var isWindows = OperatingSystem.IsWindows();
            var lastSep = -1;
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var c = path[i];
                if (c == '/' || (isWindows && c == '\\'))
                {
                    lastSep = i;
                    break;
                }
            }

            switch (lastSep)
            {
                case < 0:
                    return ReadOnlySpan<char>.Empty;
                case 0:
                    return path[..1];
            }

            if (isWindows && lastSep == 2
                          && path[1] == ':'
                          && IsAsciiLetter((byte)path[0]))
            {
                return path[..3];
            }

            return path[..lastSep];
        }
        
        /// <summary>判断文件是否存在（UTF-8 路径）。Windows 查文件属性，Unix 用 access(2)。</summary>
        /// <summary>
        /// 路径是否存在且“不是目录”（语义同 .NET <c>File.Exists</c>：跟随符号链接，目录返回 <see langword="false"/>）。
        /// 注意：这不是“路径是否存在”——判断目录请用 <see cref="Directory.Exists(System.ReadOnlySpan{byte})"/>。
        /// </summary>
        public static bool Exists(ReadOnlySpan<byte> path)
        {
            if (path.IsEmpty)
                return false;

            const int stackCap = 1024;
            if (OperatingSystem.IsWindows())
            {
                char[]? rented = null;
                Span<char> stack = stackalloc char[stackCap];
                try
                {
                    var p = PathBuf.Utf16WithNul(path, stack, out rented);
                    return Pal.FileExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                }
            }
            else
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[stackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    return Pal.FileExists(p);
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

            const int stackCap = 1024;
            if (OperatingSystem.IsWindows())
            {
                char[]? rented = null;
                Span<char> stack = stackalloc char[stackCap];
                try
                {
                    var p = PathBuf.Utf16WithNul(path, stack, out rented);
                    return Pal.FileExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                }
            }
            else
            {
                byte[]? rented = null;
                Span<byte> stack = stackalloc byte[stackCap];
                try
                {
                    var p = PathBuf.Utf8WithNul(path, stack, out rented);
                    return Pal.FileExists(p);
                }
                finally
                {
                    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        public static bool Exists(ref Utf8StringBuilder path)
        {
            return Exists(path.WrittenSpan);
        }

        /// <summary>取文件字节大小（UTF-8 路径）。Windows 读属性，Unix 打开后 lseek 到末尾。</summary>
        public static long GetFileLength(ReadOnlySpan<byte> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            if (OperatingSystem.IsWindows())
            {
                if (!WinAttributesFromUtf8(path, out var size))
                    throw new IOException(
                        $"Failed to query file size, error code: {Marshal.GetLastPInvokeError()}");
                return size;
            }

            return UnixSizeFromUtf8(path);
        }

        public static long GetFileLength(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
                throw new ArgumentException("Path is empty.", nameof(path));

            if (OperatingSystem.IsWindows())
            {
                if (!WinAttributesFromUtf16(path, out var size))
                    throw new IOException(
                        $"Failed to query file size, error code: {Marshal.GetLastPInvokeError()}");
                return size;
            }

            return UnixSizeFromUtf16(path);
        }

        public static long GetFileLength(ref Utf8StringBuilder path)
        {
            return GetFileLength(path.WrittenSpan);
        }

        /// <summary>取路径中的文件名部分（最后一个分隔符之后；Windows 还把 ':' 视为分隔符）。</summary>
        public static ReadOnlySpan<byte> GetFileName(ReadOnlySpan<byte> path)
        {
            var isWindows = OperatingSystem.IsWindows();
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var b = path[i];
                if (b == (byte)'/' || (isWindows && (b == (byte)'\\' || b == (byte)':')))
                    return path[(i + 1)..];
            }

            return path;
        }

        public static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path)
        {
            var isWindows = OperatingSystem.IsWindows();
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var c = path[i];
                if (c == '/' || (isWindows && (c == '\\' || c == ':')))
                    return path[(i + 1)..];
            }

            return path;
        }

        public static string GetFileName(string path)
        {
            var isWindows = OperatingSystem.IsWindows();
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var c = path[i];
                if (c == '/' || (isWindows && (c == '\\' || c == ':')))
                    return path[(i + 1)..];
            }

            return path;
        }

        public static ReadOnlySpan<byte> GetFileName(ref Utf8StringBuilder path)
        {
            return GetFileName(path.WrittenSpan);
        }

        /// <summary>
        /// 移动/重命名文件（UTF-8 路径）。Unix 用 rename(2)，Windows 用 MoveFileExW（允许跨卷）。
        /// <paramref name="overwrite"/> 为 <see langword="false"/>（默认）时目标已存在则抛错；为真时覆盖。
        /// </summary>
        public static void Move(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, bool overwrite = false)
        {
            if (source.IsEmpty)
                throw new ArgumentException("Source path is empty.", nameof(source));
            if (destination.IsEmpty)
                throw new ArgumentException("Destination path is empty.", nameof(destination));

            const int stackCap = 1024;

            if (OperatingSystem.IsWindows())
            {
                char[]? srcRented = null;
                char[]? dstRented = null;
                Span<char> srcStack = stackalloc char[stackCap];
                Span<char> dstStack = stackalloc char[stackCap];
                try
                {
                    var s = PathBuf.Utf16WithNul(source, srcStack, out srcRented);
                    var d = PathBuf.Utf16WithNul(destination, dstStack, out dstRented);
                    if (Pal.MoveFileEx(s, d, overwrite) != 0)
                        throw new IOException($"Failed to move file, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (srcRented is not null) ArrayPool<char>.Shared.Return(srcRented);
                    if (dstRented is not null) ArrayPool<char>.Shared.Return(dstRented);
                }
            }
            else
            {
                // POSIX rename 总会覆盖已存在的目标；overwrite=false 时先显式检查并拒绝（存在 TOCTOU，但与基本语义一致）。
                if (!overwrite && Exists(destination))
                    throw new IOException("Destination file already exists.");

                byte[]? srcRented = null;
                byte[]? dstRented = null;
                Span<byte> srcStack = stackalloc byte[stackCap];
                Span<byte> dstStack = stackalloc byte[stackCap];
                try
                {
                    var s = PathBuf.Utf8WithNul(source, srcStack, out srcRented);
                    var d = PathBuf.Utf8WithNul(destination, dstStack, out dstRented);
                    if (Pal.Rename(s, d) != 0)
                        throw new IOException($"Failed to move file, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (srcRented is not null) ArrayPool<byte>.Shared.Return(srcRented);
                    if (dstRented is not null) ArrayPool<byte>.Shared.Return(dstRented);
                }
            }
        }

        public static void Move(ReadOnlySpan<char> source, ReadOnlySpan<char> destination, bool overwrite = false)
        {
            if (source.IsEmpty)
                throw new ArgumentException("Source path is empty.", nameof(source));
            if (destination.IsEmpty)
                throw new ArgumentException("Destination path is empty.", nameof(destination));

            const int stackCap = 1024;

            if (OperatingSystem.IsWindows())
            {
                char[]? srcRented = null;
                char[]? dstRented = null;
                Span<char> srcStack = stackalloc char[stackCap];
                Span<char> dstStack = stackalloc char[stackCap];
                try
                {
                    var s = PathBuf.Utf16WithNul(source, srcStack, out srcRented);
                    var d = PathBuf.Utf16WithNul(destination, dstStack, out dstRented);
                    if (Pal.MoveFileEx(s, d, overwrite) != 0)
                        throw new IOException($"Failed to move file, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (srcRented is not null) ArrayPool<char>.Shared.Return(srcRented);
                    if (dstRented is not null) ArrayPool<char>.Shared.Return(dstRented);
                }
            }
            else
            {
                if (!overwrite && Exists(destination))
                    throw new IOException("Destination file already exists.");

                byte[]? srcRented = null;
                byte[]? dstRented = null;
                Span<byte> srcStack = stackalloc byte[stackCap];
                Span<byte> dstStack = stackalloc byte[stackCap];
                try
                {
                    var s = PathBuf.Utf8WithNul(source, srcStack, out srcRented);
                    var d = PathBuf.Utf8WithNul(destination, dstStack, out dstRented);
                    if (Pal.Rename(s, d) != 0)
                        throw new IOException($"Failed to move file, error code: {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    if (srcRented is not null) ArrayPool<byte>.Shared.Return(srcRented);
                    if (dstRented is not null) ArrayPool<byte>.Shared.Return(dstRented);
                }
            }
        }

        public static void Move(ref Utf8StringBuilder source, ref Utf8StringBuilder destination, bool overwrite = false)
        {
            Move(source.WrittenSpan, destination.WrittenSpan, overwrite);
        }
        
        private const int MaxReadPrealloc = 4 * 1024 * 1024;

        /// <summary>一次性读出整个文件的字节内容（UTF-8 路径）。</summary>
        public static byte[] ReadAllBytes(ReadOnlySpan<byte> path)
        {
            using var file = new File();
            file.Open(path, Flags.Read);
            return DrainToArray(file, PreallocFor(path));
        }

        public static byte[] ReadAllBytes(ReadOnlySpan<char> path)
        {
            using var file = new File();
            file.Open(path, Flags.Read);
            return DrainToArray(file, PreallocFor(path));
        }

        public static byte[] ReadAllBytes(ref Utf8StringBuilder path)
        {
            return ReadAllBytes(path.WrittenSpan);
        }

        /// <summary>一次性读出整个文件并按 UTF-8 解码为字符串（UTF-8 路径）。</summary>
        public static string ReadAllText(ReadOnlySpan<byte> path)
        {
            using var file = new File();
            file.Open(path, Flags.Read);
            return DrainToString(file, PreallocFor(path));
        }

        public static string ReadAllText(ReadOnlySpan<char> path)
        {
            using var file = new File();
            file.Open(path, Flags.Read);
            return DrainToString(file, PreallocFor(path));
        }

        public static string ReadAllText(ref Utf8StringBuilder path)
        {
            return ReadAllText(path.WrittenSpan);
        }

        /// <summary>一次性按行读出整个文件（UTF-8 路径），返回各行（不含换行符）。</summary>
        public static string[] ReadAllLines(ReadOnlySpan<byte> path)
        {
            using var file = new File();
            file.Open(path, Flags.Read);
            return DrainToLines(file);
        }

        public static string[] ReadAllLines(ReadOnlySpan<char> path)
        {
            using var file = new File();
            file.Open(path, Flags.Read);
            return DrainToLines(file);
        }

        public static string[] ReadAllLines(ref Utf8StringBuilder path)
        {
            return ReadAllLines(path.WrittenSpan);
        }
        
        // 按文件大小预估初始缓冲容量；查不到大小（IOException）就返回 0 让缓冲自行增长。
        private static int PreallocFor(ReadOnlySpan<byte> path)
        {
            try
            {
                var len = GetFileLength(path);
                return ClampPrealloc(len);
            }
            catch (IOException)
            {
                return 0;
            }
        }

        private static int PreallocFor(ReadOnlySpan<char> path)
        {
            try
            {
                var len = GetFileLength(path);
                return ClampPrealloc(len);
            }
            catch (IOException)
            {
                return 0;
            }
        }

        // 预分配上限封顶在 4 MiB，避免对超大文件一次性预留过多内存。
        private static int ClampPrealloc(long length)
        {
            if (length <= 0)
                return 0;
            return length > MaxReadPrealloc ? MaxReadPrealloc : (int)length;
        }

        private static byte[] DrainToArray(File file, int prealloc)
        {
            var sb = prealloc > 0
                ? Utf8StringBuilder.CreatePooled(prealloc)
                : Utf8StringBuilder.CreatePooled();
            try
            {
                file.ReadToEnd(ref sb);
                return sb.WrittenSpan.ToArray();
            }
            finally
            {
                sb.Dispose();
            }
        }

        private static string DrainToString(File file, int prealloc)
        {
            var sb = prealloc > 0
                ? Utf8StringBuilder.CreatePooled(prealloc)
                : Utf8StringBuilder.CreatePooled();
            try
            {
                file.ReadToEnd(ref sb);
                return sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }
        }

        private static string[] DrainToLines(File file)
        {
            var lines = new List<string>();
            var sb = Utf8StringBuilder.CreatePooled();
            try
            {
                while (true)
                {
                    sb.Clear();
                    if (!file.ReadLine(ref sb))
                        break;

                    lines.Add(Encoding.UTF8.GetString(sb.WrittenSpan));
                }
            }
            finally
            {
                sb.Dispose();
            }

            return lines.ToArray();
        }
        
        // 以下 Win*/Unix* 系列是按平台、按路径编码（UTF-8/UTF-16）分流的底层助手：
        // 把路径补成以 NUL 结尾的缓冲（短路径走栈、长路径走堆），再调对应的属性查询 / access / open+lseek。
        private static bool WinAttributesFromUtf8(ReadOnlySpan<byte> path, out long size)
        {
            const int stackCap = 1024;
            var capacity = path.Length + 1;
            if (capacity <= stackCap)
            {
                Span<char> chars = stackalloc char[stackCap];
                if (Utf8.ToUtf16(path, chars, out _, out var written) != OperationStatus.Done)
                    throw new IOException("Path contains invalid UTF-8.");
                chars[written] = '\0';
                return Pal.GetFileAttributes(chars[..(written + 1)], out size);
            }
            else
            {
                var heap = new char[capacity];
                if (Utf8.ToUtf16(path, heap, out _, out var written) != OperationStatus.Done)
                    throw new IOException("Path contains invalid UTF-8.");
                heap[written] = '\0';
                return Pal.GetFileAttributes(heap.AsSpan(0, written + 1), out size);
            }
        }
        
        private static bool WinAttributesFromUtf16(ReadOnlySpan<char> path, out long size)
        {
            const int stackCap = 1024;
            var capacity = path.Length + 1;
            if (capacity <= stackCap)
            {
                Span<char> chars = stackalloc char[stackCap];
                path.CopyTo(chars);
                chars[path.Length] = '\0';
                return Pal.GetFileAttributes(chars[..(path.Length + 1)], out size);
            }
            else
            {
                var heap = new char[capacity];
                path.CopyTo(heap);
                heap[path.Length] = '\0';
                return Pal.GetFileAttributes(heap.AsSpan(0, path.Length + 1), out size);
            }
        }
        
        private static long UnixSizeFromUtf8(ReadOnlySpan<byte> path)
        {
            const int stackCap = 1024;
            var capacity = path.Length + 1;
            if (capacity <= stackCap)
            {
                Span<byte> bytes = stackalloc byte[stackCap];
                path.CopyTo(bytes);
                bytes[path.Length] = 0;
                return UnixSizeCore(bytes[..(path.Length + 1)]);
            }
            else
            {
                var heap = new byte[capacity];
                path.CopyTo(heap);
                heap[path.Length] = 0;
                return UnixSizeCore(heap.AsSpan());
            }
        }
        
        private static long UnixSizeFromUtf16(ReadOnlySpan<char> path)
        {
            const int stackCap = 1024;
            var capacity = Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
            if (capacity <= stackCap)
            {
                Span<byte> bytes = stackalloc byte[stackCap];
                if (Utf8.FromUtf16(path, bytes, out _, out var written) != OperationStatus.Done)
                    throw new IOException("Path contains invalid UTF-16.");
                bytes[written] = 0;
                return UnixSizeCore(bytes[..(written + 1)]);
            }
            else
            {
                var heap = new byte[capacity];
                if (Utf8.FromUtf16(path, heap, out _, out var written) != OperationStatus.Done)
                    throw new IOException("Path contains invalid UTF-16.");
                heap[written] = 0;
                return UnixSizeCore(heap.AsSpan(0, written + 1));
            }
        }

        // Unix 取大小：以只读打开，lseek 到末尾得到字节数，最后必关闭 fd。
        private static long UnixSizeCore(ReadOnlySpan<byte> pathWithNul)
        {
            var fd = Pal.Open(pathWithNul, Flags.Read);
            if (fd < 0)
                throw new IOException(
                    $"Failed to open file for size query, error code: {Marshal.GetLastPInvokeError()}");

            try
            {
                var size = Pal.LSeekEnd(fd);
                if (size < 0)
                    throw new IOException(
                        $"Failed to determine file size, error code: {Marshal.GetLastPInvokeError()}");
                return size;
            }
            finally
            {
                Pal.Close(fd);
            }
        }
    }
}
