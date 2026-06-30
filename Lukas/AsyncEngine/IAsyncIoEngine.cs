// Lukas/AsyncEngine/IAsyncIoEngine.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace Lukas.AsyncEngine;

/// <summary>
/// 异步文件 I/O 引擎的统一抽象。不同平台有不同实现：Linux 上是 io_uring，Windows 上是 IOCP，
/// 其余 Unix 用线程池兜底。上层（如 <see cref="Io.FileAsync"/>）只依赖本接口，无需关心底层机制。
///
/// 约定：所有方法用「文件描述符 fd（一个整数）」标识打开的文件；偏移参数传 -1 表示「沿用文件当前位置」。
/// 引擎自身持有后台资源，用完需 <see cref="IDisposable.Dispose"/>。
/// </summary>
public interface IAsyncIoEngine : IDisposable
{
    /// <summary>异步打开文件，返回 fd（&lt; 0 视为失败，由实现以异常呈现）。</summary>
    ValueTask<int> OpenAsync(
        ReadOnlySpan<byte> path,
        Flags flags = Flags.Append,
        uint permission = 0x1A4,
        CancellationToken cancellationToken = default);

    ValueTask<int> AcceptAsync(int fd, nint addr, nint addrLen, uint acceptFlags, CancellationToken cancellationToken = default);
    
    /// <summary>从 <paramref name="offset"/> 异步读入 <paramref name="buffer"/>，返回实际读取字节数（0 表示文件尾）。</summary>
    ValueTask<int> ReadAsync(int fd, Memory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default);
    
    /// <summary>把 <paramref name="buffer"/> 从 <paramref name="offset"/> 异步写入，返回实际写入字节数。</summary>
    ValueTask<int> WriteAsync(int fd, ReadOnlyMemory<byte> buffer, long offset = -1, CancellationToken cancellationToken = default);
    
    /// <summary>异步关闭 fd。</summary>
    ValueTask CloseAsync(int fd, CancellationToken cancellationToken = default);
}
