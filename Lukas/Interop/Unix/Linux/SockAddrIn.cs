// Lukas/Interop/Unix/Linux/SockAddrIn.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

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
}
