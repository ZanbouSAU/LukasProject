// Lukas/AsyncEngine/AsyncEngineFactory.cs

using System;
using System.Runtime.Versioning;

namespace Lukas.AsyncEngine;

/// <summary>
/// 按当前操作系统创建合适的异步 I/O 引擎：Windows→IOCP；Linux→优先 io_uring，失败回退线程池版；
/// macOS/FreeBSD→线程池版。其余平台不支持。
/// </summary>
public static class AsyncEngineFactory
{
    public static bool IsSupportedPlatform =>
        OperatingSystem.IsLinux()
        || OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || OperatingSystem.IsFreeBSD();

    private static string RequirementHint =>
        "This program requires Windows (I/O completion ports), Linux " +
        "(io_uring on kernel >= 6.2 with the native 'uring_io' shim, otherwise a " +
        "portable thread-pool engine), macOS or FreeBSD.";

    public static IAsyncIoEngine Create()
    {
        if (OperatingSystem.IsWindows())
            return CreateIocp();

        if (OperatingSystem.IsLinux())
            return CreateLinux();

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return CreatePosix();

        throw new PlatformNotSupportedException(RequirementHint);
    }

    /// <summary>
    /// 创建引擎，并在底层为 io_uring 时提前注册一组固定缓冲区（<c>IORING_REGISTER_BUFFERS</c>）。
    /// 仅 Linux/io_uring 生效；其它平台（IOCP/线程池）忽略这两个参数，行为与 <see cref="Create()"/> 一致。
    /// 注册失败（内核不支持或 RLIMIT_MEMLOCK 不足）会自动跳过，引擎照常工作。
    /// </summary>
    /// <param name="fixedBufferCount">固定缓冲块数（即可同时在途的固定缓冲数，常取并发度）。</param>
    /// <param name="fixedBufferSize">每块字节数（建议页对齐，如 4096 的整数倍）。</param>
    [SupportedOSPlatform("linux")]
    public static IAsyncIoEngine Create(int fixedBufferCount, int fixedBufferSize)
    {
        var engine = Create();
        if (engine is IoUringEngine uring)
        {
            try
            {
                uring.EnableFixedBuffers(fixedBufferCount, fixedBufferSize);
            }
            catch
            {
                // 启用固定缓冲属于纯优化，失败不应影响引擎可用性。
            }
        }
        return engine;
    }

    // Linux：先试 io_uring 引擎，构造失败（内核太旧或原生 shim 缺失）则回退到可移植线程池引擎。
    [SupportedOSPlatform("linux")]
    private static IAsyncIoEngine CreateLinux()
    {
        try
        {
            return new IoUringEngine();
        }
        catch
        {
            return CreatePosix();
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static PosixAsyncEngine CreatePosix() => new();

    [SupportedOSPlatform("windows")]
    private static IocpEngine CreateIocp() => new();
}
