// Lukas/Security/BCrypt.cs

using System;
using System.Text;

namespace Lukas.Security;

public sealed class BCrypt : BCryptCore
{
    private static byte[] EncodePassword(ReadOnlySpan<char> password)
    {
        var count = SafeUtf8.GetByteCount(password);
        var buffer = new byte[count];
        SafeUtf8.GetBytes(password, buffer);
        return buffer;
    }

    private static byte[] AsciiToBytes(ReadOnlySpan<char> ascii)
    {
        var buffer = new byte[ascii.Length];
        for (var i = 0; i < ascii.Length; i++)
        {
            buffer[i] = (byte)ascii[i];
        }

        return buffer;
    }

    private static string ToAsciiString(ReadOnlySpan<byte> ascii) => Encoding.ASCII.GetString(ascii);

    public static byte[] ValidateAndUpgradeHash(ReadOnlySpan<byte> currentKey, ReadOnlySpan<byte> currentHash,
        ReadOnlySpan<byte> newKey, int workFactor = DefaultRounds, bool forceWorkFactor = false)
    {
        if (currentHash.Length != 60)
            throw new ArgumentException("Invalid Hash", nameof(currentHash));

        if (!Verify(currentKey, currentHash))
            throw new BcryptAuthenticationException("Current credentials could not be authenticated");

        if (currentHash[0] != (byte)'$' || currentHash[1] != (byte)'2')
            throw new SaltParseException("Invalid bcrypt version");

        if (workFactor < 1 || workFactor > 31)
            throw new SaltParseException("Work factor out of range");

        var startingOffset = 3;

        if (currentHash[2] != (byte)'$')
        {
            var minor = currentHash[2];
            if (minor != (byte)'a' && minor != (byte)'b' && minor != (byte)'x' && minor != (byte)'y' || currentHash[3] != (byte)'$')
            {
                throw new SaltParseException("Invalid bcrypt revision");
            }

            startingOffset = 4;
        }

        if (currentHash[startingOffset + 2] > (byte)'$')
        {
            throw new SaltParseException("Missing work factor");
        }

        var d0 = currentHash[startingOffset];
        var d1 = currentHash[startingOffset + 1];
        if (d0 < (byte)'0' || d0 > (byte)'9' || d1 < (byte)'0' || d1 > (byte)'9')
        {
            throw new SaltParseException("Missing work factor");
        }

        var currentWorkFactor = (d0 - (byte)'0') * 10 + (d1 - (byte)'0');

        if (!forceWorkFactor && currentWorkFactor > workFactor)
        {
            workFactor = currentWorkFactor;
        }

        return HashPassword(newKey, GenerateSalt(workFactor));
    }

    public static string ValidateAndUpgradeHash(string currentKey, string currentHash, string newKey,
        int workFactor = DefaultRounds, bool forceWorkFactor = false)
    {
        if (currentKey == null)
            throw new ArgumentNullException(nameof(currentKey));

        if (string.IsNullOrEmpty(currentHash) || currentHash.Length != 60)
            throw new ArgumentException("Invalid Hash", nameof(currentHash));

        var keyBytes = EncodePassword(currentKey);
        var newKeyBytes = EncodePassword(newKey);
        try
        {
            var result = ValidateAndUpgradeHash(keyBytes, AsciiToBytes(currentHash), newKeyBytes, workFactor, forceWorkFactor);
            return ToAsciiString(result);
        }
        finally
        {
            ZeroMemory(keyBytes);
            ZeroMemory(newKeyBytes);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> password, ReadOnlySpan<byte> hash)
    {
        Span<byte> computed = stackalloc byte[60];
        CreatePasswordHash(password, hash, computed, out var written);
        return SecureEquals(hash, computed[..written]);
    }

    public static bool Verify(ReadOnlySpan<char> text, ReadOnlySpan<char> hash)
    {
        var passwordBytes = EncodePassword(text);
        try
        {
            return Verify(passwordBytes, AsciiToBytes(hash));
        }
        finally
        {
            ZeroMemory(passwordBytes);
        }
    }

    public static bool Verify(string text, string hash) => Verify(text.AsSpan(), hash.AsSpan());

    public static byte[] HashPassword(ReadOnlySpan<byte> password, int workFactor = DefaultRounds) =>
        HashPassword(password, GenerateSalt(workFactor));

    public static byte[] HashPassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt) =>
        CreatePasswordHash(password, salt);

    public static void HashPassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Span<byte> outputBuffer,
        out int outputBufferWritten)
        => CreatePasswordHash(password, salt, outputBuffer, out outputBufferWritten);

    public static string HashPassword(string inputKey, int workFactor = DefaultRounds) =>
        HashPassword(inputKey.AsSpan(), workFactor);

    public static string HashPassword(string inputKey, string salt) =>
        HashPassword(inputKey.AsSpan(), salt.AsSpan());

    public static string HashPassword(ReadOnlySpan<char> inputKey, int workFactor = DefaultRounds)
    {
        var passwordBytes = EncodePassword(inputKey);
        try
        {
            return ToAsciiString(HashPassword(passwordBytes, workFactor));
        }
        finally
        {
            ZeroMemory(passwordBytes);
        }
    }

    public static string HashPassword(ReadOnlySpan<char> inputKey, ReadOnlySpan<char> salt)
    {
        var passwordBytes = EncodePassword(inputKey);
        try
        {
            return ToAsciiString(HashPassword(passwordBytes, AsciiToBytes(salt)));
        }
        finally
        {
            ZeroMemory(passwordBytes);
        }
    }

    public static bool PasswordNeedsRehash(ReadOnlySpan<byte> hash, int newMinimumWorkLoad) =>
        HashParser.GetWorkFactor(hash) < newMinimumWorkLoad;

    public static bool PasswordNeedsRehash(string hash, int newMinimumWorkLoad) =>
        PasswordNeedsRehash(AsciiToBytes(hash), newMinimumWorkLoad);

    public static HashInformation InterrogateHash(ReadOnlySpan<byte> hash)
    {
        try
        {
            return HashParser.GetHashInformation(hash);
        }
        catch (Exception ex)
        {
            throw new HashInformationException("Error handling hash interrogation", ex);
        }
    }

    public static HashInformation InterrogateHash(string hash) => InterrogateHash(AsciiToBytes(hash));
}