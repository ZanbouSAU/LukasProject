// FileTransfer/IpParser.cs

using System;

namespace FileTransfer;

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
    
    public static unsafe bool TryParseIpv6(ReadOnlySpan<byte> ipSource, ref In6Addr result)
    {
        result = default;

        if (ipSource.IsEmpty)
            return false;

        var words = stackalloc ushort[8];
        new Span<ushort>(words, 8).Clear();

        var pos = 0;
        var compress = -1;
        var digits = 0;
        var current = 0U;
        var hasIpv4 = false;
        var ipv4Start = -1;
        var segmentStart = -1;

        var i = 0;
        var len = ipSource.Length;

        while (i <= len)
        {
            var c = i < len ? ipSource[i] : (byte)0x3A;

            if (IsHexDigit(c))
            {
                if (digits == 0)
                {
                    segmentStart = i;
                    current = 0;
                }
                current = (current << 4) | HexValue(c);
                digits++;
                if (digits > 4)
                    return false;

                i++;
                continue;
            }

            if (c == 0x2E)
            {
                hasIpv4 = true;
                ipv4Start = segmentStart >= 0 ? segmentStart : i;
                break;
            }

            if (c != 0x3A)
                return false;

            if (digits > 0)
            {
                if (pos == 8)
                    return false;

                words[pos++] = (ushort)current;
                digits = 0;
                current = 0;
            }

            if (i == len)
                break;

            if (i + 1 < len && ipSource[i + 1] == 0x3A)
            {
                if (compress != -1)
                    return false;

                compress = pos;
                i += 2;
                continue;
            }

            i++;
        }

        if (hasIpv4)
        {
            if (pos > 6)
                return false;

            var ipv4Slice = ipSource[ipv4Start..];
            if (!TryParseIpv4(ipv4Slice, out var ipv4Net))
                return false;

            var ipv4Bytes = (byte*)&ipv4Net;
            var part6 = (ushort)((ipv4Bytes[0] << 8) | ipv4Bytes[1]);
            var part7 = (ushort)((ipv4Bytes[2] << 8) | ipv4Bytes[3]);

            if (compress != -1)
            {
                var missing = 6 - pos;
                if (missing < 0)
                    return false;

                for (var j = pos - 1; j >= compress; j--)
                    words[j + missing] = words[j];
                for (var j = 0; j < missing; j++)
                    words[compress + j] = 0;

                if (words[6] != 0 || words[7] != 0)
                    return false;
            }
            else
            {
                if (pos != 6)
                    return false;
            }

            words[6] = part6;
            words[7] = part7;
        }
        else
        {
            if (digits > 0)
            {
                if (pos == 8)
                    return false;

                words[pos++] = (ushort)current;
            }

            if (compress == -1)
            {
                if (pos != 8)
                    return false;
            }
            else
            {
                if (pos >= 8)
                    return false;

                var missing = 8 - pos;
                for (var j = pos - 1; j >= compress; j--)
                    words[j + missing] = words[j];
                for (var j = 0; j < missing; j++)
                    words[compress + j] = 0;
            }
        }

        for (var j = 0; j < 8; j++)
        {
            var val = words[j];
            result.s6_addr[j * 2] = (byte)(val >> 8);
            result.s6_addr[j * 2 + 1] = (byte)(val & 0xFF);
        }

        return true;
    }

    private static bool IsHexDigit(byte b)
    {
        return b is >= 0x30 and <= 0x39
            or >= 0x41 and <= 0x46
            or >= 0x61 and <= 0x66;
    }

    private static uint HexValue(byte b)
    {
        if (b is >= 0x30 and <= 0x39)
            return (uint)(b - 0x30);

        return (uint)((b & 0xDF) - 0x41 + 10);
    }
}
