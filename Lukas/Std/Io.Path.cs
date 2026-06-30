// Lukas/Io.Path.cs

using System;
using Lukas.Str;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 纯托管的路径字符串处理（不触碰文件系统）：取目录名、拼接路径段。
    /// 同时支持 UTF-8 字节、UTF-16 字符两种形式；分隔符按平台选择（Windows 用 '\'，Unix 用 '/'）。
    /// </summary>
    public static class Path
    {
        /// <summary>取路径的目录部分：无分隔符返回空；根 "/" 返回 "/"；Windows 盘符根 "C:\" 保留盘符。</summary>
        public static ReadOnlySpan<byte> GetDirectoryName(ReadOnlySpan<byte> path)
        {
            var isWindows = OperatingSystem.IsWindows();
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
                          && IsAsciiLetterByte(path[0]))
            {
                return path[..3];
            }

            return path[..lastSep];
        }

        public static string GetDirectoryName(string path)
        {
            return GetDirectoryName(path.AsSpan()).ToString();
        }

        public static ReadOnlySpan<char> GetDirectoryName(ReadOnlySpan<char> path)
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
                          && IsAsciiLetterByte((byte)path[0]))
            {
                return path[..3];
            }

            return path[..lastSep];
        }

        public static ReadOnlySpan<byte> GetDirectoryName(ref Utf8StringBuilder path)
            => GetDirectoryName(path.WrittenSpan);

        /// <summary>拼接两段路径（UTF-8）。第二段绝对则直接返回第二段；否则按需补一个分隔符。</summary>
        public static byte[] Combine(ReadOnlySpan<byte> path1, ReadOnlySpan<byte> path2)
        {
            if (path1.IsEmpty) return path2.ToArray();
            if (path2.IsEmpty) return path1.ToArray();
            if (IsRootedByte(path2)) return path2.ToArray();

            var sep = OperatingSystem.IsWindows() ? (byte)'\\' : (byte)'/';
            var needSep = !EndsWithSepByte(path1);

            var total = path1.Length + (needSep ? 1 : 0) + path2.Length;
            var result = new byte[total];
            var dst = result.AsSpan();
            path1.CopyTo(dst);
            var pos = path1.Length;
            if (needSep) dst[pos++] = sep;
            path2.CopyTo(dst[pos..]);
            return result;
        }
        
        public static string Combine(string path1, string path2) => Combine(path1.AsSpan(), path2.AsSpan());

        /// <summary>拼接两段路径（UTF-16）。第二段绝对则直接返回第二段；否则按需补一个分隔符。</summary>
        public static string Combine(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2)
        {
            if (path1.IsEmpty) return path2.ToString();
            if (path2.IsEmpty) return path1.ToString();
            if (IsRootedChar(path2)) return path2.ToString();

            var sep = OperatingSystem.IsWindows() ? '\\' : '/';
            var needSep = !EndsWithSepChar(path1);
            var total = path1.Length + (needSep ? 1 : 0) + path2.Length;

            Span<char> buf = total <= 1024 ? stackalloc char[1024] : new char[total];
            path1.CopyTo(buf);
            var pos = path1.Length;
            if (needSep) buf[pos++] = sep;
            path2.CopyTo(buf[pos..]);
            return new string(buf[..total]);
        }

        /// <summary>拼接三段路径（UTF-16）便捷重载。</summary>
        public static string Combine(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2, ReadOnlySpan<char> path3)
            => Combine(Combine(path1, path2).AsSpan(), path3);

        private static bool IsRootedByte(ReadOnlySpan<byte> p)
        {
            if (p.IsEmpty) return false;
            if (p[0] == (byte)'/') return true;
            if (OperatingSystem.IsWindows())
            {
                if (p[0] == (byte)'\\') return true;
                if (p.Length >= 2 && p[1] == (byte)':' && IsAsciiLetterByte(p[0])) return true;
            }
            return false;
        }

        private static bool IsRootedChar(ReadOnlySpan<char> p)
        {
            if (p.IsEmpty) return false;
            if (p[0] == '/') return true;
            if (OperatingSystem.IsWindows())
            {
                if (p[0] == '\\') return true;
                if (p.Length >= 2 && p[1] == ':' && IsAsciiLetterByte((byte)p[0])) return true;
            }
            return false;
        }
        
        /// <summary>
        /// 取路径的扩展名（含点，如 ".txt"）。语义对齐 .NET：从末尾找 '.'，遇到分隔符或找不到则返回空；
        /// 当 '.' 位于文件名首字符（如 ".gitignore"）时视为无扩展名，返回空。
        /// </summary>
        public static ReadOnlySpan<byte> GetExtension(ReadOnlySpan<byte> path)
        {
            var isWindows = OperatingSystem.IsWindows();
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var b = path[i];
                if (b == (byte)'.')
                {
                    if (i == 0 || IsSeparatorByte(path[i - 1], isWindows))
                        return ReadOnlySpan<byte>.Empty;
                    return path[i..];
                }

                if (IsSeparatorByte(b, isWindows))
                    return ReadOnlySpan<byte>.Empty;
            }

            return ReadOnlySpan<byte>.Empty;
        }
        
        public static string GetExtension(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
    
            var isWindows = OperatingSystem.IsWindows();
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var c = path[i];
                if (c == '.')
                {
                    if (i == 0 || IsSeparatorChar(path[i - 1], isWindows))
                        return string.Empty;
                    return path[i..];
                }

                if (IsSeparatorChar(c, isWindows))
                    return string.Empty;
            }

            return string.Empty;
        }

        public static ReadOnlySpan<char> GetExtension(ReadOnlySpan<char> path)
        {
            var isWindows = OperatingSystem.IsWindows();
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var c = path[i];
                if (c == '.')
                {
                    if (i == 0 || IsSeparatorChar(path[i - 1], isWindows))
                        return ReadOnlySpan<char>.Empty;
                    return path[i..];
                }

                if (IsSeparatorChar(c, isWindows))
                    return ReadOnlySpan<char>.Empty;
            }

            return ReadOnlySpan<char>.Empty;
        }
        
        public static ReadOnlySpan<byte> GetExtension(ref Utf8StringBuilder path)
            => GetExtension(path.WrittenSpan);

        private static bool IsSeparatorByte(byte b, bool isWindows)
            => b == (byte)'/' || (isWindows && (b == (byte)'\\' || b == (byte)':'));

        private static bool IsSeparatorChar(char c, bool isWindows)
            => c == '/' || (isWindows && (c == '\\' || c == ':'));

        private static bool EndsWithSepByte(ReadOnlySpan<byte> p)
        {
            var last = p[^1];
            return last == (byte)'/' || (OperatingSystem.IsWindows() && last == (byte)'\\');
        }

        private static bool EndsWithSepChar(ReadOnlySpan<char> p)
        {
            var last = p[^1];
            return last == '/' || (OperatingSystem.IsWindows() && last == '\\');
        }

        private static bool IsAsciiLetterByte(byte b)
            => (uint)((b | 0x20) - (byte)'a') <= 'z' - 'a';
    }
}
