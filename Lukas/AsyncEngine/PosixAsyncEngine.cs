// Lukas/AsyncEngine/PosixAsyncEngine.cs

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Lukas.Interop;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace Lukas.AsyncEngine;

/// <summary>
/// 可移植的兜底异步 I/O 引擎，适用于 Linux/macOS/FreeBSD（在没有 io_uring 或 IOCP 时使用）。
///
/// 它没有真正的内核异步机制，而是用一组后台工作线程把<b>阻塞</b>的 POSIX 系统调用
/// （open/read/write/close，必要时带偏移用 pread/pwrite）放到线程外执行，从而对上层呈现为异步。
/// 请求经一个阻塞队列分发给工作线程；<see cref="Operation"/> 的池化与并发收尾模型与另两个引擎一致。
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed unsafe class PosixAsyncEngine : IAsyncIoEngine
{
    private const int DefaultConcurrency = 4;
    private const int Eintr = 4;   // EINTR：被信号打断，需重试

    private readonly Thread[] _workers;
    private readonly BlockingCollection<Operation> _queue = new(new ConcurrentQueue<Operation>());  // 待执行请求队列
    private readonly ConcurrentQueue<Operation> _pool = new();                                       // Operation 对象池

    private int _disposed;

    private static bool IsSupported =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    /// <summary>创建引擎并启动 <paramref name="concurrency"/> 个后台工作线程。</summary>
    public PosixAsyncEngine(int concurrency = DefaultConcurrency)
    {
#if DEBUG
        Io.Println("Due to errors or system incompatibility, rollback to PosixAsyncEngine!");
        Io.FlushOut();  
#endif
        if (concurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(concurrency), concurrency, "Concurrency must be greater than 0.");

        if (!IsSupported)
            throw new PlatformNotSupportedException("PosixAsyncEngine is only supported on Linux, macOS and FreeBSD.");

        _workers = new Thread[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            var t = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"PosixAsyncEngine-IO-{i}"
            };
            _workers[i] = t;
        }
        
        foreach (var t in _workers)
            t.Start();
    }

    /// <summary>异步打开文件（实际由工作线程执行阻塞 open）。路径会被复制一份以脱离调用方栈。</summary>
    public ValueTask<int> OpenAsync(
        ReadOnlySpan<byte> path,
        Flags flags = Flags.Append,
        uint permission = 0x1A4,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        Io.Println("Execute PosixAsyncEngine open method!");
        Io.FlushOut();
#endif
        
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }
        
        op.PrepareOpen(path.ToArray(), ResolveOpenFlags(flags), permission);

        Submit(op, cancellationToken);
        return op.AsValueTask();
    }
    
    // todo 未完成
    public ValueTask<int> AcceptAsync(int fd, nint addr, nint addrLen, uint acceptFlags,
        CancellationToken cancellationToken = default)
    {
        var op = RentOperation();
        return op.AsValueTask();
    }

    public ValueTask<int> ReadAsync(int fd, Memory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Io.Println("Execute PosixAsyncEngine read method!");
        Io.FlushOut();
#endif
        
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        op.PrepareReadWrite(OpKind.Read, fd, buffer, offset);
        Submit(op, cancellationToken);
        return op.AsValueTask();
    }

    public ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Io.Println("Execute PosixAsyncEngine write method!");
        Io.FlushOut();
#endif
        
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        op.PrepareReadWrite(OpKind.Write, fd, MemoryMarshal.AsMemory(buffer), offset);
        Submit(op, cancellationToken);
        return op.AsValueTask();
    }

    public ValueTask CloseAsync(int fd, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Io.Println("Execute PosixAsyncEngine close method!");
        Io.FlushOut();
#endif
        
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsVoidValueTask();
        }

        op.PrepareClose(fd);
        Submit(op, cancellationToken);
        return op.AsVoidValueTask();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DisposeCore();
    }

    ~PosixAsyncEngine() => DisposeCore();

    // 把操作放入队列等待工作线程领取。若队列已关闭（引擎正在释放），就地以「已释放」收尾。
    private void Submit(Operation op, CancellationToken cancellationToken)
    {
#if DEBUG
        Io.Println("Execute PosixAsyncEngine submit method!");
        Io.FlushOut();
#endif
        
        op.MarkAwaitingKernel();
        RegisterCancellation(op, cancellationToken);

        try
        {
            _queue.Add(op, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            op.FinishWithoutKernel(new ObjectDisposedException(nameof(PosixAsyncEngine)));
        }
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.CompleteAdding();

        if (_workers != null)
        {
            foreach (var t in _workers)
            {
                if (t is { IsAlive: true })
                {
                    try
                    {
                        t.Join(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        /* best-effort shutdown */
                    }
                }
            }
        }
        
        while (_queue.TryTake(out var op))
            op.FinishWithoutKernel(new ObjectDisposedException(nameof(PosixAsyncEngine)));

        _queue.Dispose();
    }

    // 工作线程主体：不断从队列取出操作并执行；队列关闭后循环自然结束。
    private void WorkerLoop()
    {
        foreach (var op in _queue.GetConsumingEnumerable())
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                op.FinishWithoutKernel(new ObjectDisposedException(nameof(PosixAsyncEngine)));
                continue;
            }

            op.RunOnWorker();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PosixAsyncEngine));
    }

    private static void RegisterCancellation(Operation op, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return;

        var ctr = cancellationToken.Register(static state =>
        {
            var (o, ct) = ((Operation, CancellationToken))state!;
            o.CompleteCanceled(ct);
        }, (op, cancellationToken));

        op.SetCancellationRegistration(ctr);
    }

    private Operation RentOperation() => _pool.TryDequeue(out var op) ? op : new Operation(this);

    private void ReturnOperation(Operation op) => _pool.Enqueue(op);

    private static int ResolveOpenFlags(Flags palFlags)
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

        return Sys.ResolveOpenFlags(flags);
    }

    // 操作种类，决定工作线程执行哪个系统调用。
    private enum OpKind : byte
    {
        Open,
        Read,
        Write,
        Close
    }

    // 一次操作的载体。与另两个引擎不同：这里的系统调用就在 RunOnWorker 里同步执行（在工作线程上），
    // 所有阻塞调用都对 EINTR 做重试。并发收尾模型（结果闩锁 + 双方到齐回收）保持一致。
    private sealed class Operation : IValueTaskSource<int>, IValueTaskSource
    {
        private readonly PosixAsyncEngine _owner;

        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };

        private OpKind _kind;
        private int _fd;
        private long _offset;

        private Memory<byte> _buffer;
        private byte[]? _path;
        private uint _mode;

        private int _resultLatch;
        private int _recycleGate;
        private bool _awaitingKernel;

        private CancellationTokenRegistration _ctr;

        internal Operation(PosixAsyncEngine owner) => _owner = owner;

        internal void PrepareOpen(byte[] path, int oflag, uint mode)
        {
            _kind = OpKind.Open;
            _path = path;
            _fd = oflag;
            _mode = mode;
        }

        internal void PrepareReadWrite(OpKind kind, int fd, Memory<byte> buffer, long offset)
        {
            _kind = kind;
            _fd = fd;
            _buffer = buffer;
            _offset = offset;
        }

        internal void PrepareClose(int fd)
        {
            _kind = OpKind.Close;
            _fd = fd;
        }

        internal void SetCancellationRegistration(CancellationTokenRegistration ctr) => _ctr = ctr;

        internal void MarkAwaitingKernel() => _awaitingKernel = true;

        private bool TryLatch() => Interlocked.Exchange(ref _resultLatch, 1) == 0;
        
        // 在工作线程上执行：若已被取消（闩锁已占），直接收尾；否则按种类执行对应系统调用并兑现结果。
        internal void RunOnWorker()
        {
            if (Volatile.Read(ref _resultLatch) != 0)
            {
                ReleaseResources();
                AdvanceRecycle();
                return;
            }

            var result = _kind switch
            {
                OpKind.Open => DoOpen(),
                OpKind.Read => DoRead(),
                OpKind.Write => DoWrite(),
                OpKind.Close => DoClose(),
                _ => -22
            };

            CompleteFromKernel(result);
        }

        private int DoOpen()
        {
            var path = _path!;
            fixed (byte* p = path)
            {
                int fd;
                do
                {
                    fd = Sys.Open(p, _fd, (int)_mode);
                } while (fd < 0 && Marshal.GetLastPInvokeError() == Eintr);

                return fd >= 0 ? fd : -Marshal.GetLastPInvokeError();
            }
        }

        private int DoRead()
        {
            if (_buffer.IsEmpty)
                return 0;

            using var handle = _buffer.Pin();
            var ptr = (byte*)handle.Pointer;
            var len = _buffer.Length;
            var offset = _offset;

            int n;
            do
            {
                n = offset < 0 ? Sys.Read(_fd, ptr, len) : Sys.Pread(_fd, ptr, len, offset);
            } while (n < 0 && Marshal.GetLastPInvokeError() == Eintr);

            return n >= 0 ? n : -Marshal.GetLastPInvokeError();
        }

        private int DoWrite()
        {
            if (_buffer.IsEmpty)
                return 0;

            using var handle = _buffer.Pin();
            var ptr = (byte*)handle.Pointer;
            var len = _buffer.Length;
            var offset = _offset;

            int n;
            do
            {
                n = offset < 0 ? Sys.Write(_fd, ptr, len) : Sys.Pwrite(_fd, ptr, len, offset);
            } while (n < 0 && Marshal.GetLastPInvokeError() == Eintr);

            return n >= 0 ? n : -Marshal.GetLastPInvokeError();
        }

        private int DoClose()
        {
            int rc;
            do
            {
                rc = Sys.Close(_fd);
            } while (rc < 0 && Marshal.GetLastPInvokeError() == Eintr);

            return rc == 0 ? 0 : -Marshal.GetLastPInvokeError();
        }

        private void CompleteFromKernel(int result)
        {
            if (TryLatch())
            {
                if (result < 0)
                    _core.SetException(new PosixIoException(-result));
                else
                    _core.SetResult(result);
            }

            ReleaseResources();
            AdvanceRecycle();
        }
        
        internal void CompleteCanceled(CancellationToken token)
        {
            if (TryLatch())
                _core.SetException(new OperationCanceledException(token));
        }
        
        // 没有真正下发到系统调用就结束（如引擎已释放）：设异常并收尾。
        internal void FinishWithoutKernel(Exception ex)
        {
            if (TryLatch())
                _core.SetException(ex);

            ReleaseResources();
            AdvanceRecycle();
        }

        private void ReleaseResources()
        {
            _ctr.Dispose();
            _ctr = default;

            _buffer = default;
            _path = null;
        }

        private void AdvanceRecycle()
        {
            if (Interlocked.Increment(ref _recycleGate) == 2)
                RecycleNow();
        }

        private void RecycleNow()
        {
            _resultLatch = 0;
            _recycleGate = 0;
            _awaitingKernel = false;
            _fd = 0;
            _offset = 0;
            _mode = 0;
            _core.Reset();
            _owner.ReturnOperation(this);
        }

        internal ValueTask<int> AsValueTask() => new(this, _core.Version);

        internal ValueTask AsVoidValueTask() => new(this, _core.Version);

        private void OnResultConsumed()
        {
            if (!_awaitingKernel)
                RecycleNow();
            else
                AdvanceRecycle();
        }

        int IValueTaskSource<int>.GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                OnResultConsumed();
            }
        }

        void IValueTaskSource.GetResult(short token)
        {
            try
            {
                _core.GetResult(token);
            }
            finally
            {
                OnResultConsumed();
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}

internal sealed class PosixIoException(int errno) : IOException($"POSIX I/O operation failed: errno {errno}")
{
    internal int Errno { get; } = errno;
}
