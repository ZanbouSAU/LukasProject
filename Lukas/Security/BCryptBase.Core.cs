// Lukas/Security/BCryptBase.Core.cs

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lukas.Security;

public partial class BCryptCore
{
    internal delegate int EnhancedHashDelegate(
        ReadOnlySpan<byte> inputKey, HashType hashType, byte bcryptMinorRevision, Span<byte> destination);

    internal static byte[] CreatePasswordHash(ReadOnlySpan<byte> inputKey, ReadOnlySpan<byte> salt,
        HashType hashType = HashType.None, EnhancedHashDelegate enhancedHashKeyGen = null!)
    {
        Span<byte> outputBuffer = stackalloc byte[60];
        CreatePasswordHash(inputKey, salt, outputBuffer, out var outputBufferWritten, hashType, enhancedHashKeyGen);
        return outputBuffer[..outputBufferWritten].ToArray();
    }

    [SkipLocalsInit]
    internal static void CreatePasswordHash(ReadOnlySpan<byte> inputKey, ReadOnlySpan<byte> salt,
        Span<byte> outputBuffer, out int outputBufferWritten,
        HashType hashType = HashType.None,
        EnhancedHashDelegate enhancedHashKeyGen = null!)
    {
        if (salt.IsEmpty)
        {
            throw new ArgumentException("Invalid salt: salt cannot be empty", nameof(salt));
        }

        if (enhancedHashKeyGen == null && hashType != HashType.None)
        {
            throw new ArgumentException(
                "Invalid HashType, You can't have an enhanced hash without an implementation of the key generator.",
                nameof(hashType));
        }

        if (outputBuffer.Length != 60)
        {
            throw new ArgumentException("Output buffer must be 60 bytes long", nameof(outputBuffer));
        }

        int startingOffset;
        byte bcryptMinorRevision = 0;

        if (salt[0] != 0x24 || salt[1] != 0x32)
        {
            throw new SaltParseException("Invalid salt version");
        }

        if (salt[2] == 0x24)
        {
            startingOffset = 3;
        }
        else
        {
            bcryptMinorRevision = salt[2];
            if (bcryptMinorRevision != 0x61 && bcryptMinorRevision != 0x62 && bcryptMinorRevision != 0x78 &&
                bcryptMinorRevision != 0x79 || salt[3] != 0x24)
            {
                throw new SaltParseException("Invalid salt revision");
            }

            startingOffset = 4;
        }

        if (!TryParseTwoDigits(salt.Slice(startingOffset, 2), out var workFactor))
        {
            throw new SaltParseException("Missing salt rounds");
        }

        if (workFactor is < 1 or > 31)
        {
            throw new SaltParseException("Salt rounds out of range");
        }

        switch (hashType)
        {
            case HashType.None:
                var appendNul = bcryptMinorRevision >= 0x61;
                var inputByteCount = inputKey.Length + (appendNul ? 1 : 0);
                if (inputByteCount > 72)
                {
                    throw new ArgumentException(
                        "Invalid input key: input key cannot exceed 72 bytes for bCrypt",
                        nameof(inputKey));
                }

                Span<byte> keyBuffer = stackalloc byte[inputByteCount];
                inputKey.CopyTo(keyBuffer);
                if (appendNul) keyBuffer[inputKey.Length] = 0;

                if (!HashBytes(keyBuffer, salt.Slice(startingOffset + 3, 22),
                        bcryptMinorRevision, workFactor, outputBuffer, out var hashBytesWritten))
                    throw new BcryptAuthenticationException("Couldn't hash input");
                ZeroMemory(keyBuffer);
                outputBufferWritten = hashBytesWritten;
                return;

            case HashType.Sha256:
            case HashType.Sha384:
            case HashType.Sha512:
            default:
                if (enhancedHashKeyGen == null)
                {
                    throw new ArgumentException(
                        "Invalid HashType, You can't have an enhanced hash without an implementation of the key generator.",
                        nameof(hashType));
                }

                Span<byte> eInputBuffer = stackalloc byte[128];
                var eInputLen = enhancedHashKeyGen(inputKey, hashType, bcryptMinorRevision, eInputBuffer);
                var eInputBytes = eInputBuffer[..eInputLen];
                if (!HashBytes(eInputBytes, salt.Slice(startingOffset + 3, 22),
                        bcryptMinorRevision, workFactor, outputBuffer, out var written))
                    throw new BcryptAuthenticationException("Couldn't hash input");
                ZeroMemory(eInputBuffer);
                outputBufferWritten = written;

                return;
        }
    }

    [SkipLocalsInit]
    internal static bool HashBytes(
        ReadOnlySpan<byte> inputBytes,
        ReadOnlySpan<byte> extractedSalt,
        byte bcryptMinorRevision,
        int workFactor,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        var bCrypt = new BCrypt();

        Span<byte> saltBuffer = stackalloc byte[BCryptSaltLen];
        Span<byte> hashBuffer = stackalloc byte[BfCryptCiphertext.Length * 4];

        try
        {
            var written = DecodeBase64(extractedSalt, saltBuffer);
            var saltBytes = saltBuffer[..written];

            var hashBytes = bCrypt.CryptRaw(inputBytes, saltBytes, workFactor, hashBuffer);

            if (destination.Length < 60)
                return false;

            var pos = 0;
            destination[pos++] = 0x24;
            destination[pos++] = 0x32;
            destination[pos++] = bcryptMinorRevision;
            destination[pos++] = 0x24;

            destination[pos++] = (byte)(0x30 + workFactor / 10);
            destination[pos++] = (byte)(0x30 + workFactor % 10);

            destination[pos++] = 0x24;

            pos += EncodeBase64(saltBytes, saltBytes.Length, destination[pos..]);

            pos += EncodeBase64(hashBytes, BfCryptCiphertextLength * 4 - 1, destination[pos..]);

            bytesWritten = pos;

            return true;
        }
        finally
        {
            ZeroMemory(hashBuffer);
            ZeroMemory(saltBuffer);
        }
    }

    internal static byte[] GenerateSalt(int workFactor = DefaultRounds, byte bcryptMinorRevision = DefaultHashVersion)
    {
        if (workFactor is < MinRounds or > MaxRounds)
        {
            throw new ArgumentOutOfRangeException(nameof(workFactor), workFactor,
                $"The work factor must be between {MinRounds} and {MaxRounds} (inclusive)");
        }

        if (bcryptMinorRevision != 0x61 && bcryptMinorRevision != 0x62 && bcryptMinorRevision != 0x78 &&
            bcryptMinorRevision != 0x79)
        {
            throw new ArgumentException("BCrypt Revision should be a, b, x or y", nameof(bcryptMinorRevision));
        }

        Span<byte> saltBytes = stackalloc byte[BCryptSaltLen];

        RngCsp.GetBytes(saltBytes);

        var result = new byte[29];
        result[0] = 0x24;
        result[1] = 0x32;
        result[2] = bcryptMinorRevision;
        result[3] = 0x24;
        result[4] = (byte)(0x30 + workFactor / 10);
        result[5] = (byte)(0x30 + workFactor % 10);
        result[6] = 0x24;
        EncodeBase64(saltBytes, saltBytes.Length, result.AsSpan(7));

        return result;
    }

    private static bool TryParseTwoDigits(ReadOnlySpan<byte> two, out int value)
    {
        value = 0;
        if (two.Length != 2)
            return false;
        var b0 = two[0];
        var b1 = two[1];
        if (b0 < 0x30 || b0 > 0x39 || b1 < 0x30 || b1 > 0x39)
            return false;
        value = (b0 - 0x30) * 10 + (b1 - 0x30);
        return true;
    }

    internal static int EncodeBase64(ReadOnlySpan<byte> byteArray, int length, Span<byte> destination)
    {
        if (length <= 0 || length > byteArray.Length)
        {
            throw new ArgumentException("Invalid length", nameof(length));
        }

        var pos = 0;
        var off = 0;
        while (off < length)
        {
            var c1 = byteArray[off++] & 0xff;
            destination[pos++] = Base64Code[(c1 >> 2) & 0x3f];
            c1 = (c1 & 0x03) << 4;
            if (off >= length)
            {
                destination[pos++] = Base64Code[c1 & 0x3f];
                break;
            }

            var c2 = byteArray[off++] & 0xff;
            c1 |= (c2 >> 4) & 0x0f;
            destination[pos++] = Base64Code[c1 & 0x3f];
            c1 = (c2 & 0x0f) << 2;
            if (off >= length)
            {
                destination[pos++] = Base64Code[c1 & 0x3f];
                break;
            }

            c2 = byteArray[off++] & 0xff;
            c1 |= (c2 >> 6) & 0x03;
            destination[pos++] = Base64Code[c1 & 0x3f];
            destination[pos++] = Base64Code[c2 & 0x3f];
        }

        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DecodeBase64(ReadOnlySpan<byte> encodedSpan, Span<byte> destination)
    {
        var outputLength = 0;
        var position = 0;

        while (position < encodedSpan.Length - 1 && outputLength < destination.Length)
        {
            var c1 = Char64(encodedSpan[position++]);
            var c2 = Char64(encodedSpan[position++]);
            if (c1 == -1 || c2 == -1) break;

            destination[outputLength] = (byte)((c1 << 2) | ((c2 & 0x30) >> 4));
            if (++outputLength >= destination.Length || position >= encodedSpan.Length) break;

            var c3 = Char64(encodedSpan[position++]);
            if (c3 == -1) break;

            destination[outputLength] = (byte)(((c2 & 0x0F) << 4) | ((c3 & 0x3C) >> 2));
            if (++outputLength >= destination.Length || position >= encodedSpan.Length) break;

            var c4 = Char64(encodedSpan[position++]);
            if (c4 == -1) break;

            destination[outputLength] = (byte)(((c3 & 0x03) << 6) | c4);
            ++outputLength;
        }

        return outputLength;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal ReadOnlySpan<byte> CryptRaw(ReadOnlySpan<byte> inputBytes, ReadOnlySpan<byte> saltBytes,
        int workFactor, Span<byte> destination)
    {
        Span<uint> cdata = stackalloc uint[BfCryptCiphertext.Length];
        BfCryptCiphertext.CopyTo(cdata);
        var clen = cdata.Length;

        if (workFactor is < MinRounds or > MaxRounds)
        {
            throw new ArgumentException("Bad number of rounds", nameof(workFactor));
        }

        if (saltBytes.Length != BCryptSaltLen)
        {
            throw new ArgumentException("Bad salt Length", nameof(saltBytes));
        }

        var rounds = 1u << workFactor;

        if (rounds < 1)
        {
            throw new ArgumentException("Bad number of rounds", nameof(workFactor));
        }

        InitializeKey();
        try
        {
            EksKey(saltBytes, inputBytes);

            int i, j;

            for (i = 0; i != rounds; i++)
            {
                Key(inputBytes);
                Key(saltBytes);
            }

            for (i = 0; i < 64; i++)
            {
                for (j = 0; j < clen >> 1; j++)
                {
                    Encipher(cdata, j << 1);
                }
            }

            for (i = 0, j = 0; i < clen; i++)
            {
                destination[j++] = (byte)((cdata[i] >> 24) & 0xff);
                destination[j++] = (byte)((cdata[i] >> 16) & 0xff);
                destination[j++] = (byte)((cdata[i] >> 8) & 0xff);
                destination[j++] = (byte)(cdata[i] & 0xff);
            }

            return destination;
        }
        finally
        {
            ZeroMemory(_p);
            ZeroMemory(_s);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Encipher(Span<uint> blockArray, int offset)
    {
        ref var s = ref MemoryMarshal.GetArrayDataReference(_s);
        ref var p = ref MemoryMarshal.GetArrayDataReference(_p);

        var block = blockArray[offset];
        var r = blockArray[offset + 1];

        block ^= p;

        unchecked
        {
            uint round;
            for (round = 0; round <= BlowfishNumRounds - 2;)
            {
                var n = Unsafe.Add(ref s, (int)((block >> 24) & 0xff));
                n += Unsafe.Add(ref s, (int)(0x100 | ((block >> 16) & 0xff)));
                n ^= Unsafe.Add(ref s, (int)(0x200 | ((block >> 8) & 0xff)));
                n += Unsafe.Add(ref s, (int)(0x300 | (block & 0xff)));
                r ^= n ^ Unsafe.Add(ref p, (int)(++round));

                n = Unsafe.Add(ref s, (int)((r >> 24) & 0xff));
                n += Unsafe.Add(ref s, (int)(0x100 | ((r >> 16) & 0xff)));
                n ^= Unsafe.Add(ref s, (int)(0x200 | ((r >> 8) & 0xff)));
                n += Unsafe.Add(ref s, (int)(0x300 | (r & 0xff)));
                block ^= n ^ Unsafe.Add(ref p, (int)(++round));
            }

            blockArray[offset] = r ^ Unsafe.Add(ref p, BlowfishNumRounds + 1);
            blockArray[offset + 1] = block;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint StreamToWord(ReadOnlySpan<byte> data, ref int offset)
    {
        var len = data.Length;
        var off = offset;
        ref var d = ref MemoryMarshal.GetReference(data);

        uint word = 0;
        for (var i = 0; i < 4; i++)
        {
            word = (word << 8) | (uint)(Unsafe.Add(ref d, off) & 0xff);
            off++;
            if (off >= len) off = 0;
        }

        offset = off;
        return word;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Key(ReadOnlySpan<byte> keyBytes)
    {
        int i;
        var kOfP = 0;
        Span<uint> lr = stackalloc uint[2];
        lr[0] = 0; lr[1] = 0;

        int pLen = _p.Length, sLen = _s.Length;
        ref var p = ref MemoryMarshal.GetArrayDataReference(_p);
        ref var sBox = ref MemoryMarshal.GetArrayDataReference(_s);

        for (i = 0; i < pLen; i++)
        {
            Unsafe.Add(ref p, i) ^= StreamToWord(keyBytes, ref kOfP);
        }

        for (i = 0; i < pLen; i += 2)
        {
            Encipher(lr, 0);
            Unsafe.Add(ref p, i) = lr[0];
            Unsafe.Add(ref p, i + 1) = lr[1];
        }

        for (i = 0; i < sLen; i += 2)
        {
            Encipher(lr, 0);
            Unsafe.Add(ref sBox, i) = lr[0];
            Unsafe.Add(ref sBox, i + 1) = lr[1];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EksKey(ReadOnlySpan<byte> saltBytes, ReadOnlySpan<byte> inputBytes)
    {
        int i;
        var passwordOffset = 0;
        var saltOffset = 0;

        Span<uint> lr = stackalloc uint[2];
        lr[0] = 0; lr[1] = 0;

        int pLen = _p.Length, sLen = _s.Length;
        ref var p = ref MemoryMarshal.GetArrayDataReference(_p);
        ref var sBox = ref MemoryMarshal.GetArrayDataReference(_s);

        for (i = 0; i < pLen; i++)
        {
            Unsafe.Add(ref p, i) ^= StreamToWord(inputBytes, ref passwordOffset);
        }

        for (i = 0; i < pLen; i += 2)
        {
            lr[0] ^= StreamToWord(saltBytes, ref saltOffset);
            lr[1] ^= StreamToWord(saltBytes, ref saltOffset);
            Encipher(lr, 0);
            Unsafe.Add(ref p, i) = lr[0];
            Unsafe.Add(ref p, i + 1) = lr[1];
        }

        for (i = 0; i < sLen; i += 2)
        {
            lr[0] ^= StreamToWord(saltBytes, ref saltOffset);
            lr[1] ^= StreamToWord(saltBytes, ref saltOffset);
            Encipher(lr, 0);
            Unsafe.Add(ref sBox, i) = lr[0];
            Unsafe.Add(ref sBox, i + 1) = lr[1];
        }
    }
}
