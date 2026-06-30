// Lukas/Io.OutAsyncBase.ByteBuffer.cs

using System;
using System.IO;
using System.Threading.Tasks;

namespace Lukas.Std;

public static partial class Io
{
    internal sealed partial class OutAsyncBase
    {
        private byte[] _byteBuffer;

        private int _byteSize;

        private int _bytePos;

        private bool _enableByteBuffer = true;
        
        // 处理一段字节：超过缓冲容量则先冲刷再直写，否则攒入缓冲（关闭缓冲时一律直写）。
        private async ValueTask ProcessDataAsync(ReadOnlyMemory<byte> data)
        {
            if (data.IsEmpty)
                return;

            if (_enableByteBuffer)
            {
                if (data.Length >= _byteSize)
                {
                    await ByteFlushAsync().ConfigureAwait(false);
                    await WriteAllDirectAsync(data).ConfigureAwait(false);
                    return;
                }

                if (_bytePos + data.Length > _byteSize)
                    await ByteFlushAsync().ConfigureAwait(false);

                data.Span.CopyTo(_byteBuffer.AsSpan(_bytePos));
                _bytePos += data.Length;
            }
            else
            {
                await WriteAllDirectAsync(data).ConfigureAwait(false);
            }
        }
        
        // 循环异步写出整段数据，直到写完；任一次返回非正值即失败。
        private async ValueTask WriteAllDirectAsync(ReadOnlyMemory<byte> data)
        {
            while (!data.IsEmpty)
            {
                var written = await _engine.WriteAsync(_fd, data).ConfigureAwait(false);
                if (written <= 0)
                    throw new IOException("Failed to write.");

                data = data[written..];
            }
        }
        
        // 异步冲刷字节缓冲；写失败时把未写出的尾部挪到缓冲头部并修正 _bytePos，保持可重试的一致状态。
        private async ValueTask ByteFlushAsync()
        {
            if (_bytePos == 0)
                return;

            var offset = 0;
            while (offset < _bytePos)
            {
                var written = await _engine
                    .WriteAsync(_fd, _byteBuffer.AsMemory(offset, _bytePos - offset))
                    .ConfigureAwait(false);

                if (written <= 0)
                {
                    var remaining = _bytePos - offset;
                    if (offset > 0 && remaining > 0)
                        Array.Copy(_byteBuffer, offset, _byteBuffer, 0, remaining);

                    _bytePos = remaining;
                    throw new IOException("Failed to write.");
                }

                offset += written;
            }

            _bytePos = 0;
        }

        /// <summary>开关字节缓冲；关闭后字节数据直写引擎。</summary>
        internal void EnableByteBuffer(bool enableByteBuffer = true)
        {
            _gate.Wait();
            try
            {
                if (_disposed)
                    return;
                _enableByteBuffer = enableByteBuffer;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
