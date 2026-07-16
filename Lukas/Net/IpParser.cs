// Lukas/Net/IpParser.cs

using System;

namespace Lukas.Net;

public ref struct IpParser
{
    public static bool TryParseIpv4(ReadOnlySpan<byte> ipSource, out uint result)
    {
        result = 0;
        uint addr = 0;
        var octet = 0;
        var value = -1;
        var digits = 0;

        foreach (var ipByte in ipSource)
        {
            switch (ipByte)
            {
                case >= 0x30 and <= 0x39:
                {
                    var digit = ipByte - 0x30;
                    value = value < 0 ? digit : value * 10 + digit;
                    if (++digits > 3 || value > 255)
                        return false;
                    break;
                }
                case 0x2E when value < 0 || octet == 3:
                    return false;
                case 0x2E:
                    addr = (addr << 8) | (uint)value;
                    octet++;
                    value = -1;
                    digits = 0;
                    break;
                default:
                    return false;
            }
        }

        if (octet != 3 || value < 0)
            return false;

        addr = (addr << 8) | (uint)value;
        result = addr.Htonl;

        return true;
    }
}
