// Lukas/Interop/Unix/System.Native/OpenFlags.cs

using System;

namespace FileTransfer;

// open(2) 的标志位。各 Unix 平台的 O_* 数值并不一致（尤其 Apple/FreeBSD 与 Linux 差异较大），
// 所以这里先用一套与平台无关的枚举表达意图，再由 ResolveOpenFlags 翻译成当前平台的真实数值。
internal static partial class Sys
{
    [Flags]
    internal enum OpenFlags
    {
        ORdonly = 1 << 0,
        OWronly = 1 << 1,
        ORdwr = 1 << 2,
        OCreat = 1 << 3,
        OExcl = 1 << 4,
        OTrunc = 1 << 5,
        OAppend = 1 << 6,
        ONofollow = 1 << 7,
        OCloexec = 1 << 8,
        OSync = 1 << 9
    }

    private static readonly bool IsApple =
        OperatingSystem.IsMacOS()
        || OperatingSystem.IsMacCatalyst()
        || OperatingSystem.IsIOS()
        || OperatingSystem.IsTvOS();

    private static readonly bool IsFreeBsd =
        OperatingSystem.IsFreeBSD();

    private const int LinuxORdonly = 0x0000;
    private const int LinuxOWronly = 0x0001;
    private const int LinuxORdwr = 0x0002;
    private const int LinuxOCreat = 0x0040;
    private const int LinuxOExcl = 0x0080;
    private const int LinuxOTrunc = 0x0200;
    private const int LinuxOAppend = 0x0400;
    private const int LinuxONofollow = 0x20000;
    private const int LinuxOCloexec = 0x80000;
    private const int LinuxOSync = 0x101000;
    
    private const int AppleORdonly = 0x0000;
    private const int AppleOWronly = 0x0001;
    private const int AppleORdwr = 0x0002;
    private const int AppleOCreat = 0x0200;
    private const int AppleOExcl = 0x0800;
    private const int AppleOTrunc = 0x0400;
    private const int AppleOAppend = 0x0008;
    private const int AppleONofollow = 0x0100;
    private const int AppleOCloexec = 0x1000000;
    private const int AppleOSync = 0x0080;
    
    private const int FreeBsdOWronly = 0x0001;
    private const int FreeBsdORdwr = 0x0002;
    private const int FreeBsdOCreat = 0x0200;
    private const int FreeBsdOExcl = 0x0800;
    private const int FreeBsdOTrunc = 0x0400;
    private const int FreeBsdOAppend = 0x0008;
    private const int FreeBsdONofollow = 0x0100;
    private const int FreeBsdOCloexec = 0x00100000;
    private const int FreeBsdOSync = 0x0080;

    /// <summary>把平台无关的 <see cref="OpenFlags"/> 翻译成当前运行平台真实的 open() 标志数值。</summary>
    internal static int ResolveOpenFlags(OpenFlags flags)
    {
        var result = 0;

        if (IsApple)
        {
            if ((flags & OpenFlags.OWronly) != 0) result |= AppleOWronly;
            if ((flags & OpenFlags.ORdwr) != 0) result |= AppleORdwr;
            if ((flags & OpenFlags.OCreat) != 0) result |= AppleOCreat;
            if ((flags & OpenFlags.OExcl) != 0) result |= AppleOExcl;
            if ((flags & OpenFlags.OTrunc) != 0) result |= AppleOTrunc;
            if ((flags & OpenFlags.OAppend) != 0) result |= AppleOAppend;
            if ((flags & OpenFlags.ONofollow) != 0) result |= AppleONofollow;
            if ((flags & OpenFlags.OCloexec) != 0) result |= AppleOCloexec;
            if ((flags & OpenFlags.OSync) != 0) result |= AppleOSync;
        }
        else if (IsFreeBsd)
        {
            if ((flags & OpenFlags.OWronly) != 0) result |= FreeBsdOWronly;
            if ((flags & OpenFlags.ORdwr) != 0) result |= FreeBsdORdwr;
            if ((flags & OpenFlags.OCreat) != 0) result |= FreeBsdOCreat;
            if ((flags & OpenFlags.OExcl) != 0) result |= FreeBsdOExcl;
            if ((flags & OpenFlags.OTrunc) != 0) result |= FreeBsdOTrunc;
            if ((flags & OpenFlags.OAppend) != 0) result |= FreeBsdOAppend;
            if ((flags & OpenFlags.ONofollow) != 0) result |= FreeBsdONofollow;
            if ((flags & OpenFlags.OCloexec) != 0) result |= FreeBsdOCloexec;
            if ((flags & OpenFlags.OSync) != 0) result |= FreeBsdOSync;
        }
        else
        {
            if ((flags & OpenFlags.OWronly) != 0) result |= LinuxOWronly;
            if ((flags & OpenFlags.ORdwr) != 0) result |= LinuxORdwr;
            if ((flags & OpenFlags.OCreat) != 0) result |= LinuxOCreat;
            if ((flags & OpenFlags.OExcl) != 0) result |= LinuxOExcl;
            if ((flags & OpenFlags.OTrunc) != 0) result |= LinuxOTrunc;
            if ((flags & OpenFlags.OAppend) != 0) result |= LinuxOAppend;
            if ((flags & OpenFlags.ONofollow) != 0) result |= LinuxONofollow;
            if ((flags & OpenFlags.OCloexec) != 0) result |= LinuxOCloexec;
            if ((flags & OpenFlags.OSync) != 0) result |= LinuxOSync;
        }

        return result;
    }
}
