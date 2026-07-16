// Lukas/Io.InBase.CharBuffer.cs

namespace Lukas.Std;

public static partial class Io
{
    public partial class InBase
    {
        private unsafe char* _charBuffer = null!;
        
        private int _charSize;
        
        private int _charPos;
        
        private int _charLen;
        
        // 填充字符缓冲：从字节缓冲解码 UTF-8 到字符缓冲。
        // 循环是为了应对「字节够但凑不出完整字符」的情况——此时继续读更多字节再解码。
        private unsafe bool FillCharBuffer()
        {
            while (true)
            {
                if (_bytePos >= _byteLen && !FillByteBuffer())
                    return false;

                _decoder.Convert(
                    _byteBuffer + _bytePos,
                    _byteLen - _bytePos,
                    _charBuffer,
                    _charSize,
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
