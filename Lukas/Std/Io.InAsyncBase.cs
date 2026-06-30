// Lukas/Io.InAsyncBase.cs

using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lukas.AsyncEngine;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 异步带缓冲的输入端，基于 <see cref="IAsyncIoEngine"/> 读取，与 <see cref="OutAsyncBase"/> 对称。
    ///
    /// 维护字节缓冲与字符缓冲两级结构，并发由 <see cref="_gate"/> 串行化。
    /// 当读取方式从「按字符」切到「按字节」时，<c>FoldBufferedCharsIntoByteBuffer</c> 会把已解码的字符
    /// 和解码器内部残留状态回折成字节，保证字节流连续不丢数据。换行兼容 <c>\r\n</c>。
    /// </summary>
    internal sealed partial class InAsyncBase : IAsyncDisposable, IDisposable
    {
        private readonly IAsyncIoEngine _engine;
        private readonly int _fd;

        private readonly SemaphoreSlim _gate = new(1, 1);

        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

        private bool _disposed;

        internal InAsyncBase(IAsyncIoEngine engine, int fd, int charSize = 512, int byteSize = 4096)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (charSize < 1)
                throw new ArgumentOutOfRangeException(nameof(charSize), charSize, "Character buffer size must be greater than 0.");
            if (byteSize < 1)
                throw new ArgumentOutOfRangeException(nameof(byteSize), byteSize, "Byte buffer size must be greater than 0.");

            _engine = engine;
            _fd = fd;

            _charSize = charSize;
            _charBuffer = new char[charSize];

            _byteSize = byteSize;
            _byteBuffer = new byte[byteSize];
        }
        
        /// <summary>异步读取下一个字符；到达末尾返回 -1。</summary>
        internal async ValueTask<int> ReadAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return -1;

                if (_charPos >= _charLen && !await FillCharBufferAsync().ConfigureAwait(false))
                    return -1;

                return _charBuffer[_charPos++];
            }
            finally
            {
                _gate.Release();
            }
        }
        
        /// <summary>异步把数据读入字节缓冲区，返回实际字节数。会先把已缓冲的字符回折为字节再读。</summary>
        internal async ValueTask<int> ReadAsync(Memory<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return 0;
                
                FoldBufferedCharsIntoByteBuffer();

                var totalRead = 0;

                while (!buffer.IsEmpty)
                {
                    if (_bytePos >= _byteLen && !await FillByteBufferAsync().ConfigureAwait(false))
                        break;

                    var available = _byteLen - _bytePos;
                    var toCopy = Math.Min(available, buffer.Length);

                    _byteBuffer.AsSpan(_bytePos, toCopy).CopyTo(buffer.Span);

                    _bytePos += toCopy;
                    buffer = buffer[toCopy..];
                    totalRead += toCopy;
                }

                return totalRead;
            }
            finally
            {
                _gate.Release();
            }
        }
        
        /// <summary>异步把数据读入字符缓冲区，返回实际字符数。</summary>
        internal async ValueTask<int> ReadAsync(Memory<char> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return 0;

                var totalRead = 0;

                while (!buffer.IsEmpty)
                {
                    if (_charPos >= _charLen && !await FillCharBufferAsync().ConfigureAwait(false))
                        break;

                    var available = _charLen - _charPos;
                    var toCopy = Math.Min(available, buffer.Length);

                    _charBuffer.AsSpan(_charPos, toCopy).CopyTo(buffer.Span);

                    _charPos += toCopy;
                    buffer = buffer[toCopy..];
                    totalRead += toCopy;
                }

                return totalRead;
            }
            finally
            {
                _gate.Release();
            }
        }
        
        /// <summary>异步读取一行（不含行尾换行符，兼容 <c>\r\n</c>）；流结束且无内容返回 <c>null</c>。</summary>
        internal async ValueTask<string?> ReadLineAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return null;

                StringBuilder? builder = null;
                var any = false;

                while (true)
                {
                    if (_charPos >= _charLen && !await FillCharBufferAsync().ConfigureAwait(false))
                        break;

                    var c = _charBuffer[_charPos++];
                    any = true;

                    if (c == '\n')
                    {
                        if (builder is { Length: > 0 } && builder[^1] == '\r')
                            builder.Length--;
                        return builder?.ToString() ?? string.Empty;
                    }

                    builder ??= new StringBuilder();
                    builder.Append(c);
                }

                if (!any)
                    return null;

                if (builder is { Length: > 0 } && builder[^1] == '\r')
                    builder.Length--;

                return builder?.ToString() ?? string.Empty;
            }
            finally
            {
                _gate.Release();
            }
        }
        
        /// <summary>把剩余全部数据异步读入 <paramref name="writer"/>（先吐缓冲残留再直读到 EOF），返回总字节数。</summary>
        internal async ValueTask<int> ReadToEndAsync(IBufferWriter<byte> writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return 0;
                
                FoldBufferedCharsIntoByteBuffer();

                var totalRead = 0;
                
                if (_bytePos < _byteLen)
                {
                    var pending = _byteLen - _bytePos;
                    writer.Write(_byteBuffer.AsSpan(_bytePos, pending));
                    _bytePos = _byteLen;
                    totalRead += pending;
                }
                
                const int chunk = 4096;
                while (true)
                {
                    var dst = writer.GetMemory(chunk);
                    var n = await _engine.ReadAsync(_fd, dst).ConfigureAwait(false);
                    if (n <= 0)
                        break;

                    writer.Advance(n);
                    totalRead += n;
                }

                return totalRead;
            }
            finally
            {
                _gate.Release();
            }
        }
        
        /// <summary>把剩余全部数据按 UTF-8 解码为字符串返回。</summary>
        internal async ValueTask<string> ReadToEndAsync()
        {
            var sink = new ArrayBufferWriter<byte>();
            await ReadToEndAsync(sink).ConfigureAwait(false);
            return Encoding.UTF8.GetString(sink.WrittenSpan);
        }
        
        // 从「按字符读」切到「按字节读」时的衔接：把字符缓冲里已解码但未消费的字符、
        // 以及解码器内部缓存的残留字节，统一重新编码成 UTF-8 拼回字节缓冲前面，
        // 使后续按字节读取看到的字节流连续无缝。容量不够时换更大的缓冲。
        private void FoldBufferedCharsIntoByteBuffer()
        {
            Span<char> tail = stackalloc char[8];
            _decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                tail,
                flush: true,
                out _,
                out var tailChars,
                out _);

            var bufferedChars = _charLen - _charPos;
            if (bufferedChars == 0 && tailChars == 0)
                return;

            var maxBytes = Encoding.UTF8.GetMaxByteCount(Math.Max(1, bufferedChars + tailChars));
            var tmp = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                var n = 0;
                if (bufferedChars > 0)
                    n += Encoding.UTF8.GetBytes(_charBuffer.AsSpan(_charPos, bufferedChars), tmp.AsSpan(n));
                if (tailChars > 0)
                    n += Encoding.UTF8.GetBytes(tail[..tailChars], tmp.AsSpan(n));

                var undecoded = _byteLen - _bytePos;
                var needed = n + undecoded;

                if (needed > _byteSize)
                {
                    var bigger = new byte[needed];
                    if (undecoded > 0)
                        Array.Copy(_byteBuffer, _bytePos, bigger, n, undecoded);
                    tmp.AsSpan(0, n).CopyTo(bigger);
                    _byteBuffer = bigger;
                    _byteSize = needed;
                }
                else
                {
                    if (undecoded > 0)
                        Array.Copy(_byteBuffer, _bytePos, _byteBuffer, n, undecoded);
                    tmp.AsSpan(0, n).CopyTo(_byteBuffer);
                }

                _bytePos = 0;
                _byteLen = needed;
                _charPos = 0;
                _charLen = 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _gate.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
