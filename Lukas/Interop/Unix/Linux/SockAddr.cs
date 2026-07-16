// Lukas/Interop/Unix/Linux/SockAddr.cs

using System.Runtime.InteropServices;

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SockAddr
    {
        public ushort sa_family;
        public fixed byte sa_data[14];
    }
}
