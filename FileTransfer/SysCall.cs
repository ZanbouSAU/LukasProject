// Lukas/Interop/Unix/Linux/SysCall.cs

using System.Runtime.InteropServices;

namespace FileTransfer;

internal static unsafe partial class SysLinux
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoSqringOffsets
    {
        public uint head;
        public uint tail;
        public uint ring_mask;
        public uint ring_entries;
        public uint flags;
        public uint dropped;
        public uint array;
        public uint resv1;
        public ulong user_addr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCqringOffsets
    {
        public uint head;
        public uint tail;
        public uint ring_mask;
        public uint ring_entries;
        public uint overflow;
        public uint cqes;
        public uint flags;
        public uint resv1;
        public ulong user_addr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoUringParams
    {
        public uint sq_entries;
        public uint cq_entries;
        public uint flags;
        public uint sq_thread_cpu;
        public uint sq_thread_idle;
        public uint features;
        public uint wq_fd;
        public fixed uint resv[3];
        public IoSqringOffsets sq_off;
        public IoCqringOffsets cq_off;
    }

    internal const int Setup = 425;
    internal const int Enter = 426;
    internal const int Register = 427;

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial int SysIoUringSetup(long number, uint entries, IoUringParams* p);
    
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial int SysIoUringEnter(long number, uint fd, uint toSubmit, uint minComplete, uint flags, void* arg, nuint sz);
    
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial int SysIoUringRegister(long number, uint fd, uint opCode, void* arg, uint nrArgs);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    internal static partial void* MMap(void* addr, nuint len, int prot, int flags, int fd, long offset);
    
    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    internal static partial int MUnmap(void* addr, nuint len);
    
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(nint fd);
}
