// Lukas/Interop/Unix/System.Native/Stat.cs
//
// 纯 C# 的文件类型探测，替换原先依赖 liblukasbasecpp 的 std_path_kind C shim。
// 关键：避免直接传 struct stat（其布局随平台/架构而异）——
//   * Linux 用 statx(2)：struct statx 布局在所有架构上稳定，stx_mode 固定在偏移 28（u16）。
//   * macOS 用 stat$INODE64 / lstat$INODE64：64 位 inode 的 struct stat 在 x86_64/arm64 上一致，
//     st_mode 固定在偏移 4（u16）。
// 仅读取 st_mode 的类型位，不跨边界传结构体，彻底回避布局问题。

using System;
using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.System.Native;

internal static unsafe partial class Sys
{
    // 路径类型:0=不存在/出错 1=普通文件 2=目录 3=其它。
    internal const int PathKindNotFound = 0;
    internal const int PathKindFile = 1;
    internal const int PathKindDir = 2;
    internal const int PathKindOther = 3;

    // st_mode 类型位（Linux 与 macOS 一致）。
    private const int SIfmt = 0xF000;
    private const int SIfreg = 0x8000;
    private const int SIfdir = 0x4000;

    // statx 相关常量（Linux）。
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const int StatxStructSize = 256;     // sizeof(struct statx)
    private const int StatxModeOffset = 28;      // stx_mode（u16）在 struct statx 中的偏移
    private const int MacStatStructSize = 256;   // > sizeof(struct stat)，留足余量
    private const int MacStatModeOffset = 4;     // st_mode（u16）在 macOS struct stat 中的偏移

    // ---- Linux: statx ----
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatxRaw(int dirfd, byte* path, int flags, uint mask, byte* statxbuf);

    // ---- macOS: stat / lstat（64 位 inode 变体）----
    [LibraryImport("libSystem", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static partial int StatMac(byte* path, byte* buf);

    [LibraryImport("libSystem", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static partial int LStatMac(byte* path, byte* buf);

    /// <summary>跟随符号链接（stat 语义）。返回 PathKind* 常量。</summary>
    internal static int PathKind(byte* path) => PathKindCore(path, follow: true);

    /// <summary>不跟随符号链接（lstat 语义；链接本身归为「其它」）。返回 PathKind* 常量。</summary>
    internal static int PathKindNoFollow(byte* path) => PathKindCore(path, follow: false);

    private static int PathKindCore(byte* path, bool follow)
    {
        if (path is null)
            return PathKindNotFound;

        int mode;

        if (OperatingSystem.IsMacOS())
        {
            var buf = stackalloc byte[MacStatStructSize];
            var rc = follow ? StatMac(path, buf) : LStatMac(path, buf);
            if (rc != 0)
                return PathKindNotFound;
            mode = *(ushort*)(buf + MacStatModeOffset);
        }
        else
        {
            var buf = stackalloc byte[StatxStructSize];
            var flags = follow ? 0 : AtSymlinkNoFollow;
            var rc = StatxRaw(AtFdCwd, path, flags, StatxType, buf);
            if (rc != 0)
                return PathKindNotFound;
            mode = *(ushort*)(buf + StatxModeOffset);
        }

        var type = mode & SIfmt;
        return type switch
        {
            SIfreg => PathKindFile,
            SIfdir => PathKindDir,
            _ => PathKindOther
        };
    }
}