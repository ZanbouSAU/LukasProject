using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransfer;

[SupportedOSPlatform("linux")]
internal static class Program
{
    private static async Task Main()
    {
        var ioUringEngine = new IoUringEngine();
        
        SysNet.SockAddrIn6 serverAddr6 = default;
        SysNet.SockAddrIn6 clientAddr6 = default;
        
        var serverFd = SysNet.Socket(SysNet.AddrFamily.AfInet6, SysNet.SocketType.SockStream, 0);
        serverAddr6.sin6_family = SysNet.AddrFamily.AfInet6;
        serverAddr6.sin6_addr = In6Addr.Any;
        serverAddr6.sin6_port = ((ushort)8080).Htons;

        unsafe
        {
            SysNet.Bind(serverFd, (SysNet.SockAddr*)&serverAddr6, (uint)sizeof(SysNet.SockAddrIn6));
        }
        
        SysNet.Listen(serverFd, 128);
        
        RunAsync()

        while (true)
        {
            var addrPtr = IntPtr.Zero;
            var addrLen = IntPtr.Zero;
            unsafe
            {
                addrPtr = (nint)Unsafe.AsPointer(ref clientAddr6);
                addrLen = sizeof(SysNet.SockAddrIn6);
            }
            var clientFd = await ioUringEngine.AcceptAsync(
                serverFd,
                addrPtr,
                addrLen,
                0,
                CancellationToken.None
            );
        }
    }
    
    private static async Task RunAsync(nint addrPtr, nint addrLen)
    {
        var ioUringEngine = new IoUringEngine();

        while (true)
        {
            
        }
    }
}