// FileTransfer/In6Addr.cs

using System.Runtime.InteropServices;

namespace FileTransfer;

[StructLayout(LayoutKind.Explicit, Size = 16)]
public unsafe struct In6Addr
{
    [FieldOffset(0)]
    public fixed byte s6_addr[16];

    [FieldOffset(0)]
    public fixed ushort s6_addr16[8];

    [FieldOffset(0)]
    public fixed uint s6_addr32[4];

    public static In6Addr Any => new();

    public static In6Addr Loopback
    {
        get
        {
            var addr = new In6Addr();
            addr.s6_addr[15] = 1;
            return addr;
        }
    }

    public bool IsAny
    {
        get
        {
            for (var i = 0; i < 16; i++)
            {
                if (s6_addr[i] != 0)
                    return false;
            }
            return true;
        }
    }

    public bool IsLoopback
    {
        get
        {
            if (s6_addr[15] != 1)
                return false;

            for (var i = 0; i < 15; i++)
            {
                if (s6_addr[i] != 0)
                    return false;
            }
            return true;
        }
    }
}