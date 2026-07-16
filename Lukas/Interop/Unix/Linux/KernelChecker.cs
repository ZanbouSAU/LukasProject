// Lukas/Interop/Unix/Linux/KernelChecker.cs

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Lukas.Interop.Unix.Linux;

// uname(2) 返回的 utsname 结构里每个字段是定长字符数组，这里用 InlineArray 表达 65 字节的定长缓冲。
[InlineArray(65)]
internal struct UtsField
{
    private byte _element0;
}

/// <summary>对应 <c>uname(2)</c> 的 <c>struct utsname</c>，各字段为以 NUL 结尾的 C 字符串。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UtsName
{
    internal UtsField SysName;
    internal UtsField NodeName;
    internal UtsField Release;
    internal UtsField Version;
    internal UtsField Machine;
    internal UtsField DomainName;
}

// 探测内核版本。io_uring 的若干特性要求较新的内核，这里据此决定能否启用 IoUringEngine。
internal static partial class KernelChecker
{
    [LibraryImport("libc", EntryPoint = "uname", SetLastError = true)]
    private static partial int Uname(out UtsName buf);

    /// <summary>当前是否为 Linux 且内核版本 ≥ 6.2（本库选用 io_uring 引擎的下限）。</summary>
    internal static bool IsKernelVersionAtLeast62()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        if (Uname(out var uts) == 0)
        {
            var release = ReadCString(uts.Release);

            var parts = release.Split('.');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var major) &&
                int.TryParse(parts[1], out var minor))
            {
                return major > 6 || (major == 6 && minor >= 2);
            }
        }

        return false;
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var nul = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? bytes[..nul] : bytes);
    }
}
