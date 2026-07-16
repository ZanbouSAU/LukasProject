// Lukas/Security/HashParser.cs

using System;

namespace Lukas.Security;

public static class HashParser
{
    private static readonly HashFormatDescriptor OldFormatDescriptor = new(versionLength: 1);
    private static readonly HashFormatDescriptor NewFormatDescriptor = new(versionLength: 2);

    public static HashInformation GetHashInformation(ReadOnlySpan<byte> hash)
    {
        if (!IsValidHash(hash, out var format))
        {
            ThrowInvalidHashFormat();
        }

        var workFactor = 10 * (hash[format.WorkfactorOffset] - 0x30) + (hash[format.WorkfactorOffset + 1] - 0x30);

        return new HashInformation(
            hash[..format.SettingLength].ToArray(),
            hash.Slice(1, format.VersionLength).ToArray(),
            workFactor,
            hash[format.HashOffset..].ToArray());
    }

    public static int GetWorkFactor(ReadOnlySpan<byte> hash)
    {
        if (!IsValidHash(hash, out var format))
        {
            ThrowInvalidHashFormat();
        }

        return 10 * (hash[format.WorkfactorOffset] - 0x30) + (hash[format.WorkfactorOffset + 1] - 0x30);
    }

    public static byte[] GetSalt(ReadOnlySpan<byte> hash)
    {
        if (!IsValidHash(hash, out var format))
        {
            ThrowInvalidHashFormat();
        }

        if (hash.IsEmpty || hash.Length < 29)
        {
            throw new ArgumentException("Invalid BCrypt hash.");
        }

        return hash[..(22 + format.HashOffset)].ToArray();
    }

    private static bool IsValidHash(ReadOnlySpan<byte> hash, out HashFormatDescriptor format)
    {
        if (hash.Length != 59 && hash.Length != 60 ||
            hash.Length < 2 || hash[0] != 0x24 || hash[1] != 0x32)
        {
            format = null!;
            return false;
        }

        var offset = 2;
        if (IsValidBCryptVersionChar(hash[offset]))
        {
            offset++;
            format = NewFormatDescriptor;
        }
        else
        {
            format = OldFormatDescriptor;
        }

        if (hash[offset++] != 0x24)
        {
            format = null!;
            return false;
        }

        if (!IsAsciiNumeric(hash[offset++]) || !IsAsciiNumeric(hash[offset++]))
        {
            format = null!;
            return false;
        }

        if (hash[offset++] != 0x24)
        {
            format = null!;
            return false;
        }

        for (var i = offset; i < hash.Length; ++i)
        {
            if (!IsValidBCryptBase64Char(hash[i]))
            {
                format = null!;
                return false;
            }
        }

        return true;
    }

    private static bool IsValidBCryptVersionChar(byte value)
    {
        return value is 0x61 or 0x62 or 0x78 or 0x79;
    }

    private static bool IsValidBCryptBase64Char(byte value)
    {
        return value is 0x2E or 0x2F
            or >= 0x30 and <= 0x39
            or >= 0x41 and <= 0x5A
            or >= 0x61 and <= 0x7A;
    }

    private static bool IsAsciiNumeric(byte value)
    {
        return value is >= 0x30 and <= 0x39;
    }

    private static void ThrowInvalidHashFormat()
    {
        throw new SaltParseException("Invalid Hash Format");
    }

    private class HashFormatDescriptor
    {
        public HashFormatDescriptor(int versionLength)
        {
            VersionLength = versionLength;
            WorkfactorOffset = 1 + VersionLength + 1;
            SettingLength = WorkfactorOffset + 2;
            HashOffset = SettingLength + 1;
        }

        public int VersionLength { get; }

        public int WorkfactorOffset { get; }

        public int SettingLength { get; }

        public int HashOffset { get; }
    }
}
