// Lukas/Io.InAsyncBase.CharBuffer.cs

using System;
using System.Threading.Tasks;

namespace Lukas.Std;

public static partial class Io
{
    internal sealed partial class InAsyncBase
    {
        private char[] _charBuffer;

        private int _charSize;

        private int _charPos;

        private int _charLen;
        
        // 异步填充字符缓冲：从字节缓冲解码 UTF-8；循环以应对「字节够但凑不齐完整字符」的情况。
        private async ValueTask<bool> FillCharBufferAsync()
        {
            while (true)
            {
                if (_bytePos >= _byteLen && !await FillByteBufferAsync().ConfigureAwait(false))
                    return false;
                
                _decoder.Convert(
                    _byteBuffer.AsSpan(_bytePos, _byteLen - _bytePos),
                    _charBuffer.AsSpan(0, _charSize),
                    flush: false,
                    out var bytesUsed,
                    out var charsUsed,
                    out _);

                _bytePos += bytesUsed;

                if (charsUsed > 0)
                {
                    _charPos = 0;
                    _charLen = charsUsed;
                    return true;
                }
            }
        }
    }
}
