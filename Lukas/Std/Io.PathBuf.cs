// Lukas/Io.PathBuf.cs

using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Unicode;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 路径缓冲辅助：把 UTF-8/UTF-16 路径转成「以 NUL 结尾」的平台原生缓冲，供 PAL 直接传给系统调用。
    /// 优先用调用方提供的栈缓冲；不够时从 <see cref="ArrayPool{T}"/> 租借，并通过 <c>rented</c> 出参回传，
    /// 由调用方在 <c>finally</c> 中归还。编码非法时本方法会先归还自己租借的数组再抛出，调用方的
    /// <c>rented</c>（须初始化为 <see langword="null"/>）不会被写回，避免重复归还。
    /// </summary>
    internal static class PathBuf
    {
        /// <summary>UTF-8 路径 → 以 NUL 结尾的 UTF-8 字节缓冲（仅追加一个 0）。</summary>
        internal static ReadOnlySpan<byte> Utf8WithNul(ReadOnlySpan<byte> path, Span<byte> stack, out byte[]? rented)
        {
            rented = null;
            var need = path.Length + 1;
            var dst = need <= stack.Length ? stack : (rented = ArrayPool<byte>.Shared.Rent(need));
            path.CopyTo(dst);
            dst[path.Length] = 0;
            return dst[..need];
        }

        /// <summary>UTF-16 路径 → 以 NUL 结尾的 UTF-8 字节缓冲（转码 + 追加 0）。</summary>
        internal static ReadOnlySpan<byte> Utf8WithNul(ReadOnlySpan<char> path, Span<byte> stack, out byte[]? rented)
        {
            rented = null;
            var need = Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
            var dst = need <= stack.Length ? stack : (rented = ArrayPool<byte>.Shared.Rent(need));
            if (Utf8.FromUtf16(path, dst, out _, out var written) != OperationStatus.Done)
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
                rented = null;
                throw new IOException("Path contains invalid UTF-16.");
            }
            dst[written] = 0;
            return dst[..(written + 1)];
        }

        /// <summary>UTF-16 路径 → 以 NUL 结尾的 UTF-16 字符缓冲（拷贝 + 追加 '\0'）。</summary>
        internal static ReadOnlySpan<char> Utf16WithNul(ReadOnlySpan<char> path, Span<char> stack, out char[]? rented)
        {
            rented = null;
            var need = path.Length + 1;
            var dst = need <= stack.Length ? stack : (rented = ArrayPool<char>.Shared.Rent(need));
            path.CopyTo(dst);
            dst[path.Length] = '\0';
            return dst[..need];
        }

        /// <summary>UTF-8 路径 → 以 NUL 结尾的 UTF-16 字符缓冲（转码 + 追加 '\0'）。</summary>
        internal static ReadOnlySpan<char> Utf16WithNul(ReadOnlySpan<byte> path, Span<char> stack, out char[]? rented)
        {
            rented = null;
            var need = Encoding.UTF8.GetMaxCharCount(path.Length) + 1;
            var dst = need <= stack.Length ? stack : (rented = ArrayPool<char>.Shared.Rent(need));
            if (Utf8.ToUtf16(path, dst, out _, out var written) != OperationStatus.Done)
            {
                if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                rented = null;
                throw new IOException("Path contains invalid UTF-8.");
            }
            dst[written] = '\0';
            return dst[..(written + 1)];
        }
    }
}
