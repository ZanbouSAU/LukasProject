// Lukas/Interop/Unix/Linux/AddrFamily.cs

namespace Lukas.Interop.Unix.Linux;

internal partial struct SysNet
{
    internal struct AddrFamily
    {
        internal const int AfUnspec = 0;
        internal const int AfLocal = 1;
        internal const int AfUnix = AfLocal;
        internal const int AfFile = AfLocal;
        internal const int AfInet = 2;
        internal const int AfAx25 = 3;
        internal const int AfIpx = 4;
        internal const int AfAppletalk = 5;
        internal const int AfNetRom = 6;
        internal const int AfBridge = 7;
        internal const int AfAtmPvc = 8;
        internal const int AfX25 = 9;
        internal const int AfInet6 = 10;
        internal const int AfRose = 11;
        internal const int AfDecNet = 12;
        internal const int AfNetBeui = 13;
        internal const int AfSecurity = 14;
        internal const int AfKey = 15;
        internal const int AfNetLink = 16;
        internal const int AfRoute = AfNetLink;
        internal const int AfPacket = 17;
        internal const int AfAsh = 18;
        internal const int AfEconet = 19;
        internal const int AfAtmSvc = 20;
        internal const int AfRds = 21;
        internal const int AfSna = 22;
        internal const int AfIrda = 23;
        internal const int AfPppoX = 24;
        internal const int AfWanpipe = 25;
        internal const int AfLlc = 26;
        internal const int AfIb = 27;
        internal const int AfMpls = 28;
        internal const int AfCan = 29;
        internal const int AfTipc = 30;
        internal const int AfBluetooth = 31;
        internal const int AfIucv = 32;
        internal const int AfRxRpc = 33;
        internal const int AfIsdn = 34;
        internal const int AfPhonet = 35;
        internal const int AfIeee802154 = 36;
        internal const int AfCaif = 37;
        internal const int AfAlg = 38;
        internal const int AfNfc = 39;
        internal const int AfVSock = 40;
        internal const int AfKcm = 41;
        internal const int AfQualcomm = 42;
        internal const int AfSmc = 43;
        internal const int AfXdp = 44;
        internal const int AfMctp = 45;
        internal const int AfMax = 46;
    }
}
