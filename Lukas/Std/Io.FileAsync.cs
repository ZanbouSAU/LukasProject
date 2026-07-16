// Lukas/Io.FileAsync.cs

using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Lukas.AsyncEngine;
using Lukas.Interop;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 异步文件句柄：在 <see cref="IAsyncIoEngine"/> 之上提供 open/read/write/flush/close。
    ///
    /// 可传入外部引擎共用（<c>_ownsEngine == false</c>），也可由无参构造按平台自动选取引擎并独占持有。
    /// 一个实例同一时刻要么用于读、要么用于写：打开时按 flags 决定创建 <see cref="InAsyncBase"/> 还是 <see cref="OutAsyncBase"/>。
    /// 所有打开/关闭操作经 <see cref="_gate"/> 串行化。
    /// </summary>
    public sealed class FileAsync : IAsyncDisposable
    {
        private const int InvalidFd = -1;
        private const int StackPathThreshold = 512;

        private readonly IAsyncIoEngine _engine;
        private readonly bool _ownsEngine;

        private readonly SemaphoreSlim _gate = new(1, 1);

        private int _fd = InvalidFd;
        private bool _disposed;

        private OutAsyncBase? _writer;
        private InAsyncBase? _reader;
        
        /// <summary>使用调用方提供的引擎（不持有，释放时不销毁引擎）。</summary>
        public FileAsync(IAsyncIoEngine engine)
        {
            ArgumentNullException.ThrowIfNull(engine);
            _engine = engine;
            _ownsEngine = false;
        }

        /// <summary>按当前平台自动选取并独占一个默认引擎（释放时一并销毁）。</summary>
        public FileAsync()
        {
            _engine = AsyncEngineFactory.Create();
            _ownsEngine = true;
        }

        /// <summary>打开文件；默认以追加方式、权限 0o644（<c>0x1A4</c>）打开。</summary>
        public ValueTask OpenAsync(string path, Flags flags = Flags.Append, uint permission = 0x1A4)
        {
            ArgumentNullException.ThrowIfNull(path);
            return OpenAsync(path.AsMemory(), flags, permission);
        }

        public async ValueTask OpenAsync(
            ReadOnlyMemory<char> path,
            Flags flags = Flags.Append,
            uint permission = 0x1A4)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ReleaseLockedAsync().ConfigureAwait(false);

                var fd = await SubmitOpen(path.Span, flags, permission).ConfigureAwait(false);
                CompleteOpenLocked(fd, flags);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask OpenAsync(
            ReadOnlyMemory<byte> utf8Path,
            Flags flags = Flags.Append,
            uint permission = 0x1A4)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ReleaseLockedAsync().ConfigureAwait(false);

                var fd = await SubmitOpenUtf8(utf8Path.Span, flags, permission).ConfigureAwait(false);
                CompleteOpenLocked(fd, flags);
            }
            finally
            {
                _gate.Release();
            }
        }
        
        // 把 UTF-16 路径转成以 NUL 结尾的 UTF-8 字节传给引擎；短路径走栈缓冲，长路径租用池化数组。
        private ValueTask<int> SubmitOpen(ReadOnlySpan<char> path, Flags flags, uint permission)
        {
            var maxBytes = Encoding.UTF8.GetMaxByteCount(path.Length) + 1;

            byte[]? rented = null;
            var buf = maxBytes <= StackPathThreshold
                ? stackalloc byte[StackPathThreshold]
                : rented = ArrayPool<byte>.Shared.Rent(maxBytes);

            try
            {
                if (Utf8.FromUtf16(path, buf, out _, out var written) != OperationStatus.Done)
                    throw new IOException("File name contains invalid UTF-16.");

                buf[written] = 0;
                return _engine.OpenAsync(buf[..(written + 1)], flags, permission);
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
        
        // 已是 UTF-8 路径：若已以 NUL 结尾则直接用，否则补一个 NUL 再传。
        private ValueTask<int> SubmitOpenUtf8(ReadOnlySpan<byte> utf8Path, Flags flags, uint permission)
        {
            var needsNul = utf8Path.Length == 0 || utf8Path[^1] != 0;
            if (!needsNul)
                return _engine.OpenAsync(utf8Path, flags, permission);

            var total = utf8Path.Length + 1;
            byte[]? rented = null;
            var buf = total <= StackPathThreshold
                ? stackalloc byte[StackPathThreshold]
                : (rented = ArrayPool<byte>.Shared.Rent(total));

            try
            {
                utf8Path.CopyTo(buf);
                buf[utf8Path.Length] = 0;
                return _engine.OpenAsync(buf[..total], flags, permission);
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // 打开成功后记录 fd，并按 flags 建立读端或写端（只读建 reader，其余建 writer）。
        private void CompleteOpenLocked(int fd, Flags flags)
        {
            _fd = fd;

            if (flags == Flags.Read)
                _reader = new InAsyncBase(_engine, fd);
            else
                _writer = new OutAsyncBase(_engine, fd);

            _disposed = false;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> contents, bool isLine = false)
            => EnsureWritable().WriteAsync(contents, isLine);

        public ValueTask WriteLineAsync(ReadOnlyMemory<byte> contents)
            => EnsureWritable().WriteAsync(contents, isLine: true);

        public ValueTask WriteAsync(ReadOnlyMemory<char> contents, bool isLine = false)
            => EnsureWritable().WriteAsync(contents, isLine);

        public ValueTask WriteLineAsync(ReadOnlyMemory<char> contents)
            => EnsureWritable().WriteAsync(contents, isLine: true);

        public ValueTask WriteAsync(string contents, bool isLine = false)
        {
            ArgumentNullException.ThrowIfNull(contents);
            return EnsureWritable().WriteAsync(contents.AsMemory(), isLine);
        }

        public ValueTask WriteLineAsync(string contents)
            => WriteAsync(contents, isLine: true);

        public ValueTask FlushAsync()
            => EnsureWritable().FlushAsync();

        public ValueTask<int> ReadAsync(Memory<byte> buffer)
            => EnsureReadable().ReadAsync(buffer);

        public ValueTask<int> ReadAsync(Memory<char> buffer)
            => EnsureReadable().ReadAsync(buffer);

        public ValueTask<int> ReadAsync()
            => EnsureReadable().ReadAsync();

        public ValueTask<string?> ReadLineAsync()
            => EnsureReadable().ReadLineAsync();

        public ValueTask<int> ReadToEndAsync(IBufferWriter<byte> writer)
            => EnsureReadable().ReadToEndAsync(writer);

        public ValueTask<string> ReadToEndAsync()
            => EnsureReadable().ReadToEndAsync();

        /// <summary>关闭文件并释放读/写端与底层 fd；可重复调用。</summary>
        public async ValueTask CloseAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;

                _disposed = true;
                await ReleaseLockedAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);

            if (_ownsEngine)
                _engine.Dispose();

            _gate.Dispose();
        }
        
        // 在已持锁的前提下释放写端、读端和 fd（关闭 fd 的异常被吞掉，因为此时已无可补救）。
        private async ValueTask ReleaseLockedAsync()
        {
            if (_writer != null)
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
                _writer = null;
            }

            if (_reader != null)
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
                _reader = null;
            }

            if (_fd != InvalidFd)
            {
                try
                {
                    await _engine.CloseAsync(_fd).ConfigureAwait(false);
                }
                catch
                {
                    /* Got it. Ignored. */
                }

                _fd = InvalidFd;
            }
        }

        // 取写端，未打开或未以可写方式打开则抛异常。
        private OutAsyncBase EnsureWritable()
        {
            return _disposed
                ? throw new ObjectDisposedException(nameof(FileAsync))
                : _writer ?? throw new InvalidOperationException("File is not open for writing.");
        }

        // 取读端，未打开或未以可读方式打开则抛异常。
        private InAsyncBase EnsureReadable()
        {
            return _disposed
                ? throw new ObjectDisposedException(nameof(FileAsync))
                : _reader ?? throw new InvalidOperationException("File is not open for reading.");
        }
    }
}
