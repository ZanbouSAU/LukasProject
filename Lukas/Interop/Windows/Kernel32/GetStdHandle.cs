// Lukas/Interop/Windows/Kernel32/GetStdHandle.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Windows.Kernel32;

// GetStdHandle：取标准输入/输出/错误的句柄。标注 SuppressGCTransition 以省去这类高频小调用的 GC 切换开销。

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
#if !NO_SUPPRESS_GC_TRANSITION
    [SuppressGCTransition]
#endif
    internal static unsafe partial nint GetStdHandle(int nStdHandle);
}
