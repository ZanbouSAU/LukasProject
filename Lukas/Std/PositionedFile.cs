// FileCommon/PositionedFile.cs

using System;
using System.IO;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Lukas.AsyncEngine;
using Lukas.Interop;
using Lukas.Interop.Unix.System.Native;

namespace Lukas.Std;

/// <summary>
/// 基于 <see cref="IAsyncIoEngine"/> 的<b>带偏移</b>文件读写封装（定位读写，positioned I/O）。
///
/// 与 <see cref="Io.FileAsync"/> 不同，这里每次读写都显式指定文件偏移，适合按块乱序传输的场景。
/// <see cref="ReadFullAsync"/>/<see cref="WriteAllAsync"/> 会循环直到读满/写完，屏蔽底层的部分读写。
/// </summary>
public sealed class PositionedFile : IAsyncDisposable
{
    private readonly IAsyncIoEngine _engine;
    private int _fd;
    private bool _closed;

    private const int InvalidFd = -1;

    private PositionedFile(IAsyncIoEngine engine, int fd)
    {
        _engine = engine;
        _fd = fd;
    }

    public int Fd => _fd;
    
    /// <summary>打开文件，默认权限 0o644（<c>0x1A4</c>）；返回的 fd 为负视为失败并抛出。</summary>
    public static async ValueTask<PositionedFile> OpenAsync(
        IAsyncIoEngine engine,
        string path,
        Flags flags,
        uint permission = 0x1A4,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var utf8 = ToNulTerminatedUtf8(path);
        var fd = await engine.OpenAsync(utf8, flags, permission, ct).ConfigureAwait(false);
        return fd < 0 ? throw new IOException($"Failed to open \"{path}\" (fd={fd}).") : new PositionedFile(engine, fd);
    }
    
    /// <summary>从 <paramref name="offset"/> 起尽量读满 <paramref name="buffer"/>，返回实际字节数（遇 EOF 可少于请求长度）。</summary>
    public async ValueTask<int> ReadFullAsync(Memory<byte> buffer, long offset, CancellationToken ct = default)
    {
        ThrowIfClosed();
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await _engine.ReadAsync(_fd, buffer[total..], offset + total, ct).ConfigureAwait(false);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }
    
    /// <summary>从 <paramref name="offset"/> 起把整段数据写完；中途出现非正写入数即抛出短写异常。</summary>
    public async ValueTask WriteAllAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken ct = default)
    {
        ThrowIfClosed();
        var written = 0;
        while (written < data.Length)
        {
            var n = await _engine.WriteAsync(_fd, data[written..], offset + written, ct).ConfigureAwait(false);
            if (n <= 0)
                throw new IOException("Short write: io_uring write returned non-positive count.");
            written += n;
        }
    }

    /// <summary>关闭文件；用原子交换把 fd 置无效，保证并发下只关闭一次。可重复调用。</summary>
    public async ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (_closed)
            return;
        _closed = true;
        var fd = Interlocked.Exchange(ref _fd, InvalidFd);
        if (fd != InvalidFd)
            await _engine.CloseAsync(fd, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore close errors during the release phase.
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(PositionedFile));
    }

    // 把路径转成以 NUL 结尾的 UTF-8 字节数组，供原生 open 使用。
    private static byte[] ToNulTerminatedUtf8(string path)
    {
        var n = Encoding.UTF8.GetByteCount(path);
        var buf = new byte[n + 1];
        Utf8.FromUtf16(path, buf, out _, out _);
        buf[n] = 0;
        return buf;
    }
}
