// Lukas/AsyncEngine/IocpEngine.cs

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Lukas.Interop;
using Lukas.Interop.Unix.System.Native;
using Lukas.Interop.Windows.Kernel32;
using Lukas.Std;

namespace Lukas.AsyncEngine;

/// <summary>
/// Windows 上基于 I/O 完成端口（IOCP）的异步 I/O 引擎。
///
/// 文件以 OVERLAPPED（重叠）方式打开并关联到同一个完成端口；一个后台线程跑
/// <see cref="CompletionLoop"/> 不断取出完成事件，按 OVERLAPPED 指针找回对应
/// <see cref="Operation"/> 兑现结果。<see cref="Operation"/> 同样是池化的
/// <see cref="IValueTaskSource{T}"/>，并发收尾逻辑与 <see cref="IoUringEngine"/> 中的同名类一致
/// （结果闩锁 + 双方到齐才回收）。
///
/// 这里的 fd 是引擎自己分配的逻辑编号，经 <c>_handles</c> 映射到真实的 Win32 句柄。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class IocpEngine : IAsyncIoEngine
{
    private const int ErrorIoPending = 997;
    private const int ErrorHandleEof = 38;
    private const int ErrorOperationAborted = 995;
    private const uint Infinite = 0xFFFFFFFF;
    
    private static readonly nuint ShutdownKey = unchecked((nuint)ulong.MaxValue);
    private const nuint FileKey = 1;

    private readonly nint _port;                  // I/O 完成端口句柄
    private readonly Thread _completionThread;     // 取完成事件的后台线程

    private readonly ConcurrentDictionary<int, nint> _handles = new();      // 逻辑 fd → Win32 句柄
    private readonly ConcurrentDictionary<int, StrongBox<long>> _positions = new(); // 逻辑 fd → 顺序读写的逻辑游标
    private readonly ConcurrentDictionary<nint, Operation> _pending = new(); // OVERLAPPED 指针 → 进行中的操作
    private readonly ConcurrentQueue<Operation> _pool = new();               // Operation 对象池

    private int _fdCounter;
    private int _disposed;

    public IocpEngine()
    {
#if DEBUG
        Log.Info("Execute IOCP constructor!");
#endif
        
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("IocpEngine is only supported on Windows.");

        var port = Kernel32.CreateIoCompletionPort(Kernel32.InvalidHandleValue, 0, 0, 0);
        if (port == 0)
            throw new IocpException(Marshal.GetLastPInvokeError());

        _port = port;

        _completionThread = new Thread(CompletionLoop)
        {
            IsBackground = true,
            Name = "IocpEngine-Completion"
        };

        try
        {
#if DEBUG
            Log.Info("Execute io_uring start!");
#endif
            _completionThread.Start();
        }
        catch (Exception ex)
        {
#if DEBUG
            Log.Error("io_uring creation failed!");
            Log.Error(ex.ToString());
#endif
            Kernel32.CloseHandle(_port);
            throw;
        }
    }

    /// <summary>
    /// 以重叠模式打开文件并关联到完成端口，返回逻辑 fd。
    /// 路径以 NUL 结尾的 UTF-8 传入，内部解码为 UTF-16 后调用 CreateFileW。
    /// </summary>
    public ValueTask<int> OpenAsync(
        ReadOnlySpan<byte> path,
        Flags flags = Flags.Append,
        uint permission = 0x1A4,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute IOCP open!");
#endif

        ThrowIfDisposed();

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<int>(cancellationToken);
        
        var nameUtf16 = DecodeNulTerminatedUtf8(path);

        var handle = Io.Pal.CreateFile(nameUtf16, flags, overlapped: true);

        if (handle == Kernel32.InvalidHandleValue)
            return ValueTask.FromException<int>(new IocpException(Marshal.GetLastPInvokeError()));
        
        var assoc = Kernel32.CreateIoCompletionPort(handle, _port, FileKey, 0);
        if (assoc == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            Kernel32.CloseHandle(handle);
            return ValueTask.FromException<int>(new IocpException(err));
        }

        var fd = Interlocked.Increment(ref _fdCounter);
        _handles[fd] = handle;

        // IOCP 的重叠读写不维护"文件当前位置"，因此引擎自行为每个句柄记一个逻辑游标。
        // 追加模式从文件末尾起步（对齐 POSIX O_APPEND 的顺序写语义）；其余模式从 0 起步。
        long initialPosition = 0;
        if (flags == Flags.Append &&
            Kernel32.GetFileAttributesEx(nameUtf16, out var attrs))
        {
            initialPosition = ((long)attrs.FileSizeHigh << 32) | attrs.FileSizeLow;
        }

        _positions[fd] = new StrongBox<long>(initialPosition);
        
#if DEBUG
        Log.Info("IOCP open successfully！");
#endif
        return new ValueTask<int>(fd);
    }
    
    // todo 未完成
    public ValueTask<int> AcceptAsync(int fd, nint addr, nint addrLen, uint acceptFlags,
        CancellationToken cancellationToken = default)
    {
        var op = RentOperation();
        return op.AsValueTask();
    }

    public ValueTask<int> ReadAsync(int fd, Memory<byte> buffer, long offset = -1,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute IOCP read!");
#endif
        return SubmitAsync(fd, buffer, offset, isWrite: false, cancellationToken);
    }

    public ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> buffer, long offset = -1,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute IOCP write!");
#endif
        return SubmitAsync(fd, MemoryMarshal.AsMemory(buffer), offset, isWrite: true, cancellationToken);
    }

    // 读写共用的提交路径。为本次操作分配 OVERLAPPED 并固定缓冲区，按 OVERLAPPED 指针登记进 _pending，
    // 然后发起重叠读/写。常见返回是「同步失败 + ERROR_IO_PENDING」——表示已挂起、稍后由完成端口回收；
    // 此外还要处理直接成功、读到文件尾（EOF→0）、以及其它同步错误几种情况。
    private ValueTask<int> SubmitAsync(int fd, Memory<byte> buffer, long offset, bool isWrite, CancellationToken cancellationToken)
    {
#if DEBUG
        Log.Info("Execute IOCP submit!");
#endif
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceledInline(cancellationToken);
            return op.AsValueTask();
        }

        if (!_handles.TryGetValue(fd, out var handle))
        {
            op.CompleteInlineError(ErrorInvalidHandle);
            return op.AsValueTask();
        }

        // 解析本次操作的实际文件偏移：
        //   offset >= 0：定位 I/O，按给定偏移读写，且不动顺序游标；
        //   offset <  0：顺序 I/O，读取并推进本句柄的逻辑游标（IOCP 不维护"当前位置"，需自行记账）。
        StrongBox<long>? cursor = null;
        long effectiveOffset;
        if (offset >= 0)
        {
            effectiveOffset = offset;
        }
        else
        {
            if (!_positions.TryGetValue(fd, out cursor))
            {
                op.CompleteInlineError(ErrorInvalidHandle);
                return op.AsValueTask();
            }

            effectiveOffset = Volatile.Read(ref cursor.Value);
        }

        var handlePin = buffer.Pin();
        var ov = (Interop.Windows.Kernel32.NativeOverlapped*)NativeMemory
            .AllocZeroed((nuint)sizeof(Interop.Windows.Kernel32.NativeOverlapped));
        ov->SetOffset(effectiveOffset);

        op.Prepare(handlePin, ov, handle, cursor);

        var ovKey = (nint)ov;
        _pending[ovKey] = op;
        op.MarkAwaitingKernel();

        var ptr = (byte*)handlePin.Pointer;
        var len = buffer.Length;
        var rc = isWrite
            ? Kernel32.WriteFileOverlapped(handle, ptr, len, null, ov)
            : Kernel32.ReadFileOverlapped(handle, ptr, len, null, ov);

        if (rc == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorIoPending)
            {
                RegisterCancellation(op, cancellationToken);
            }
            else if (!isWrite && err == ErrorHandleEof)
            {
                if (_pending.TryRemove(ovKey, out _))
                    op.CompleteFromKernel(0);
            }
            else
            {
                if (_pending.TryRemove(ovKey, out _))
                    op.CompleteSubmitFailure(-err);
            }
        }
        else
        {
            RegisterCancellation(op, cancellationToken);
        }

#if DEBUG
        Log.Info("IOCP submit successfully！");
#endif
        return op.AsValueTask();
    }

    /// <summary>关闭逻辑 fd 对应的句柄。关闭句柄即会取消其上挂起的 I/O。</summary>
    public ValueTask CloseAsync(int fd, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute IOCP close!");
#endif
        ThrowIfDisposed();

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        
        if (_handles.TryRemove(fd, out var handle) && handle != Kernel32.InvalidHandleValue)
        {
            _positions.TryRemove(fd, out _);
            if (!Kernel32.CloseHandle(handle))
                return ValueTask.FromException(new IocpException(Marshal.GetLastPInvokeError()));
        }

#if DEBUG
        Log.Info("IOCP close successfully！");
#endif
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DisposeCore();
    }

    ~IocpEngine() => DisposeCore();

    // 关闭所有句柄，向完成端口投递一个特殊的关闭事件（ShutdownKey）唤醒后台线程退出，
    // 再把仍挂起的操作以「已中止」收尾，最后关闭完成端口。
    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        
        foreach (var kv in _handles)
        {
            if (_handles.TryRemove(kv.Key, out var h) && h != Kernel32.InvalidHandleValue)
                Kernel32.CloseHandle(h);
        }

        _positions.Clear();
        
        if (_port != 0)
        {
            Kernel32.PostQueuedCompletionStatus(_port, 0, ShutdownKey, 0);
            try { _completionThread.Join(TimeSpan.FromSeconds(5)); }
            catch { /* */ }
        }
        
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var op))
                op.CompleteFromKernel(-ErrorOperationAborted);
        }

        if (_port != 0)
            Kernel32.CloseHandle(_port);
    }

    // 后台线程主循环：阻塞等待完成事件。lpOverlapped 为 0 表示是唤醒/关闭信号；
    // 否则按该指针找回操作，成功则回传字节数，EOF 当作 0，其余错误回传 -errno。
    private void CompletionLoop()
    {
        while (true)
        {
            var ok = Kernel32.GetQueuedCompletionStatus(
                _port, out var bytes, out var key, out var lpOverlapped, Infinite);

            if (lpOverlapped == 0)
            {
                if (key == ShutdownKey || Volatile.Read(ref _disposed) != 0)
                    return;
                continue;
            }

            if (!_pending.TryRemove(lpOverlapped, out var op))
                continue;

            if (ok != 0)
            {
                op.CompleteFromKernel((int)bytes);
            }
            else
            {
                var err = Marshal.GetLastPInvokeError();
                if (err == ErrorHandleEof)
                    op.CompleteFromKernel(0);
                else
                    op.CompleteFromKernel(-err);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(IocpEngine));
    }

    private const int ErrorInvalidHandle = 6;

    private static void RegisterCancellation(Operation op, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return;

        var ctr = cancellationToken.Register(static state =>
        {
            var (o, ct) = ((Operation, CancellationToken))state!;
            o.RequestCancel(ct);
        }, (op, cancellationToken));

        op.SetCancellationRegistration(ctr);
    }

    private Operation RentOperation() => _pool.TryDequeue(out var op) ? op : new Operation(this);

    private void ReturnOperation(Operation op) => _pool.Enqueue(op);

    private static char[] DecodeNulTerminatedUtf8(ReadOnlySpan<byte> path)
    {
        var len = path.IndexOf((byte)0);
        if (len < 0)
            len = path.Length;
        var slice = path[..len];
        var count = System.Text.Encoding.UTF8.GetCharCount(slice);
        var chars = new char[count];
        System.Text.Encoding.UTF8.GetChars(slice, chars);
        return chars;
    }

    // 一次重叠 I/O 的载体，并发收尾模型同 IoUringEngine.Operation：
    // _resultLatch 仲裁结果来源（完成端口 / 取消），_recycleGate 计到 2（内核侧 + 消费侧）才归还池。
    // 取消时除设异常外还调用 CancelIoEx 主动取消内核里挂起的那次 I/O。
    private sealed class Operation : IValueTaskSource<int>, IValueTaskSource
    {
        private readonly IocpEngine _owner;

        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };

        private MemoryHandle _handle;
        private bool _hasHandle;

        private Interop.Windows.Kernel32.NativeOverlapped* _ov;   // 本次操作的 OVERLAPPED（原生内存）
        private nint _fileHandle;                                  // 取消时用来定位要中止的句柄
        private StrongBox<long>? _cursor;                          // 顺序 I/O 时本句柄的逻辑游标；定位 I/O 为 null

        private int _resultLatch;
        private int _recycleGate;
        private bool _awaitingKernel;

        private CancellationTokenRegistration _ctr;

        internal Operation(IocpEngine owner) => _owner = owner;

        internal void Prepare(MemoryHandle handle, Interop.Windows.Kernel32.NativeOverlapped* ov, nint fileHandle, StrongBox<long>? cursor)
        {
            _handle = handle;
            _hasHandle = true;
            _ov = ov;
            _fileHandle = fileHandle;
            _cursor = cursor;
        }

        internal void SetCancellationRegistration(CancellationTokenRegistration ctr) => _ctr = ctr;

        internal void MarkAwaitingKernel() => _awaitingKernel = true;

        private bool TryLatch() => Interlocked.Exchange(ref _resultLatch, 1) == 0;
        
        // 取消：抢到结果权后设取消异常，并请求内核中止该 OVERLAPPED 上挂起的 I/O。
        // 资源与回收门留给随后的完成事件收尾（中止后完成端口仍会投递一个事件回来）。
        internal void RequestCancel(CancellationToken token)
        {
            if (TryLatch())
            {
                _core.SetException(new OperationCanceledException(token));
                if (_ov != null && _fileHandle != 0)
                    Kernel32.CancelIoEx(_fileHandle, (nint)_ov);
            }
        }

        internal void CompleteFromKernel(int result)
        {
            if (TryLatch())
            {
                if (result < 0)
                {
                    _core.SetException(new IocpException(-result));
                }
                else
                {
                    // 顺序 I/O：在发布结果前先把逻辑游标推进 result 字节，
                    // 确保等待者被唤醒后发起的下一次顺序读写能看到更新后的位置。
                    if (result > 0 && _cursor != null)
                        Interlocked.Add(ref _cursor.Value, result);

                    _core.SetResult(result);
                }
            }

            ReleaseResources();
            AdvanceRecycle();
        }

        internal void CompleteCanceledInline(CancellationToken token)
        {
            if (TryLatch())
                _core.SetException(new OperationCanceledException(token));
        }
        
        internal void CompleteInlineError(int error)
        {
            if (TryLatch())
                _core.SetException(new IocpException(error));
        }

        internal void CompleteSubmitFailure(int result)
        {
            if (TryLatch())
            {
                if (result < 0)
                    _core.SetException(new IocpException(-result));
                else
                    _core.SetResult(result);
            }

            ReleaseResources();
            AdvanceRecycle();
        }

        // 释放本次操作占用的资源：注销取消注册、解除缓冲区固定、释放 OVERLAPPED 原生内存。
        private void ReleaseResources()
        {
            _ctr.Dispose();
            _ctr = default;

            if (_hasHandle)
            {
                _handle.Dispose();
                _handle = default;
                _hasHandle = false;
            }

            if (_ov != null)
            {
                NativeMemory.Free(_ov);
                _ov = null;
            }

            _fileHandle = 0;
            _cursor = null;
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

/// <summary>IOCP 操作失败时抛出的异常，<see cref="Error"/> 为 Win32 错误码。</summary>
internal sealed class IocpException(int error) : System.IO.IOException($"IOCP operation failed: win32 error {error}")
{
    internal int Error { get; } = error;
}
