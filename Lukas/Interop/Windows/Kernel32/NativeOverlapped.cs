// Lukas/Interop/Windows/Kernel32/NativeOverlapped.cs

namespace Lukas.Interop.Windows.Kernel32;

// OVERLAPPED 结构：异步 I/O 的请求上下文，Offset/OffsetHigh 共同表示 64 位文件偏移。

internal struct NativeOverlapped
{
    internal nuint Internal;
    internal nuint InternalHigh;
    internal uint Offset;
    internal uint OffsetHigh;
    internal nint HEvent;
    
    internal void SetOffset(long offset)
    {
        var u = unchecked((ulong)offset);
        Offset = (uint)(u & 0xFFFFFFFF);
        OffsetHigh = (uint)(u >> 32);
    }
}
