// Lukas/Io.OutAsyncBase.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using Lukas.AsyncEngine;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 异步带缓冲的输出端，基于 <see cref="IAsyncIoEngine"/>（io_uring / IOCP / 线程池）写入。
    ///
    /// 与同步的 <see cref="OutBase"/> 同构（两级字符/字节缓冲、UTF-8 转码、可选自动冲刷），
    /// 区别是落盘走异步引擎且并发由 <see cref="_gate"/>（信号量）串行化，确保同一时刻只有一个写操作在进行。
    /// 跨 await 可能出现半个代理对（high surrogate）滞留，需由 <c>FlushDanglingHighSurrogateAsync</c> 收尾。
    /// </summary>
    internal sealed partial class OutAsyncBase : IAsyncDisposable, IDisposable
    {
        private static readonly ReadOnlyMemory<byte> NewlineByte = new byte[] { 0x0A };

        private readonly IAsyncIoEngine _engine;
        private readonly int _fd;

        private readonly SemaphoreSlim _gate = new(1, 1);

        private bool _enableAutoFlush;

        private bool _disposed;

        internal OutAsyncBase(IAsyncIoEngine engine, int fd, int charSize = 512, int byteSize = 4096)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (charSize < 1)
                throw new ArgumentOutOfRangeException(nameof(charSize), charSize, "字符缓冲区大小必须大于 0。");
            if (byteSize < 1)
                throw new ArgumentOutOfRangeException(nameof(byteSize), byteSize, "字节缓冲区大小必须大于 0。");

            _engine = engine;
            _fd = fd;

            _charSize = charSize;
            _charBuffer = new char[charSize];

            _byteSize = byteSize;
            _byteBuffer = new byte[byteSize];
        }

        /// <summary>异步写入一段字节；<paramref name="isLine"/> 为真时追加换行并按需自动冲刷。</summary>
        internal async ValueTask WriteAsync(ReadOnlyMemory<byte> value, bool isLine = false)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                await ProcessDataAsync(value).ConfigureAwait(false);

                if (!isLine)
                    return;

                await ProcessDataAsync(NewlineByte).ConfigureAwait(false);

                if (_enableAutoFlush)
                    await ByteFlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>异步写入一段字符；空内容且 <paramref name="isLine"/> 时只写一个换行。</summary>
        internal async ValueTask WriteAsync(ReadOnlyMemory<char> value, bool isLine = false)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                if (value.IsEmpty)
                {
                    if (!isLine)
                        return;

                    await ProcessDataAsync(NewlineByte).ConfigureAwait(false);
                    if (_enableAutoFlush)
                        await ByteFlushAsync().ConfigureAwait(false);
                    return;
                }

                await WriteCharsAsync(value, isLine).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>把字符缓冲、滞留的高代理项、字节缓冲依次异步冲刷落盘。</summary>
        internal async ValueTask FlushAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;

                await CharFlushAsync().ConfigureAwait(false);
                await FlushDanglingHighSurrogateAsync().ConfigureAwait(false);
                await ByteFlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal void EnableAutoFlush(bool enableAutoFlush)
        {
            _gate.Wait();
            try
            {
                if (_disposed)
                    return;
                _enableAutoFlush = enableAutoFlush;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;

                try
                {
                    await CharFlushAsync().ConfigureAwait(false);
                    await FlushDanglingHighSurrogateAsync().ConfigureAwait(false);
                }
                catch
                {
                    /* 释放时忽略刷新异常 */
                }

                try
                {
                    await ByteFlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    /* 释放时忽略刷新异常 */
                }

                _disposed = true;
            }
            finally
            {
                _gate.Release();
            }

            _gate.Dispose();
        }
        
        // 同步释放：桥接到异步释放并阻塞等待完成（仅在无法 await 的场景使用）。
        public void Dispose()
        {
            if (_disposed)
                return;

            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OutAsyncBase));
        }
    }
}
