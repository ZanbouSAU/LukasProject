// Lukas/Io.InAsyncBase.ByteBuffer.cs

using System;
using System.Threading.Tasks;

namespace Lukas.Std;

public static partial class Io
{
    internal sealed partial class InAsyncBase
    {
        private byte[] _byteBuffer;

        private int _byteSize;

        private int _bytePos;

        private int _byteLen;
        
        // 异步填充字节缓冲：先紧凑化未消费数据，再从引擎读入填满剩余空间；返回 false 表示流末尾。
        private async ValueTask<bool> FillByteBufferAsync()
        {
            if (_bytePos > 0)
            {
                _byteLen -= _bytePos;
                if (_byteLen > 0)
                {
                    Array.Copy(_byteBuffer, _bytePos, _byteBuffer, 0, _byteLen);
                }

                _bytePos = 0;
            }

            var space = _byteSize - _byteLen;
            if (space <= 0)
                return true;

            var n = await _engine
                .ReadAsync(_fd, _byteBuffer.AsMemory(_byteLen, space))
                .ConfigureAwait(false);

            if (n <= 0)
                return false;

            _byteLen += n;
            return true;
        }
    }
}
