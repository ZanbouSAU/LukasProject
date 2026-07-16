// Lukas/Interop/Unix/Linux/IoUring.cs
// Updated version with separated Submit/Flush and IORING_OP_SPLICE support
// .NET 10 / C# 14 compatible, performance optimized, safe explicit layout

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FileTransfer;

internal sealed class UringException(int errno) : IOException($"io_uring operation failed: errno {errno}")
{
    internal int Errno { get; } = errno;
}

internal static unsafe class IoUring
{
    private const byte OpNop = 0;
    private const byte OpReadFixed = 4;
    private const byte OpWriteFixed = 5;
    private const byte OpAccept = 13;
    private const byte OpOpenat = 18;
    private const byte OpClose = 19;
    private const byte OpRead = 22;
    private const byte OpWrite = 23;
    private const byte OpSplice = 25;

    private const uint RegRegisterBuffers = 0;
    private const uint RegUnregisterBuffers = 1;

    private const long OffSqRing = 0L;
    private const long OffCqRing = 0x8000000L;
    private const long OffSqes = 0x10000000L;

    private const uint EnterGetEvents = 1u;
    private const uint FeatSingleMmap = 1u;

    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapShared = 0x1;
    private const int MapPopulate = 0x8000;

    private const int AtFdCwd = -100;
    private const int Eintr = 4;
    private const int Eagain = 11;
    private const ulong WakeToken = ulong.MaxValue;

    internal static nint UringCreate(uint queueDepth, nint cb, nint userdata)
    {
        var ctx = new UringContext(queueDepth, cb, userdata);
        return ctx.SelfPtr;
    }

    internal static void UringDestroy(nint ctx)
    {
        if (ctx == 0) return;

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

    internal static int UringReadFixed(nint ctx, ulong token, int fd, void* buf, nuint nbytes, long offset, ushort bufIndex)
        => Context(ctx).Submit(OpReadFixed, fd, off: (ulong)offset, addr: (ulong)buf, len: (uint)nbytes, opFlags: 0, token, bufIndex);

    internal static int UringWriteFixed(nint ctx, ulong token, int fd, void* buf, nuint nbytes, long offset, ushort bufIndex)
        => Context(ctx).Submit(OpWriteFixed, fd, off: (ulong)offset, addr: (ulong)buf, len: (uint)nbytes, opFlags: 0, token, bufIndex);

    internal static int UringRegisterBuffers(nint ctx, Iovec* iovecs, uint count)
        => Context(ctx).RegisterBuffers(iovecs, count);

    internal static int UringUnregisterBuffers(nint ctx)
        => Context(ctx).UnregisterBuffers();

    internal static int UringClose(nint ctx, ulong token, int fd)
        => Context(ctx).Submit(OpClose, fd, off: 0, addr: 0, len: 0, opFlags: 0, token);

    internal static int UringSplice(nint ctx, ulong token,
        int fdOut, long offOut,
        int fdIn, long offIn,
        uint len, uint spliceFlags, uint sqeFlags = 0)
    {
        return Context(ctx).SubmitSplice(fdOut, offOut, fdIn, offIn, len, spliceFlags, token, sqeFlags);
    }

    private static UringContext Context(nint ctx)
        => GCHandle.FromIntPtr(ctx).Target as UringContext
           ?? throw new ObjectDisposedException(nameof(IoUring));

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct IoUringSqe
    {
        [FieldOffset(0)] public byte Opcode;
        [FieldOffset(1)] public byte Flags;
        [FieldOffset(2)] public ushort Ioprio;
        [FieldOffset(4)] public int Fd;
        [FieldOffset(8)] public ulong Off;
        [FieldOffset(16)] public ulong Addr;
        [FieldOffset(16)] public ulong SpliceOffIn;
        [FieldOffset(24)] public uint Len;
        [FieldOffset(28)] public uint OpFlags;
        [FieldOffset(32)] public ulong UserData;
        [FieldOffset(40)] public ushort BufIndex;
        [FieldOffset(44)] public int SpliceFdIn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Iovec
    {
        public void* Base;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoUringCqe
    {
        public ulong UserData;
        public int Res;
        public uint Flags;
    }

    private sealed class UringContext
    {
        private readonly nint _cb;
        private readonly nint _userdata;
        private readonly Lock _submitLock = new();

        private int _ringFd = -1;

        private byte* _sqBase;
        private nuint _sqMapLen;
        private byte* _cqBase;
        private nuint _cqMapLen;
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
            var ptr = SysLinux.MMap(null, length, ProtRead | ProtWrite, MapShared | MapPopulate, _ringFd, offset);
            return (nint)ptr == -1
                ? throw new UringException(Marshal.GetLastSystemError())
                : ptr;
        }

        internal int Submit(byte opcode, int fd, ulong off, ulong addr, uint len, uint opFlags, ulong token, ushort bufIndex = 0)
        {
            lock (_submitLock)
            {
                var head = Volatile.Read(ref *_sqHead);
                if (_sqTailLocal - head >= _sqEntries)
                    return -Eagain;

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
                sqe->BufIndex = bufIndex;

                _sqArray[index] = index;

                _sqTailLocal++;
                Volatile.Write(ref *_sqTail, _sqTailLocal);

                return 0;
            }
        }

        internal int SubmitSplice(
            int fdOut, long offOut,
            int fdIn, long offIn,
            uint len,
            uint spliceFlags,
            ulong token,
            uint sqeFlags = 0)
        {
            lock (_submitLock)
            {
                var head = Volatile.Read(ref *_sqHead);
                if (_sqTailLocal - head >= _sqEntries)
                    return -Eagain;

                var index = _sqTailLocal & _sqMask;
                var sqe = _sqes + index;

                *sqe = default;

                sqe->Opcode = OpSplice;
                sqe->Fd = fdOut;
                sqe->Off = (ulong)offOut;
                sqe->SpliceOffIn = (ulong)offIn;
                sqe->SpliceFdIn = fdIn;
                sqe->Len = len;
                sqe->OpFlags = spliceFlags;
                sqe->UserData = token;
                sqe->Flags = (byte)sqeFlags;

                _sqArray[index] = index;

                _sqTailLocal++;
                Volatile.Write(ref *_sqTail, _sqTailLocal);

                return 0;
            }
        }

        internal int Flush(uint minComplete = 0)
        {
            lock (_submitLock)
            {
                var toSubmit = _sqTailLocal - Volatile.Read(ref *_sqHead);
                if (toSubmit == 0)
                    return 0;

                while (true)
                {
                    var ret = SysLinux.SysIoUringEnter(
                        SysLinux.Enter,
                        (uint)_ringFd,
                        toSubmit,
                        minComplete,
                        0, null, 0);

                    if (ret >= 0)
                        return ret;

                    var err = Marshal.GetLastSystemError();
                    if (err == Eintr)
                        continue;

                    return -err;
                }
            }
        }

        private const uint AutoFlushThresholdRatio = 3;

        internal void AutoFlush()
        {
            var pending = _sqTailLocal - Volatile.Read(ref *_sqHead);
            var threshold = _sqEntries / AutoFlushThresholdRatio;

            if (pending < threshold && pending < _sqEntries - 8)
                return;

            Flush();
        }

        internal int RegisterBuffers(Iovec* iovecs, uint count)
        {
            while (true)
            {
                var ret = SysLinux.SysIoUringRegister(
                    SysLinux.Register, (uint)_ringFd, RegRegisterBuffers, iovecs, count);
                if (ret >= 0) return 0;

                var err = Marshal.GetLastSystemError();
                if (err == Eintr) continue;
                return -err;
            }
        }

        internal int UnregisterBuffers()
        {
            while (true)
            {
                var ret = SysLinux.SysIoUringRegister(
                    SysLinux.Register, (uint)_ringFd, RegUnregisterBuffers, null, 0);
                if (ret >= 0) return 0;

                var err = Marshal.GetLastSystemError();
                if (err == Eintr) continue;
                return -err;
            }
        }

        private void CompletionLoop()
        {
            while (true)
            {
                var ret = SysLinux.SysIoUringEnter(SysLinux.Enter, (uint)_ringFd, 0, 1, EnterGetEvents, null, 0);
                if (ret < 0)
                {
                    var err = Marshal.GetLastSystemError();
                    if (err == Eintr) continue;
                    break;
                }

                Reap();

                if (!_running)
                    break;
            }
        }

        private void Reap()
        {
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
            var fn = (delegate* unmanaged[Cdecl]<ulong, int, void*, void>)_cb;
            fn(token, result, (void*)_userdata);
        }

        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _running = false;

            if (_completionThread is not null)
            {
                try
                {
                    Submit(OpNop, fd: -1, off: 0, addr: 0, len: 0, opFlags: 0, WakeToken);
                    Flush();
                }
                catch { /* ignore */ }

                _completionThread.Join();
            }

            CleanupNative();

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }

        private void CleanupNative()
        {
            if (_sqes is not null)
            {
                SysLinux.MUnmap(_sqes, _sqesMapLen);
                _sqes = null;
            }

            if (_sqBase is not null)
            {
                SysLinux.MUnmap(_sqBase, _sqMapLen);

                if (!_singleMmap && _cqBase is not null)
                    SysLinux.MUnmap(_cqBase, _cqMapLen);

                _sqBase = null;
                _cqBase = null;

            }

            if (_ringFd >= 0)
            {
                SysLinux.Close(_ringFd);
                _ringFd = -1;
            }
        }
    }

    internal static void AutoFlush(nint ctx)
    {
        Context(ctx).AutoFlush();
    }
}