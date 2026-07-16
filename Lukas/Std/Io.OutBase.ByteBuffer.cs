// Lukas/Io.OutBase.ByteBuffer.cs

using System;
using System.IO;

namespace Lukas.Std;

public static partial class Io
{
    internal partial class OutBase
    {
        private unsafe byte* _byteBuffer = null;
        
        private int _byteSize;
        
        private int _bytePos;
        
        private bool _enableByteBuffer = true;
        
        // 字节级冲刷：循环写出缓冲中已攒的数据。
        private void ByteFlush()
        {
            if (_bytePos == 0)
                return;

            unsafe
            {
                var offset = 0;
                while (offset < _bytePos)
                {
                    var written = OperatingSystem.IsWindows()
                        ? Pal.WriteFile(_handle, _byteBuffer + offset, _bytePos - offset)
                        : Pal.Write(_handle, _byteBuffer + offset, _bytePos - offset);

                    if (written <= 0)
                    {
                        // 写失败时把尚未写出的尾部数据挪到缓冲头部并修正 _bytePos，
                        // 这样异常被外层捕获后缓冲仍保持一致，可重试。
                        var remaining = _bytePos - offset;
                        if (offset > 0 && remaining > 0)
                        {
                            new Span<byte>(_byteBuffer + offset, remaining)
                                .CopyTo(new Span<byte>(_byteBuffer, remaining));
                        }
                        _bytePos = remaining;
                        throw new IOException("Failed to write.");
                    }

                    offset += written;
                }

                _bytePos = 0;
            }
        }
        
        /// <summary>开关字节缓冲；关闭后字节数据将直写句柄。</summary>
        internal void EnableByteBuffer(bool enableByteBuffer = true)
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                _enableByteBuffer = enableByteBuffer;
            }
        }
    }
}
