// Lukas/Interop/Unix/Linux/SocketType.cs

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    internal struct SocketType
    {
        internal const int SockStream = 1;
        internal const int SockDgram = 2;
        internal const int SockRaw = 3;
        internal const int SockRdm = 4;
        internal const int SockSeqPacket = 5;
        internal const int SockDccp = 6;
        internal const int SockPacket = 10;
        internal const int SockCloexec = 524288;
        internal const int SockNonBlock = 2048;
    }
}
