using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace FileTransfer;

[SupportedOSPlatform("linux")]
public sealed unsafe class IoUringEngine
{
    private const uint DefaultQueueDepth = 256;

    private readonly nint _context;
    private GCHandle _self;

    private readonly ConcurrentDictionary<ulong, Operation> _pending = new();
    private readonly ConcurrentQueue<Operation> _pool = new();

    private long _tokenCounter;
    private int _disposed;

    private FixedBufferRegistration? _fixedBuffers;

    private const uint SpliceFMove = 1;

    public IoUringEngine(uint queueDepth = DefaultQueueDepth)
    {
        if (queueDepth == 0)
            throw new ArgumentOutOfRangeException(nameof(queueDepth), queueDepth, "Queue depth must be greater than 0.");

        _self = GCHandle.Alloc(this, GCHandleType.Weak);

        var cb = (nint)(delegate* unmanaged[Cdecl]<ulong, int, void*, void>)&OnCompletion;

        nint ctx;
        try
        {
            ctx = IoUring.UringCreate(queueDepth, cb, GCHandle.ToIntPtr(_self));
        }
        catch (Exception)
        {
            _self.Free();
            throw;
        }

        if (ctx == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            _self.Free();
            throw new UringException(err);
        }

        _context = ctx;
    }

    public bool EnableFixedBuffers(int bufferCount, int bufferSize)
    {
        ThrowIfDisposed();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        if (_fixedBuffers is not null)
            throw new InvalidOperationException("Fixed buffers are already enabled.");

        var reg = FixedBufferRegistration.TryCreate(_context, bufferCount, bufferSize);
        if (reg is null)
            return false;

        _fixedBuffers = reg;
        return true;
    }

    public bool RentFixedBuffer(out Memory<byte> buffer, out int bufferId)
    {
        var fb = _fixedBuffers;
        if (fb is not null && fb.TryRent(out buffer, out bufferId))
            return true;

        buffer = default;
        bufferId = -1;
        return false;
    }

    public void ReturnFixedBuffer(int bufferId)
        => _fixedBuffers?.Return(bufferId);

    public ValueTask<int> SpliceAsync(
        int fdOut,
        long offOut,
        int fdIn,
        long offIn,
        uint len,
        uint spliceFlags = SpliceFMove,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        var token = op.Token = NextToken();
        _pending[token] = op;

        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        var rc = IoUring.UringSplice(_context, token, fdOut, offOut, fdIn, offIn, len, spliceFlags);

        if (rc < 0)
        {
            _pending.TryRemove(token, out _);
            op.CompleteSubmitFailure(rc);
        }
        else
        {
            IoUring.AutoFlush(_context);
        }

        return op.AsValueTask();
    }

    public ValueTask<int> OpenAsync(
        ReadOnlySpan<byte> path,
        Flags flags,
        uint permission = 0x1A4,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        var oflag = ResolveOpenFlags(flags);

        var token = op.Token = NextToken();
        _pending[token] = op;

        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        int rc;
        fixed (byte* p = path)
        {
            rc = IoUring.UringOpen(_context, token, p, oflag, permission);
        }

        if (rc < 0)
        {
            _pending.TryRemove(token, out _);
            op.CompleteSubmitFailure(rc);
        }
        else
        {
            IoUring.AutoFlush(_context);
        }

        return op.AsValueTask();
    }

    public ValueTask<int> AcceptAsync(int fd, nint addr, nint addrLen, uint acceptFlags = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        var token = op.Token = NextToken();
        _pending[token] = op;

        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        var rc = IoUring.UringAccept(_context, token, fd, (void*)addr, (void*)addrLen, acceptFlags);

        if (rc < 0)
        {
            _pending.TryRemove(token, out _);
            op.CompleteSubmitFailure(rc);
        }
        else
        {
            IoUring.AutoFlush(_context);
        }

        return op.AsValueTask();
    }

    public ValueTask<int> ReadAsync(int fd, Memory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        var handle = buffer.Pin();
        op.SetHandle(handle);

        var token = op.Token = NextToken();
        _pending[token] = op;

        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        int rc;
        var fb = _fixedBuffers;
        if (fb is not null && fb.TryMapToIndex(handle.Pointer, buffer.Length, out var bufIndex))
            rc = IoUring.UringReadFixed(_context, token, fd, handle.Pointer, (nuint)buffer.Length, offset, bufIndex);
        else
            rc = IoUring.UringRead(_context, token, fd, handle.Pointer, (nuint)buffer.Length, offset);

        if (rc < 0)
        {
            _pending.TryRemove(token, out _);
            op.CompleteSubmitFailure(rc);
        }
        else
        {
            IoUring.AutoFlush(_context);
        }

        return op.AsValueTask();
    }

    public ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        var handle = buffer.Pin();
        op.SetHandle(handle);

        var token = op.Token = NextToken();
        _pending[token] = op;

        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        int rc;
        var fb = _fixedBuffers;
        if (fb is not null && fb.TryMapToIndex(handle.Pointer, buffer.Length, out var bufIndex))
            rc = IoUring.UringWriteFixed(_context, token, fd, handle.Pointer, (nuint)buffer.Length, offset, bufIndex);
        else
            rc = IoUring.UringWrite(_context, token, fd, handle.Pointer, (nuint)buffer.Length, offset);

        if (rc < 0)
        {
            _pending.TryRemove(token, out _);
            op.CompleteSubmitFailure(rc);
        }
        else
        {
            IoUring.AutoFlush(_context);
        }

        return op.AsValueTask();
    }

    public ValueTask CloseAsync(int fd, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsVoidValueTask();
        }

        var token = op.Token = NextToken();
        _pending[token] = op;

        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        var rc = IoUring.UringClose(_context, token, fd);

        if (rc < 0)
        {
            _pending.TryRemove(token, out _);
            op.CompleteSubmitFailure(rc);
        }
        else
        {
            IoUring.AutoFlush(_context);
        }

        return op.AsVoidValueTask();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DisposeCore();
    }

    ~IoUringEngine() => DisposeCore();

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_context != 0)
            IoUring.UringDestroy(_context);

        _fixedBuffers?.Dispose(_context);
        _fixedBuffers = null;

        if (_self.IsAllocated)
            _self.Free();

        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var op))
                op.CompleteFromKernel(-Ecanceled);
        }
    }

    private const int Ecanceled = 125;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCompletion(ulong token, int result, void* userdata)
    {
        try
        {
            var gch = GCHandle.FromIntPtr((nint)userdata);
            if (gch.Target is IoUringEngine engine && engine._pending.TryRemove(token, out var op))
                op.CompleteFromKernel(result);
        }
        catch { /* */ }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private ulong NextToken() => (ulong)Interlocked.Increment(ref _tokenCounter);

    private static void RegisterCancellation(Operation op, ulong token, CancellationToken cancellationToken)
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

    // ==================== FixedBufferRegistration（完整实现） ====================
    private sealed class FixedBufferRegistration
    {
        private readonly nint[] _bases;
        private readonly NativeFixedMemoryManager[] _managers;
        private readonly int _bufferSize;
        private readonly nint _baseLow;
        private readonly nint _baseHigh;
        private readonly bool _contiguous;
        private readonly nint _contiguousBase;

        private readonly ConcurrentBag<int> _free = [];
        private int _disposed;

        private FixedBufferRegistration(nint[] bases, int bufferSize, bool contiguous, nint contiguousBase)
        {
            _bases = bases;
            _bufferSize = bufferSize;
            _contiguous = contiguous;
            _contiguousBase = contiguousBase;

            _managers = new NativeFixedMemoryManager[bases.Length];
            for (var i = 0; i < bases.Length; i++)
                _managers[i] = new NativeFixedMemoryManager((byte*)bases[i], bufferSize);

            var low = bases[0];
            var high = bases[0] + bufferSize;
            foreach (var b in bases)
            {
                if (b < low) low = b;
                if (b + bufferSize > high) high = b + bufferSize;
            }
            _baseLow = low;
            _baseHigh = high;

            for (var i = 0; i < bases.Length; i++)
                _free.Add(i);
        }

        internal static FixedBufferRegistration? TryCreate(nint context, int bufferCount, int bufferSize)
        {
            const nuint alignment = 4096;
            var total = (nuint)bufferSize * (nuint)bufferCount;

            var block = (byte*)NativeMemory.AlignedAlloc(total, alignment);
            if (block is null)
                return null;

            var bases = new nint[bufferCount];

            const int stackLimit = 128;
            IoUring.Iovec* iovecs;
            IoUring.Iovec* iovecsHeap = null;
            var iovecsStack = stackalloc IoUring.Iovec[bufferCount <= stackLimit ? bufferCount : 1];
            if (bufferCount <= stackLimit)
            {
                iovecs = iovecsStack;
            }
            else
            {
                iovecsHeap = (IoUring.Iovec*)NativeMemory.Alloc((nuint)bufferCount * (nuint)sizeof(IoUring.Iovec));
                iovecs = iovecsHeap;
            }

            for (var i = 0; i < bufferCount; i++)
            {
                var p = block + (nuint)i * (nuint)bufferSize;
                bases[i] = (nint)p;
                iovecs[i].Base = p;
                iovecs[i].Len = (nuint)bufferSize;
            }

            var rc = IoUring.UringRegisterBuffers(context, iovecs, (uint)bufferCount);

            if (iovecsHeap is not null)
                NativeMemory.Free(iovecsHeap);

            if (rc < 0)
            {
                NativeMemory.AlignedFree(block);
                return null;
            }

            return new FixedBufferRegistration(bases, bufferSize, contiguous: true, contiguousBase: (nint)block);
        }

        internal bool TryRent(out Memory<byte> buffer, out int bufferId)
        {
            if (_free.TryTake(out var id))
            {
                buffer = _managers[id].Memory;
                bufferId = id;
                return true;
            }

            buffer = default;
            bufferId = -1;
            return false;
        }

        internal void Return(int bufferId)
        {
            if ((uint)bufferId < (uint)_bases.Length)
                _free.Add(bufferId);
        }

        internal bool TryMapToIndex(void* ptr, int length, out ushort bufIndex)
        {
            bufIndex = 0;
            var addr = (nint)ptr;

            if (addr < _baseLow || addr >= _baseHigh || length <= 0)
                return false;

            if (_contiguous)
            {
                var delta = (long)(addr - _contiguousBase);
                var idx = delta / _bufferSize;
                var within = delta - idx * _bufferSize;
                if (within + length > _bufferSize || (uint)idx >= (uint)_bases.Length)
                    return false;
                bufIndex = (ushort)idx;
                return true;
            }

            for (var i = 0; i < _bases.Length; i++)
            {
                var b = _bases[i];
                if (addr >= b && addr + length <= b + _bufferSize)
                {
                    bufIndex = (ushort)i;
                    return true;
                }
            }
            return false;
        }

        internal void Dispose(nint context)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                IoUring.UringUnregisterBuffers(context);
            }
            catch
            {
                //
            }

            if (_contiguous && _contiguousBase != 0)
                NativeMemory.AlignedFree((void*)_contiguousBase);
        }
    }

    private sealed class NativeFixedMemoryManager(byte* ptr, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new(ptr, length);

        public override MemoryHandle Pin(int elementIndex = 0)
            => new(ptr + elementIndex);

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    // ==================== Operation（完整实现） ====================
    private sealed class Operation : IValueTaskSource<int>, IValueTaskSource
    {
        private readonly IoUringEngine _owner;

        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };

        private MemoryHandle _handle;
        private bool _hasHandle;

        private int _resultLatch;
        private int _recycleGate;
        private bool _awaitingKernel;

        private CancellationTokenRegistration _ctr;

        internal ulong Token;

        internal Operation(IoUringEngine owner) => _owner = owner;

        internal void SetHandle(MemoryHandle handle)
        {
            _handle = handle;
            _hasHandle = true;
        }

        internal void SetCancellationRegistration(CancellationTokenRegistration ctr) => _ctr = ctr;

        internal void MarkAwaitingKernel() => _awaitingKernel = true;

        private bool TryLatch() => Interlocked.Exchange(ref _resultLatch, 1) == 0;

        internal void CompleteFromKernel(int result)
        {
            if (TryLatch())
            {
                if (result < 0)
                    _core.SetException(new UringException(-result));
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

        internal void CompleteSubmitFailure(int result)
        {
            if (TryLatch())
            {
                if (result < 0)
                    _core.SetException(new UringException(-result));
                else
                    _core.SetResult(result);
            }

            ReleaseResources();
            AdvanceRecycle();
        }

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
        }

        private void AdvanceRecycle()
        {
            if (Interlocked.Increment(ref _recycleGate) == 2)
                RecycleNow();
        }

        private void RecycleNow()
        {
            Token = 0;
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
            try { return _core.GetResult(token); }
            finally { OnResultConsumed(); }
        }

        void IValueTaskSource.GetResult(short token)
        {
            try { _core.GetResult(token); }
            finally { OnResultConsumed(); }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    private static int ResolveOpenFlags(Flags palFlags)
    {
        var flags = palFlags switch
        {
            Flags.CreateNew => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OExcl | Sys.OpenFlags.OCloexec,
            Flags.Create => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OTrunc | Sys.OpenFlags.OCloexec,
            Flags.Open => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCloexec,
            Flags.OpenOrCreate => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OCloexec,
            Flags.Truncate => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OTrunc | Sys.OpenFlags.OCloexec,
            Flags.Append => Sys.OpenFlags.OWronly | Sys.OpenFlags.OCreat | Sys.OpenFlags.OAppend | Sys.OpenFlags.OCloexec,
            Flags.Read => Sys.OpenFlags.ORdonly | Sys.OpenFlags.OCloexec,
            _ => Sys.OpenFlags.ORdwr | Sys.OpenFlags.OCreat | Sys.OpenFlags.OCloexec
        };

        return Sys.ResolveOpenFlags(flags);
    }
}
