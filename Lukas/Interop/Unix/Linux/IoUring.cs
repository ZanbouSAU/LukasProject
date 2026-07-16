// Lukas/Interop/Unix/Linux/IoUring.cs

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Lukas.Std;

namespace Lukas.Interop.Unix.Linux;

/// <summary>io_uring 原生操作失败时抛出的异常，<see cref="Errno"/> 为对应的 errno。</summary>
internal sealed class UringException(int errno) : IOException($"io_uring operation failed: errno {errno}")
{
    internal int Errno { get; } = errno;
}

/// <summary>
/// 纯 C# 实现的 io_uring 驱动，直接架在 <see cref="SysLinux"/> 的原始系统调用
/// （io_uring_setup / io_uring_enter / mmap）之上，<b>不依赖 liburing</b>。
///
/// 对外保持与原生薄封装相同的 6 个入口（Create/Destroy/Open/Read/Write/Close），因此
/// <c>IoUringEngine</c> 可零改动接入。语义约定：
/// <list type="bullet">
/// <item><see cref="UringCreate"/> 返回一个不透明上下文句柄；失败抛 <see cref="UringException"/>。</item>
/// <item>四个提交方法返回 0 表示已入队，返回负值（<c>-errno</c>）表示「提交阶段」同步失败；
/// 真正的 I/O 结果一律经创建时传入的回调 <c>cb(token, result, userdata)</c> 异步送达。</item>
/// <item><see cref="UringDestroy"/> 会先停掉后台完成线程再清理，返回后保证不再有回调发生。</item>
/// </list>
/// 回调签名为 <c>delegate* unmanaged[Cdecl]&lt;ulong, int, void*, void&gt;</c>（token, result, userdata）。
/// </summary>
internal static unsafe class IoUring
{
    // ---- io_uring 操作码（linux/io_uring.h）----
    private const byte OpNop = 0;
    private const byte OpReadFixed = 4;   // IORING_OP_READ_FIXED：使用已注册固定缓冲区的读
    private const byte OpWriteFixed = 5;  // IORING_OP_WRITE_FIXED：使用已注册固定缓冲区的写
    private const byte OpAccept = 13;
    private const byte OpOpenat = 18;
    private const byte OpClose = 19;
    private const byte OpRead = 22;
    private const byte OpWrite = 23;

    // ---- io_uring_register 操作码（IORING_REGISTER_*）----
    private const uint RegRegisterBuffers = 0;    // IORING_REGISTER_BUFFERS
    private const uint RegUnregisterBuffers = 1;   // IORING_UNREGISTER_BUFFERS

    // ---- mmap 偏移（IORING_OFF_*）----
    private const long OffSqRing = 0L;
    private const long OffCqRing = 0x8000000L;
    private const long OffSqes = 0x10000000L;

    // ---- io_uring_enter 标志 / setup 特性 ----
    private const uint EnterGetEvents = 1u;
    private const uint FeatSingleMmap = 1u; // IORING_FEAT_SINGLE_MMAP

    // ---- mmap prot/flags ----
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapShared = 0x1;
    private const int MapPopulate = 0x8000;

    // ---- 其它常量 ----
    private const int AtFdCwd = -100;
    private const int Eintr = 4;
    private const int Eagain = 11;
    private const ulong WakeToken = ulong.MaxValue; // 关闭时用于唤醒完成线程的 NOP 哨兵 token

    /// <summary>创建 io_uring 上下文并启动后台完成线程。失败抛 <see cref="UringException"/>。</summary>
    internal static nint UringCreate(uint queueDepth, nint cb, nint userdata)
    {
        var ctx = new UringContext(queueDepth, cb, userdata);
        return ctx.SelfPtr;
    }

    /// <summary>销毁上下文：停止完成线程、解除映射、关闭 ring fd、释放句柄。</summary>
    internal static void UringDestroy(nint ctx)
    {
#if DEBUG
        Io.Println("Execute io_uring UringDestroy method!");
        Io.FlushOut();
#endif
        if (ctx == 0)
            return;

        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.Target is UringContext context)
            context.Dispose();
    }

    internal static int UringOpen(nint ctx, ulong token, byte* path, int oflag, uint mode)
        => Context(ctx).Submit(OpOpenat, AtFdCwd, off: 0, addr: (ulong)path, len: mode, opFlags: (uint)oflag, token);
    
    internal static int UringAccept(nint ctx, ulong token, int fd, void* addr, void* addrLen, uint acceptFlags)
        => Context(ctx).Submit(OpAccept, fd, off: (ulong)addrLen, addr: (ulong)addr, len: 0, opFlags: acceptFlags, token);

    internal static int UringRead(nint ctx, ulong token, int fd, void* buf, nuint nbytes, long offset)
        => Context(ctx).Submit(OpRead, fd, off: (ulong)offset, addr: (ulong)buf, len: (uint)nbytes, opFlags: 0, token);

    internal static int UringWrite(nint ctx, ulong token, int fd, void* buf, nuint nbytes, long offset)
        => Context(ctx).Submit(OpWrite, fd, off: (ulong)offset, addr: (ulong)buf, len: (uint)nbytes, opFlags: 0, token);

    /// <summary>
    /// 用已注册固定缓冲区发起读（IORING_OP_READ_FIXED）。<paramref name="buf"/> 必须落在
    /// <paramref name="bufIndex"/> 所注册区间内；内核免去每次提交的 get_user_pages 开销。
    /// </summary>
    internal static int UringReadFixed(nint ctx, ulong token, int fd, void* buf, nuint nbytes, long offset, ushort bufIndex)
        => Context(ctx).Submit(OpReadFixed, fd, off: (ulong)offset, addr: (ulong)buf, len: (uint)nbytes, opFlags: 0, token, bufIndex);

    /// <summary>用已注册固定缓冲区发起写（IORING_OP_WRITE_FIXED）。约束同 <see cref="UringReadFixed"/>。</summary>
    internal static int UringWriteFixed(nint ctx, ulong token, int fd, void* buf, nuint nbytes, long offset, ushort bufIndex)
        => Context(ctx).Submit(OpWriteFixed, fd, off: (ulong)offset, addr: (ulong)buf, len: (uint)nbytes, opFlags: 0, token, bufIndex);

    /// <summary>
    /// 提前注册一组固定缓冲区（IORING_REGISTER_BUFFERS）。<paramref name="iovecs"/> 指向
    /// <paramref name="count"/> 个 <see cref="Iovec"/>。成功返回 0，失败返回 <c>-errno</c>。
    /// 注册为同步系统调用，应在引擎初始化阶段、尚无在途 I/O 时调用。
    /// </summary>
    internal static int UringRegisterBuffers(nint ctx, Iovec* iovecs, uint count)
        => Context(ctx).RegisterBuffers(iovecs, count);

    /// <summary>注销之前注册的全部固定缓冲区（IORING_UNREGISTER_BUFFERS）。</summary>
    internal static int UringUnregisterBuffers(nint ctx)
        => Context(ctx).UnregisterBuffers();

    internal static int UringClose(nint ctx, ulong token, int fd)
        => Context(ctx).Submit(OpClose, fd, off: 0, addr: 0, len: 0, opFlags: 0, token);

    private static UringContext Context(nint ctx)
        => GCHandle.FromIntPtr(ctx).Target as UringContext
           ?? throw new ObjectDisposedException(nameof(IoUring));

    // ---- struct io_uring_sqe（64 字节，仅声明本驱动用到的字段）----
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct IoUringSqe
    {
        [FieldOffset(0)] public byte Opcode;
        [FieldOffset(1)] public byte Flags;
        [FieldOffset(2)] public ushort Ioprio;
        [FieldOffset(4)] public int Fd;
        [FieldOffset(8)] public ulong Off;       // 读/写偏移；openat 此处置 0
        [FieldOffset(16)] public ulong Addr;      // 读/写缓冲；openat 为路径指针
        [FieldOffset(24)] public uint Len;        // 读/写字节数；openat 为 mode
        [FieldOffset(28)] public uint OpFlags;    // rw_flags / open_flags
        [FieldOffset(32)] public ulong UserData;  // token
        [FieldOffset(40)] public ushort BufIndex; // READ_FIXED/WRITE_FIXED 的已注册缓冲索引（buf_index）
        // 42..63 其余 union 字段（personality/splice_fd_in/addr3/pad）保持 0
    }

    // ---- struct iovec（注册固定缓冲区用）----
    [StructLayout(LayoutKind.Sequential)]
    internal struct Iovec
    {
        public void* Base;   // iov_base
        public nuint Len;    // iov_len
    }

    // ---- struct io_uring_cqe（16 字节）----
    [StructLayout(LayoutKind.Sequential)]
    private struct IoUringCqe
    {
        public ulong UserData;
        public int Res;
        public uint Flags;
    }

    /// <summary>承载单个 io_uring 实例的全部内核资源与提交/完成逻辑。</summary>
    private sealed class UringContext
    {
        private readonly nint _cb;
        private readonly nint _userdata;
        private readonly object _submitLock = new();

        private int _ringFd = -1;

        private byte* _sqBase;
        private nuint _sqMapLen;
        private byte* _cqBase;
        private nuint _cqMapLen;   // 单映射模式下为 0（与 _sqBase 共用一块）
        private IoUringSqe* _sqes;
        private nuint _sqesMapLen;
        private bool _singleMmap;

        private uint* _sqHead;
        private uint* _sqTail;
        private uint* _sqArray;
        private uint _sqMask;
        private uint _sqEntries;
        private uint _sqTailLocal;

        private uint* _cqHead;
        private uint* _cqTail;
        private IoUringCqe* _cqes;
        private uint _cqMask;

        private readonly Thread? _completionThread;
        private volatile bool _running;
        private int _disposed;

        private GCHandle _selfHandle;

        internal nint SelfPtr => GCHandle.ToIntPtr(_selfHandle);

        internal UringContext(uint queueDepth, nint cb, nint userdata)
        {
            _cb = cb;
            _userdata = userdata;

            try
            {
                SetupAndMap(queueDepth);

                _running = true;
                _completionThread = new Thread(CompletionLoop)
                {
                    IsBackground = true,
                    Name = "io_uring-cq"
                };
                _completionThread.Start();
            }
            catch
            {
                CleanupNative();
                throw;
            }

            _selfHandle = GCHandle.Alloc(this);
        }

        private void SetupAndMap(uint queueDepth)
        {
#if DEBUG
            Log.Info("Execute io_uring setup and map method!");
#endif
            SysLinux.IoUringParams p = default;

            var fd = SysLinux.SysIoUringSetup(SysLinux.Setup, queueDepth, &p);
            if (fd < 0)
                throw new UringException(Marshal.GetLastSystemError());
            _ringFd = fd;

            _sqEntries = p.sq_entries;

            var sqRingBytes = (nuint)(p.sq_off.array + p.sq_entries * sizeof(uint));
            var cqRingBytes = (nuint)(p.cq_off.cqes + p.cq_entries * (uint)sizeof(IoUringCqe));

            _singleMmap = (p.features & FeatSingleMmap) != 0;

            if (_singleMmap)
            {
                var ringBytes = sqRingBytes > cqRingBytes ? sqRingBytes : cqRingBytes;
                var ptr = Map(ringBytes, OffSqRing);
                _sqBase = (byte*)ptr;
                _cqBase = (byte*)ptr;
                _sqMapLen = ringBytes;
                _cqMapLen = 0;
            }
            else
            {
                _sqBase = (byte*)Map(sqRingBytes, OffSqRing);
                _sqMapLen = sqRingBytes;
                _cqBase = (byte*)Map(cqRingBytes, OffCqRing);
                _cqMapLen = cqRingBytes;
            }

            var sqesBytes = (nuint)(p.sq_entries * (uint)sizeof(IoUringSqe));
            _sqes = (IoUringSqe*)Map(sqesBytes, OffSqes);
            _sqesMapLen = sqesBytes;

            _sqHead = (uint*)(_sqBase + p.sq_off.head);
            _sqTail = (uint*)(_sqBase + p.sq_off.tail);
            _sqMask = *(uint*)(_sqBase + p.sq_off.ring_mask);
            _sqArray = (uint*)(_sqBase + p.sq_off.array);
            _sqTailLocal = *_sqTail;

            _cqHead = (uint*)(_cqBase + p.cq_off.head);
            _cqTail = (uint*)(_cqBase + p.cq_off.tail);
            _cqMask = *(uint*)(_cqBase + p.cq_off.ring_mask);
            _cqes = (IoUringCqe*)(_cqBase + p.cq_off.cqes);
        }

        private void* Map(nuint length, long offset)
        {
#if DEBUG
            Log.Info("Execute io_uring mmap method!");
#endif
            var ptr = SysLinux.MMap(null, length, ProtRead | ProtWrite, MapShared | MapPopulate, _ringFd, offset);
            
            return (nint)ptr == -1
                ? throw new UringException(Marshal.GetLastSystemError())
                : ptr;
        }

        /// <summary>填充一个 SQE 并提交。线程安全：整段在 <see cref="_submitLock"/> 下串行化。</summary>
        internal int Submit(byte opcode, int fd, ulong off, ulong addr, uint len, uint opFlags, ulong token, ushort bufIndex = 0)
        {
            lock (_submitLock)
            {
#if DEBUG
                Log.Info("Execute io_uring submit method!");
#endif
                var head = Volatile.Read(ref *_sqHead);
                if (_sqTailLocal - head >= _sqEntries)
                    return -Eagain; // 提交队列已满（每次提交即 enter，正常情况下不会发生）

                var index = _sqTailLocal & _sqMask;
                var sqe = _sqes + index;

                *sqe = default;
                sqe->Opcode = opcode;
                sqe->Fd = fd;
                sqe->Off = off;
                sqe->Addr = addr;
                sqe->Len = len;
                sqe->OpFlags = opFlags;
                sqe->UserData = token;
                sqe->BufIndex = bufIndex; // 仅 READ_FIXED/WRITE_FIXED 有意义；其它操作恒为 0（已由 default 清零）

                _sqArray[index] = index;

                _sqTailLocal++;
                Volatile.Write(ref *_sqTail, _sqTailLocal);

                while (true)
                {
                    var ret = SysLinux.SysIoUringEnter(SysLinux.Enter, (uint)_ringFd, 1, 0, 0, null, 0);
                    if (ret >= 0)
                    {
#if DEBUG
                        Log.Info("io_uring submit successfully!");
#endif
                        return 0;
                    }

                    var err = Marshal.GetLastSystemError();
                    if (err == Eintr)
                    {
#if DEBUG
                        Io.Stderr.WriteLine("io_uring signal terminated!");
                        Io.Stderr.Flush();
#endif
                        continue; // 被信号打断，重试提交
                    }

#if DEBUG
                    Log.Error("io_uring error!");
#endif
                    return -err;
                }
            }
        }

        // 提前注册固定缓冲区。返回 0 成功，-errno 失败。被信号打断（EINTR）时重试。
        // 注意：内核要求注册时 ring 处于空闲（无在途请求），故只应在引擎初始化阶段调用。
        internal int RegisterBuffers(Iovec* iovecs, uint count)
        {
            while (true)
            {
                var ret = SysLinux.SysIoUringRegister(
                    SysLinux.Register, (uint)_ringFd, RegRegisterBuffers, iovecs, count);
                if (ret >= 0)
                    return 0;

                var err = Marshal.GetLastSystemError();
                if (err == Eintr)
                    continue;
                return -err;
            }
        }

        internal int UnregisterBuffers()
        {
            while (true)
            {
                var ret = SysLinux.SysIoUringRegister(
                    SysLinux.Register, (uint)_ringFd, RegUnregisterBuffers, null, 0);
                if (ret >= 0)
                    return 0;

                var err = Marshal.GetLastSystemError();
                if (err == Eintr)
                    continue;
                return -err;
            }
        }

        // 后台完成线程：阻塞等待至少一个完成项，逐个取出 CQE 并回调；收到关闭信号后退出。
        private void CompletionLoop()
        {
#if DEBUG
            Log.Info("Execute io_uring CompletionLoop method!");
#endif
            while (true)
            {
                var ret = SysLinux.SysIoUringEnter(SysLinux.Enter, (uint)_ringFd, 0, 1, EnterGetEvents, null, 0);
                if (ret < 0)
                {
                    var err = Marshal.GetLastSystemError();
                    if (err == Eintr)
                        continue;

                    // 其它错误：无论是否在关闭，都退出，避免空转。
                    break;
                }

                Reap();

                if (!_running)
                    break;
            }
        }

        private void Reap()
        {
#if DEBUG
            Log.Info("Execute io_uring Reap method!");
#endif
            var head = *_cqHead;
            var tail = Volatile.Read(ref *_cqTail);

            while (head != tail)
            {
                var cqe = _cqes + (head & _cqMask);
                var token = cqe->UserData;
                var res = cqe->Res;
                head++;

                if (token != WakeToken)
                    Invoke(token, res);
            }

            Volatile.Write(ref *_cqHead, head);
        }

        private void Invoke(ulong token, int result)
        {
#if DEBUG
            Log.Info("Execute io_uring Invoke method!");
#endif
            var fn = (delegate* unmanaged[Cdecl]<ulong, int, void*, void>)_cb;
            fn(token, result, (void*)_userdata);
        }

        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // 通知完成线程退出，并提交一个 NOP 把它从阻塞的 enter 中唤醒。
            _running = false;
            if (_completionThread is not null)
            {
                try
                {
                    Submit(OpNop, fd: -1, off: 0, addr: 0, len: 0, opFlags: 0, WakeToken);
                }
                catch
                {
                    // 即便唤醒提交失败也继续 Join；线程最终会因 ring fd 关闭而结束。
                }

                _completionThread.Join();
            }

            CleanupNative();

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }

        // 解除映射并关闭 ring fd。可在构造失败的部分初始化状态下安全调用（各字段带空判）。
        private void CleanupNative()
        {
#if DEBUG
            Log.Info("Execute io_uring close ring fd(MUnmap) method!");
#endif
            if (_sqes is not null)
            {
                SysLinux.MUnmap(_sqes, _sqesMapLen);
                _sqes = null;
            }

            if (_sqBase is not null)
            {
                SysLinux.MUnmap(_sqBase, _sqMapLen);

                // 双映射模式下 CQ 独立，需单独解除；单映射模式 _cqBase 与 _sqBase 同源，已随上面解除。
                if (!_singleMmap && _cqBase is not null)
                    SysLinux.MUnmap(_cqBase, _cqMapLen);

                _sqBase = null;
                _cqBase = null;

#if DEBUG
                Log.Info("MUnmap successfully!");
#endif
            }

            if (_ringFd >= 0)
            {
                SysLinux.Close(_ringFd);
                _ringFd = -1;

#if DEBUG
                Log.Info("fd close successfully!");
#endif
            }
        }
    }
}
