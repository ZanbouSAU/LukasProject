// FileTransfer/SockAddrIn.cs

using System.Runtime.InteropServices;

namespace FileTransfer;

internal partial struct SysNet
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SockAddrIn
    {
        internal ushort sin_family;
        internal ushort sin_port;
        internal uint sin_addr;
        internal ulong sin_zero;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct SockAddrIn6
    {
        internal ushort sin6_family;
        internal ushort sin6_port;
        internal uint sin6_flowinfo;
        internal In6Addr sin6_addr;
        internal uint sin6_scope_id;
    }
}
