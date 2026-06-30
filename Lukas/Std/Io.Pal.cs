// Lukas/Io.Pal.cs

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lukas.Interop.Unix.System.Native;
using Lukas.Interop.Windows.Kernel32;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 平台适配层（PAL）：把上层用到的文件/目录操作收敛成一组方法，内部按操作系统分派到
    /// Unix 系统调用（<c>Sys.*</c>）或 Windows 内核接口（<c>Kernel32.*</c>）。
    /// 标 <c>[SupportedOSPlatform]</c>/<c>[UnsupportedOSPlatform]</c> 的成员只在对应平台调用。
    /// 句柄统一用 <see cref="nint"/> 表示：Windows 上是 HANDLE，Unix 上是文件描述符。
    /// </summary>
    public static unsafe class Pal
    {
        // 三个标准流的句柄。Windows 经 GetStdHandle 获取，Unix 直接用 0/1/2 三个 fd。
        private static readonly int StdinFd = OperatingSystem.IsWindows() ? -10 : 0;
        internal static readonly nint StdInHandle = OperatingSystem.IsWindows()
            ? Kernel32.GetStdHandle(StdinFd)
            : StdinFd;

        private static readonly int StdoutFd = OperatingSystem.IsWindows() ? -11 : 1;
        internal static readonly nint StdOutHandle = OperatingSystem.IsWindows()
            ? Kernel32.GetStdHandle(StdoutFd)
            : StdoutFd;

        private static readonly int StderrFd = OperatingSystem.IsWindows() ? -12 : 2;
        internal static readonly nint StdErrHandle = OperatingSystem.IsWindows()
            ? Kernel32.GetStdHandle(StderrFd)
            : StderrFd;

        /// <summary>
        /// 【Unix】按 <see cref="Flags"/> 打开文件，返回文件描述符；失败返回 -1。
        /// 各种打开方式会映射成对应的 O_* 组合，并一律带上 O_CLOEXEC。
        /// <paramref name="permission"/> 默认 0o644。
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        internal static int Open(
            ReadOnlySpan<byte> filename,
            Flags palFlags = Flags.Append,
            int permission = 0x1A4)
        {
            var flags = palFlags switch
            {
                Flags.CreateNew
                    => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OExcl | Sys.OpenFlags.OCloexec,

                Flags.Create
                    => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OTrunc | Sys.OpenFlags.OCloexec,

                Flags.Open
                    => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCloexec,

                Flags.OpenOrCreate
                    => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OCloexec,

                Flags.Truncate
                    => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OTrunc | Sys.OpenFlags.OCloexec,

                Flags.Append
                    => Sys.OpenFlags.OWronly | Sys.OpenFlags.OCreat | Sys.OpenFlags.OAppend | Sys.OpenFlags.OCloexec,

                Flags.Read
                    => Sys.OpenFlags.ORdonly | Sys.OpenFlags.OCloexec,

                _ => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OCloexec
            };
            fixed (byte* bytes = filename)
            {
                var unixHandle = Sys.Open(bytes, Sys.ResolveOpenFlags(flags), permission);

                if (unixHandle == -1)
                    return -1;

                return unixHandle;
            }
        }

        /// <summary>【Windows】按 <see cref="Flags"/> 打开/创建文件，返回句柄；失败返回 -1。</summary>
        [SupportedOSPlatform("windows")]
        internal static nint CreateFile(ReadOnlySpan<char> filename, Flags palFlags = Flags.Append)
        {
            var fileMode = palFlags switch
            {
                Flags.CreateNew => FileMode.CreateNew,

                Flags.Create => FileMode.Create,

                Flags.Open => FileMode.Open,

                Flags.OpenOrCreate => FileMode.OpenOrCreate,

                Flags.Truncate => FileMode.Truncate,

                // Win32 的 dwCreationDisposition 只接受 1–5，没有“追加”这种处置——
                // 追加靠 FILE_APPEND_DATA 访问权表达。这里用 OpenOrCreate(OPEN_ALWAYS)：不存在则建、存在则打开。
                Flags.Append => FileMode.OpenOrCreate,

                Flags.Read => FileMode.Open,

                _ => FileMode.OpenOrCreate
            };

            var genericAccessRights = palFlags switch
            {
                Flags.Read => GenericAccessRights.FileReadData,
                Flags.Append => GenericAccessRights.FileAppendData,
                _ => GenericAccessRights.FileReadData | GenericAccessRights.FileWriteData
            };

            var winHandle = Kernel32.CreateFile(
                filename,
                genericAccessRights,
                FileShare.Read | FileShare.Write,
                fileMode,
                FileFlagsAndAttributes.FileAttributeNormal);

            if (winHandle == -1)
                return -1;

            return winHandle;
        }

        /// <summary>
        /// 【Windows】打开文件并可选地以重叠（异步）模式创建句柄。
        /// <paramref name="overlapped"/> 为真时附加 FILE_FLAG_OVERLAPPED，供 IOCP 使用。
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static nint CreateFile(ReadOnlySpan<char> filename, Flags palFlags, bool overlapped)
        {
            var fileMode = palFlags switch
            {
                Flags.CreateNew => FileMode.CreateNew,
                Flags.Create => FileMode.Create,
                Flags.Open => FileMode.Open,
                Flags.OpenOrCreate => FileMode.OpenOrCreate,
                Flags.Truncate => FileMode.Truncate,
                // 同上：Win32 没有“追加”这种创建处置，用 OpenOrCreate(OPEN_ALWAYS)；
                // 重叠写下的追加位置由 IocpEngine 的逻辑游标（开句柄时按文件大小定位到末尾）负责。
                Flags.Append => FileMode.OpenOrCreate,
                Flags.Read => FileMode.Open,
                _ => FileMode.OpenOrCreate
            };

            var genericAccessRights = palFlags switch
            {
                Flags.Read => GenericAccessRights.GenericRead,
                _ => GenericAccessRights.GenericRead | GenericAccessRights.GenericWrite
            };

            var flagsAndAttrs = FileFlagsAndAttributes.FileAttributeNormal;
            if (overlapped)
                flagsAndAttrs |= FileFlagsAndAttributes.FileFlagOverlapped;

            var winHandle = Kernel32.CreateFile(
                filename,
                genericAccessRights,
                FileShare.Read | FileShare.Write,
                fileMode,
                flagsAndAttrs);

            return winHandle;
        }

        [UnsupportedOSPlatform("windows")]
        internal static int MkDir(ReadOnlySpan<byte> path, int permission = 0x1ED)
        {
            fixed (byte* buffer = path)
            {
                return Sys.MkDir(buffer, permission);
            }
        }

        [SupportedOSPlatform("windows")]
        internal static int CreateDirectory(ReadOnlySpan<char> path, Kernel32.SecurityAttributes* lpSecurityAttributes = null)
        {
            if (Kernel32.CreateDirectory(path, lpSecurityAttributes))
                return 0;

            return -1;
        }

        [UnsupportedOSPlatform("windows")]
        internal static int Unlink(ReadOnlySpan<byte> path)
        {
            fixed (byte* buffer = path)
            {
                return Sys.Unlink(buffer);
            }
        }

        [SupportedOSPlatform("windows")]
        internal static int DeleteFile(ReadOnlySpan<char> path)
        {
            if (Kernel32.DeleteFile(path))
                return 0;

            return -1;
        }

        [UnsupportedOSPlatform("windows")]
        internal static int Close(nint handle)
        {
            return Sys.Close(handle);
        }

        [SupportedOSPlatform("windows")]
        internal static int CloseHandle(nint handle)
        {
            if (Kernel32.CloseHandle(handle))
                return 0;

            return -1;
        }

        [UnsupportedOSPlatform("windows")]
        internal static int Read(nint fd, byte* buffer, int count)
        {
            return Sys.Read(fd, buffer, count);
        }

        [SupportedOSPlatform("windows")]
        internal static int ReadFile(nint handle, byte* bytes, int numberBytesToWrite)
        {
            if (Kernel32.ReadFile(handle, bytes, numberBytesToWrite, out var numBytesWritten, nint.Zero) == 0)
                return -1;

            return numBytesWritten;
        }

        [UnsupportedOSPlatform("windows")]
        internal static int Write(nint fd, byte* buffer, int count)
        {
            return Sys.Write(fd, buffer, count);
        }

        [SupportedOSPlatform("windows")]
        internal static int WriteFile(nint handle, byte* bytes, int numberBytesToWrite)
        {
            if (Kernel32.WriteFile(handle, bytes, numberBytesToWrite, out var bytesWritten, nint.Zero) == 0)
                return -1;

            return bytesWritten;
        }

        /// <summary>
        /// 【Windows】带偏移的同步读：偏移 &lt; 0 时退化为从当前位置读；否则用 OVERLAPPED 指定偏移。
        /// 读到文件尾返回 0，失败返回 -1。
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static int ReadFileAt(nint handle, byte* bytes, int count, long offset)
        {
            if (offset < 0)
                return ReadFile(handle, bytes, count);

            var ov = new NativeOverlapped();
            ov.SetOffset(offset);
            int read;
            if (Kernel32.ReadFileOverlapped(handle, bytes, count, &read, &ov) == 0)
            {
                var err = Marshal.GetLastPInvokeError();
                if (err == ErrorHandleEof)
                    return 0;
                return -1;
            }
            return read;
        }

        /// <summary>【Windows】带偏移的同步写：偏移 &lt; 0 时退化为从当前位置写；否则用 OVERLAPPED 指定偏移。</summary>
        [SupportedOSPlatform("windows")]
        internal static int WriteFileAt(nint handle, byte* bytes, int count, long offset)
        {
            if (offset < 0)
                return WriteFile(handle, bytes, count);

            var ov = new NativeOverlapped();
            ov.SetOffset(offset);
            int written;
            if (Kernel32.WriteFileOverlapped(handle, bytes, count, &written, &ov) == 0)
                return -1;
            return written;
        }

        /// <summary>【Unix】路径是否存在（access F_OK）。</summary>
        [UnsupportedOSPlatform("windows")]
        internal static bool Access(ReadOnlySpan<byte> pathWithNul)
        {
            fixed (byte* p = pathWithNul)
            {
                return Sys.Access(p, Sys.FOk) == 0;
            }
        }

        /// <summary>【Unix】把文件偏移定位到末尾，返回值即文件大小。</summary>
        [UnsupportedOSPlatform("windows")]
        internal static long LSeekEnd(nint fd)
        {
            return Sys.LSeek(fd, 0, Sys.SeekEnd);
        }

        /// <summary>【Windows】查询文件大小（同时间接判断存在性），失败返回 <see langword="false"/> 且 size 置 -1。</summary>
        [SupportedOSPlatform("windows")]
        internal static bool GetFileAttributes(ReadOnlySpan<char> pathWithNul, out long size)
        {
            if (!Kernel32.GetFileAttributesEx(pathWithNul, out var data))
            {
                size = -1;
                return false;
            }

            size = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
            return true;
        }

        /// <summary>【Windows】路径是否存在且为目录。</summary>
        [SupportedOSPlatform("windows")]
        internal static bool DirectoryExists(ReadOnlySpan<char> pathWithNul)
        {
            return Kernel32.GetFileAttributesEx(pathWithNul, out var data)
                   && (data.FileAttributes & FileAttributeDirectory) != 0;
        }

        /// <summary>
        /// 【Unix】路径是否存在且为目录。
        /// 经 C shim（pal_stat）的 std_path_kind 判类型，跟随符号链接：指向目录的链接也算目录。
        /// 不再用纯 C# P/Invoke stat（其 struct 布局与本机 struct stat 不一致会段错误）。
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        internal static bool DirectoryExists(ReadOnlySpan<byte> pathWithNul)
        {
            fixed (byte* p = pathWithNul)
            {
                return Sys.PathKind(p) == Sys.PathKindDir;
            }
        }

        /// <summary>【Windows】路径是否存在且“不是目录”（即视为文件，语义同 .NET File.Exists）。</summary>
        [SupportedOSPlatform("windows")]
        internal static bool FileExists(ReadOnlySpan<char> pathWithNul)
        {
            return Kernel32.GetFileAttributesEx(pathWithNul, out var data)
                   && (data.FileAttributes & FileAttributeDirectory) == 0;
        }

        /// <summary>
        /// 【Unix】路径是否存在且“不是目录”（语义同 .NET File.Exists）。
        /// 经 C shim 判类型，跟随符号链接：普通文件或其它非目录类型（设备/FIFO/socket 等）都算“文件”。
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        internal static bool FileExists(ReadOnlySpan<byte> pathWithNul)
        {
            fixed (byte* p = pathWithNul)
            {
                var kind = Sys.PathKind(p);
                return kind == Sys.PathKindFile || kind == Sys.PathKindOther;
            }
        }

        /// <summary>【Unix】rename(2)：成功返回 0，失败返回 -1（错误码经 GetLastPInvokeError 获取）。</summary>
        [UnsupportedOSPlatform("windows")]
        internal static int Rename(ReadOnlySpan<byte> oldPathWithNul, ReadOnlySpan<byte> newPathWithNul)
        {
            fixed (byte* o = oldPathWithNul)
            fixed (byte* n = newPathWithNul)
            {
                return Sys.Rename(o, n);
            }
        }

        /// <summary>【Unix】rmdir(2)：删除空目录，成功返回 0，失败返回 -1。</summary>
        [UnsupportedOSPlatform("windows")]
        internal static int RmDir(ReadOnlySpan<byte> pathWithNul)
        {
            fixed (byte* p = pathWithNul)
            {
                return Sys.RmDir(p);
            }
        }

        /// <summary>【Windows】MoveFileExW：成功返回 0，失败返回 -1。允许跨卷；<paramref name="replaceExisting"/> 控制是否覆盖。</summary>
        [SupportedOSPlatform("windows")]
        internal static int MoveFileEx(ReadOnlySpan<char> existingWithNul, ReadOnlySpan<char> newWithNul, bool replaceExisting)
        {
            var flags = Kernel32.MoveFileCopyAllowed | (replaceExisting ? Kernel32.MoveFileReplaceExisting : 0);
            return Kernel32.MoveFileEx(existingWithNul, newWithNul, flags) ? 0 : -1;
        }

        /// <summary>【Windows】RemoveDirectoryW：删除空目录，成功返回 0，失败返回 -1。</summary>
        [SupportedOSPlatform("windows")]
        internal static int RemoveDirectory(ReadOnlySpan<char> pathWithNul)
        {
            return Kernel32.RemoveDirectory(pathWithNul) ? 0 : -1;
        }

        // ---------- 目录遍历（递归删除用） ----------

        /// <summary>【Unix】opendir：成功返回 DIR* 句柄，失败返回 0(NULL)。</summary>
        [UnsupportedOSPlatform("windows")]
        internal static nint OpenDir(ReadOnlySpan<byte> pathWithNul)
        {
            fixed (byte* p = pathWithNul)
            {
                return Sys.OpenDir(p);
            }
        }

        /// <summary>
        /// 【Unix】readdir 读取下一个目录项：把项名拷进 <paramref name="nameBuffer"/> 并给出类型。
        /// 读到末尾返回 <see langword="false"/>。<paramref name="entryType"/> 为 DT_*（DtUnknown 时上层需 lstat 兜底）。
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        internal static bool ReadDir(nint dir, Span<byte> nameBuffer, out int nameLength, out int entryType)
        {
            nameLength = 0;
            entryType = Sys.DtUnknown;

            var entry = Sys.ReadDir(dir);
            if (entry is null)
                return false;

            entryType = *(entry + Sys.DirentDTypeOffset);

            var namePtr = entry + Sys.DirentDNameOffset;
            var i = 0;
            while (i < nameBuffer.Length && namePtr[i] != 0)
            {
                nameBuffer[i] = namePtr[i];
                i++;
            }
            nameLength = i;
            return true;
        }

        [UnsupportedOSPlatform("windows")]
        internal static int CloseDir(nint dir) => Sys.CloseDir(dir);

        /// <summary>
        /// 【Unix】判类型（不跟随符号链接）：返回是否存在，并给出 isDir/isLink。
        /// 经 C shim（pal_stat）的 std_path_kind_nofollow 判类型——符号链接本身不被跟随，
        /// 归入“其它”类型（即 isLink=true），用于列目录/打包时识别并跳过链接，防止借链接逃出用户目录。
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        internal static bool TryGetUnixType(ReadOnlySpan<byte> pathWithNul, out bool isDir, out bool isLink)
        {
            isDir = false;
            isLink = false;
            fixed (byte* p = pathWithNul)
            {
                var kind = Sys.PathKindNoFollow(p);
                if (kind == Sys.PathKindNotFound)
                    return false;
                isDir = kind == Sys.PathKindDir;
                // nofollow 下，符号链接与其它特殊类型（设备/FIFO/socket）都归为 PathKindOther；
                // 对“跳过非常规项”的用途而言，统一当作需跳过的链接/特殊项处理。
                isLink = kind == Sys.PathKindOther;
                return true;
            }
        }

        /// <summary>【Windows】FindFirstFileW：成功返回查找句柄并填首项名/属性；失败返回 -1。</summary>
        [SupportedOSPlatform("windows")]
        internal static nint FindFirst(ReadOnlySpan<char> patternWithNul, Span<char> nameBuffer, out int nameLength, out int attributes)
        {
            var handle = Kernel32.FindFirstFile(patternWithNul, out var data);
            if (handle == -1)
            {
                nameLength = 0;
                attributes = 0;
                return -1;
            }

            CopyFindName(ref data, nameBuffer, out nameLength);
            attributes = data.DwFileAttributes;
            return handle;
        }

        /// <summary>【Windows】FindNextFileW：还有项返回 <see langword="true"/> 并填项名/属性。</summary>
        [SupportedOSPlatform("windows")]
        internal static bool FindNext(nint handle, Span<char> nameBuffer, out int nameLength, out int attributes)
        {
            if (!Kernel32.FindNextFile(handle, out var data))
            {
                nameLength = 0;
                attributes = 0;
                return false;
            }

            CopyFindName(ref data, nameBuffer, out nameLength);
            attributes = data.DwFileAttributes;
            return true;
        }

        [SupportedOSPlatform("windows")]
        internal static void FindClose(nint handle) => Kernel32.FindClose(handle);

        [SupportedOSPlatform("windows")]
        private static void CopyFindName(ref Kernel32.Win32FindDataW data, Span<char> nameBuffer, out int nameLength)
        {
            var i = 0;
            while (i < nameBuffer.Length && data.FileName[i] != '\0')
            {
                nameBuffer[i] = data.FileName[i];
                i++;
            }
            nameLength = i;
        }

        internal const int FileAttributeDirectory = 0x10;
        internal const int FileAttributeReparsePoint = 0x400;

        private const int ErrorHandleEof = 38;
    }
}
