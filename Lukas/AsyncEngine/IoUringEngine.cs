// Lukas/AsyncEngine/IoUringEngine.cs

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Lukas.Interop.Unix.Linux;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace Lukas.AsyncEngine;

/// <summary>
/// Linux 上基于 io_uring 的异步 I/O 引擎，通过原生共享库 <c>uring_io</c> 提交操作。
///
/// 工作方式：每次提交生成一个递增的 token，并把对应的 <see cref="Operation"/> 登记进
/// <c>_pending</c> 字典；原生侧完成后在它自己的 I/O 线程上回调 <see cref="OnCompletion"/>，
/// 据 token 找回 <see cref="Operation"/> 并兑现其结果。<see cref="Operation"/> 实现了
/// <see cref="IValueTaskSource{T}"/> 并被对象池复用，避免每次 I/O 都分配。
///
/// 要求内核 ≥ 6.2，且原生 <c>uring_io</c> 库可用，否则构造抛异常（由 <c>AsyncEngineFactory</c> 回落到其它引擎）。
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class IoUringEngine : IAsyncIoEngine
{
    private const uint DefaultQueueDepth = 256;

    private readonly nint _context; // 原生 uring 上下文指针
    private GCHandle _self; // 传给原生回调的弱句柄，用来在回调里找回本实例

    private readonly ConcurrentDictionary<ulong, Operation> _pending = new(); // token → 进行中的操作
    private readonly ConcurrentQueue<Operation> _pool = new(); // 可复用的 Operation 对象池

    private long _tokenCounter;
    private int _disposed;

    // ---- 固定缓冲区池（io_uring IORING_REGISTER_BUFFERS 提前注册）----
    // 设计：引擎自有一组对齐的匿名原生内存块，启用时一次性注册进内核。读/写时若调用方使用
    // 的缓冲恰好落在某个已注册块内（按指针区间判定），自动改走 READ_FIXED/WRITE_FIXED 快路径，
    // 省去内核每次提交的页表锁定（get_user_pages）开销；否则原样回退到 Pin()+普通读写。
    // 全程不改变 IAsyncIoEngine 公共契约，对调用方透明。
    private FixedBufferRegistration? _fixedBuffers;

    private static bool IsSupported => KernelChecker.IsKernelVersionAtLeast62();
    
    /// <summary>
    /// 创建引擎并启动原生 io_uring 上下文。<paramref name="queueDepth"/> 为提交队列深度。
    /// 平台不满足（内核过旧）抛 <see cref="PlatformNotSupportedException"/>；原生创建失败抛 <see cref="UringException"/>。
    /// </summary>
    public IoUringEngine(uint queueDepth = DefaultQueueDepth)
    {
#if DEBUG
        Log.Info("Execute io_uring constructor!");
#endif
        
        if (queueDepth == 0)
            throw new ArgumentOutOfRangeException(nameof(queueDepth), queueDepth, "Queue depth must be greater than 0.");

        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "IoUringEngine requires Linux kernel 6.2 or higher with io_uring enabled.");
        
        // 用弱句柄登记自身：既能在原生回调里取回实例，又不至于让引擎被句柄强引用而无法回收。
        _self = GCHandle.Alloc(this, GCHandleType.Weak);

        // 取静态回调的函数指针交给原生侧；它会在每次操作完成时回调。
        var cb = (nint)(delegate* unmanaged[Cdecl]<ulong, int, void*, void>)&OnCompletion;

        nint ctx;
        try
        {
#if DEBUG
            Log.Info("Execute io_uring create!");
#endif
            ctx = IoUring.UringCreate(queueDepth, cb, GCHandle.ToIntPtr(_self));
        }
        catch (Exception ex)
        {
#if DEBUG
            Log.Error("io_uring creation failed!");
            Log.Error(ex.ToString());
            if (ex is UringException ue)
                Log.Error($"errno = {ue.Errno}");
#endif
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

    /// <summary>
    /// 启用并提前注册一组固定缓冲区（io_uring <c>IORING_REGISTER_BUFFERS</c>）。
    /// 注册后，凡是使用这些缓冲区（通过 <see cref="RentFixedBuffer"/> 取得）发起的读/写，
    /// 都会自动走 <c>READ_FIXED</c>/<c>WRITE_FIXED</c> 快路径，省去内核每次提交的页表锁定开销。
    /// 高 IOPS、小块（如 4 KiB）场景收益最明显。
    /// </summary>
    /// <param name="bufferCount">缓冲块数量（即可同时在途的固定缓冲数）。</param>
    /// <param name="bufferSize">每块字节数（建议页对齐，如 4096 的整数倍）。</param>
    /// <returns>注册成功返回 true；内核不支持或资源受限（如 RLIMIT_HEMLOCK）返回 false，
    /// 此时引擎仍可正常工作，只是不会走固定缓冲快路径。</returns>
    /// <remarks>应在引擎刚创建、尚无在途 I/O 时调用一次。重复调用将抛出 <see cref="InvalidOperationException"/>。</remarks>
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

    /// <summary>
    /// 从已注册的固定缓冲池租一块缓冲，返回的 <see cref="Memory{T}"/> 背靠原生内存，
    /// 用它发起的读/写会自动命中 <c>READ_FIXED</c>/<c>WRITE_FIXED</c> 快路径。
    /// 用完必须调用 <see cref="ReturnFixedBuffer"/> 归还。池空或未启用时返回 false。
    /// </summary>
    public bool RentFixedBuffer(out Memory<byte> buffer, out int bufferId)
    {
        var fb = _fixedBuffers;
        if (fb is not null && fb.TryRent(out buffer, out bufferId))
            return true;

        buffer = default;
        bufferId = -1;
        return false;
    }

    /// <summary>归还 <see cref="RentFixedBuffer"/> 取得的固定缓冲块。</summary>
    public void ReturnFixedBuffer(int bufferId)
        => _fixedBuffers?.Return(bufferId);

    /// <summary>
    /// 异步打开文件。四个提交方法（Open/Read/Write/Close）形状相同：先取一个池化
    /// <see cref="Operation"/>，已取消则就地完成；否则分配 token、登记进 <c>_pending</c>、注册取消，
    /// 再调用原生提交接口。原生返回值 &lt; 0 属于「提交阶段」的同步失败，此时直接撤销登记并就地失败；
    /// 真正的 I/O 结果一律由 <see cref="OnCompletion"/> 异步送达。
    /// </summary>
    public ValueTask<int> OpenAsync(
        ReadOnlySpan<byte> path,
        Flags flags,
        uint permission = 0x1A4,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute io_uring open!");
#endif
        
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
        
#if DEBUG
        Log.Info("Waiting for io_uring's open!");
#endif
        return op.AsValueTask();
    }
    
    /// <summary>异步接受连接,返回新连接的 fd。addr/addrLen 缓冲区在完成回调前保持有效。</summary>
    public ValueTask<int> AcceptAsync(int fd, nint addr, nint addrLen, uint acceptFlags = 0,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute io_uring accept!");
#endif
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

#if DEBUG
        Log.Info("Waiting for io_uring's accept!");
#endif
        return op.AsValueTask();
    }

    /// <summary>从 <paramref name="offset"/> 异步读取。缓冲区会被固定（pin），直到完成回调到来才释放。</summary>
    public ValueTask<int> ReadAsync(int fd, Memory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute io_uring read!");
#endif
        
        ThrowIfDisposed();

        var op = RentOperation();

        if (cancellationToken.IsCancellationRequested)
        {
            op.CompleteCanceled(cancellationToken);
            return op.AsValueTask();
        }

        // 固定缓冲区，确保原生读取期间内存地址稳定；句柄由 Operation 持有，完成时统一释放。
        var handle = buffer.Pin();
        op.SetHandle(handle);

        var token = op.Token = NextToken();
        _pending[token] = op;
        
        op.MarkAwaitingKernel();
        RegisterCancellation(op, token, cancellationToken);

        // 快路径：若该缓冲落在已注册的固定缓冲区内，走 READ_FIXED（省去内核每次锁页）。
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

#if DEBUG
        Log.Info("Waiting for io_uring's read!");
#endif
        return op.AsValueTask();
    }

    /// <summary>把 <paramref name="buffer"/> 从 <paramref name="offset"/> 异步写入；缓冲区在完成回调前保持固定。</summary>
    public ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute io_uring write!");
#endif
        
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

        // 快路径：命中已注册固定缓冲区则走 WRITE_FIXED。
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

#if DEBUG
        Log.Info("Waiting for io_uring's write!");
#endif
        return op.AsValueTask();
    }

    /// <summary>异步关闭 fd。</summary>
    public ValueTask CloseAsync(int fd, CancellationToken cancellationToken = default)
    {
#if DEBUG
        Log.Info("Execute io_uring close!");
#endif
        
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

#if DEBUG
        Log.Info("Waiting for io_uring's close!");
#endif
        return op.AsVoidValueTask();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DisposeCore();
    }

    ~IoUringEngine() => DisposeCore();

    // 真正的释放逻辑。用 Interlocked 保证只执行一次（Dispose 与终结器可能竞争）。
    // 销毁原生上下文会等后台 I/O 线程跑完；之后把仍滞留在 _pending 里的操作统一以取消收尾。
    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 先销毁上下文：UringDestroy 会停掉完成线程并保证内核不再有在途请求触碰任何缓冲。
        if (_context != 0)
            IoUring.UringDestroy(_context);

        // 上下文销毁后，固定缓冲区已无任何内核引用，此时注销并释放原生内存才安全。
        // （ring fd 已关闭，注销系统调用会失败也无妨，关键是把原生内存 free 掉。）
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

    // 原生侧的完成回调，运行在 uring_io 库的 I/O 线程上（非托管栈）。
    // 通过 userdata 里的弱句柄取回引擎实例，按 token 找到对应操作并兑现结果。
    // 关键约束：异常绝不能逸出到原生代码，因此整体包在 try/catch 里。
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCompletion(ulong token, int result, void* userdata)
    {
        try
        {
            var gch = GCHandle.FromIntPtr((nint)userdata);
            if (gch.Target is IoUringEngine engine && engine._pending.TryRemove(token, out var op))
                op.CompleteFromKernel(result);
        }
        catch
        {
            // The completion callback runs on the local I/O thread, and exceptions must never escape into native code.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private ulong NextToken() => (ulong)Interlocked.Increment(ref _tokenCounter);
    
    // 注册取消回调：取消触发时把操作标记为已取消（与内核完成竞争，由 Operation 内部的闩锁仲裁）。
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

    /// <summary>
    /// 承载一组已注册固定缓冲区的原生内存与空闲管理。每块缓冲是独立的页对齐匿名原生内存，
    /// 在创建时一次性通过 <c>IORING_REGISTER_BUFFERS</c> 注册；<see cref="TryMapToIndex"/>
    /// 按指针区间把任意（已 Pin 的）地址映射回它所属的 buf_index，从而判定能否走 FIXED 快路径。
    /// </summary>
    private sealed class FixedBufferRegistration
    {
        private readonly nint[] _bases;       // 每块缓冲的起始地址
        private readonly NativeFixedMemoryManager[] _managers; // 每块对应的 MemoryManager（预建，复用）
        private readonly int _bufferSize;
        private readonly nint _baseLow;        // 所有块的最小地址（区间命中初筛）
        private readonly nint _baseHigh;       // 所有块的最大结束地址
        private readonly bool _contiguous;     // 是否一整块连续分配（命中判定可 O(1)）
        private readonly nint _contiguousBase; // 连续分配时的总起始地址

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

        /// <summary>
        /// 分配 <paramref name="bufferCount"/> 块、每块 <paramref name="bufferSize"/> 字节的页对齐
        /// 匿名原生内存并注册。成功返回实例，失败（注册被内核拒绝等）返回 null 并回滚已分配内存。
        /// </summary>
        internal static FixedBufferRegistration? TryCreate(nint context, int bufferCount, int bufferSize)
        {
            // 一整块连续分配，按 bufferSize 切片 —— 既减少碎片，也让命中判定 O(1)。
            // 页对齐确保符合内核对固定缓冲的要求，并利于 O_DIRECT。
            const nuint alignment = 4096;
            var total = (nuint)bufferSize * (nuint)bufferCount;

            var block = (byte*)NativeMemory.AlignedAlloc(total, alignment);
            if (block is null)
                return null;

            var bases = new nint[bufferCount];

            // iovec 数组：小数量走栈，大数量走原生堆，避免栈溢出。
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

        /// <summary>
        /// 把一个已固定的地址 + 长度映射到它所属的 buf_index。命中（完全落在某块内）返回 true。
        /// 连续分配下用一次除法即可定位，再校验未跨块、未越界。
        /// </summary>
        internal bool TryMapToIndex(void* ptr, int length, out ushort bufIndex)
        {
            bufIndex = 0;
            var addr = (nint)ptr;

            // 初筛：落在总区间外直接否决。
            if (addr < _baseLow || addr >= _baseHigh || length <= 0)
                return false;

            if (_contiguous)
            {
                var delta = (long)(addr - _contiguousBase);
                var idx = delta / _bufferSize;
                var within = delta - idx * _bufferSize;
                // 必须整体落在单一块内：起点在块内且 [起点, 起点+length) 不越过块尾。
                if (within + length > _bufferSize || (uint)idx >= (uint)_bases.Length)
                    return false;
                bufIndex = (ushort)idx;
                return true;
            }

            // 非连续布局：线性查找（块数通常很小）。
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

            // best-effort 注销（ring fd 可能已随上下文销毁而关闭，失败无妨）。
            try { IoUring.UringUnregisterBuffers(context); } catch { /* ignore */ }

            // 释放整块连续原生内存。
            if (_contiguous && _contiguousBase != 0)
                NativeMemory.AlignedFree((void*)_contiguousBase);
        }
    }

    /// <summary>把一段固定的原生内存包装成 <see cref="Memory{T}"/>。内存生命周期由
    /// <see cref="FixedBufferRegistration"/> 管理，这里不负责释放，Pin 直接返回稳定地址。</summary>
    private sealed class NativeFixedMemoryManager(byte* ptr, int length)
        : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new(ptr, length);

        public override MemoryHandle Pin(int elementIndex = 0)
            => new(ptr + elementIndex);

        public override void Unpin() { /* 原生内存常驻，无需操作 */ }

        protected override void Dispose(bool disposing) { /* 内存由注册器统一释放 */ }
    }

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
    
    /// <summary>
    /// 一次异步操作的载体，同时充当 <see cref="IValueTaskSource{T}"/> 让 <c>await</c> 拿到结果。
    /// 实例由 <see cref="_pool"/> 复用，因此正确的「何时归还」是这里的核心。
    ///
    /// 并发要点：
    /// <list type="bullet">
    /// <item>结果可能来自两个互斥的来源——内核完成回调，或取消触发。用 <c>_resultLatch</c>
    /// 这把一次性闩锁（<see cref="TryLatch"/>）保证只有最先到达者真正写入结果。</item>
    /// <item>对象要安全复用，必须等「内核侧已不再触碰它」且「await 一方已取走结果」两件事都发生。
    /// 用 <c>_recycleGate</c> 计数：两边各 +1，到 2 时才 <see cref="RecycleNow"/> 归还池中。</item>
    /// </list>
    /// </summary>
    private sealed class Operation : IValueTaskSource<int>, IValueTaskSource
    {
        private readonly IoUringEngine _owner;

        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };

        private MemoryHandle _handle;   // 固定缓冲区的句柄（读/写时持有）
        private bool _hasHandle;
        
        private int _resultLatch;       // 结果闩锁：0 未定，1 已被某一方占用

        private int _recycleGate;       // 回收门：累计到 2（内核侧 + 消费侧）才可归还

        private bool _awaitingKernel;   // 是否已把请求交给内核（决定回收需不需要等两边）

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
        
        // 抢占结果写入权：返回 true 表示本次调用是第一个到达者，可以设置结果。
        private bool TryLatch() => Interlocked.Exchange(ref _resultLatch, 1) == 0;

        // 内核完成：设置结果（负值转成异常）、释放资源，并推进回收门。
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
        
        // 取消触发：仅设置异常。注意此时请求可能仍在内核中，故不释放资源、也不推进回收门，
        // 留给随后必然到来的内核完成回调去收尾，避免缓冲区在内核还在用时被提前释放。
        internal void CompleteCanceled(CancellationToken token)
        {
            if (TryLatch())
                _core.SetException(new OperationCanceledException(token));
        }
        
        // 提交阶段同步失败（请求根本没进内核）：设置结果并立即收尾。
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
        
        // 回收门 +1；当内核侧与消费侧都到齐（计数达 2）时执行归还。
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
        
        // await 取走结果后调用：若请求从未交给内核（如就地取消/失败），可直接归还；
        // 否则只推进回收门，等内核侧那一票到齐再归还。
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
