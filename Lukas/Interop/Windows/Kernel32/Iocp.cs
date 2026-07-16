// Lukas/Interop/Windows/Kernel32/Iocp.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Windows.Kernel32;

// I/O 完成端口（IOCP）相关接口：创建/关联端口、取出完成事件、手动投递事件、取消挂起的 I/O。
// 这是 IocpEngine 在 Windows 上实现异步 I/O 的内核机制。

internal static partial class Kernel32
{
    internal const nint InvalidHandleValue = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateIoCompletionPort(
        nint fileHandle,
        nint existingCompletionPort,
        nuint completionKey,
        uint numberOfConcurrentThreads);
    
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetQueuedCompletionStatus(
        nint completionPort,
        out uint numberOfBytes,
        out nuint completionKey,
        out nint lpOverlapped,
        uint dwMilliseconds);
    
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int PostQueuedCompletionStatus(
        nint completionPort,
        uint numberOfBytesTransferred,
        nuint completionKey,
        nint lpOverlapped);
    
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(nint handle, nint lpOverlapped);
}
