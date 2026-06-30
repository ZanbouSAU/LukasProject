// Lukas/Io.InBase.ByteBuffer.cs

using System;

namespace Lukas.Std;

public static partial class Io
{
    public partial class InBase
    {
        private unsafe byte* _byteBuffer = null;
        
        private int _byteSize;
        
        private int _bytePos;
        
        private int _byteLen;
        
        // 填充字节缓冲：先把未消费的尾部数据搬到缓冲头部（紧凑化），再从句柄读入填满剩余空间。
        // 返回 false 表示已到流末尾。
        private unsafe bool FillByteBuffer()
        {
            if (_bytePos > 0)
            {
                _byteLen -= _bytePos;
                if (_byteLen > 0)
                {
                    Buffer.MemoryCopy(
                        _byteBuffer + _bytePos,
                        _byteBuffer,
                        _byteSize, 
                        _byteLen);
                }
            
                _bytePos = 0;
            }

            var space = _byteSize - _byteLen;

            var n = OperatingSystem.IsWindows()
                ? Pal.ReadFile(_handle, _byteBuffer + _byteLen, space)
                : Pal.Read(_handle, _byteBuffer + _byteLen, space);
        
            if (n <= 0)
                return false;

            _byteLen += n;
            return true;
        }
    }
}
