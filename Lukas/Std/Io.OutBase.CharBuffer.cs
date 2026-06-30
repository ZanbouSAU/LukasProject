// Lukas/Io.OutBase.CharBuffer.cs

using System;
using System.Buffers;
using System.Text.Unicode;

namespace Lukas.Std;

public static partial class Io
{
    internal partial class OutBase
    {
        private unsafe char* _charBuffer = null;

        private int _charSize;

        private int _charPos;
        
        private bool _enableCharBuffer = true;
        
        // 把 UTF-16 字符按 UTF-8 编码进字节缓冲；目标空间不足就先冲刷再续，
        // 遇到非法/不完整码元写入替换字符 U+FFFD（即 \ufffd），保证输出始终是合法 UTF-8。
        private unsafe void ConvertUtf16ToByteBuffer(char* chars, int length)
        {
            if (length == 0)
                return;

            var p = chars;
            var remaining = length;

            while (remaining > 0)
            {
                if (_bytePos >= _byteSize)
                    ByteFlush();

                var status = Utf8.FromUtf16(
                    new ReadOnlySpan<char>(p, remaining),
                    new Span<byte>(_byteBuffer + _bytePos, _byteSize - _bytePos),
                    out var charsRead,
                    out var bytesWritten);

                _bytePos += bytesWritten;
                p += charsRead;
                remaining -= charsRead;

                switch (status)
                {
                    case OperationStatus.Done:
                        break;
                    case OperationStatus.DestinationTooSmall:
                        ByteFlush();
                        continue;
                    case OperationStatus.NeedMoreData:
                        var replacementSpan = "\ufffd"u8;

                        if (_bytePos + replacementSpan.Length > _byteSize)
                            ByteFlush();
                            
                        replacementSpan.CopyTo(new Span<byte>(_byteBuffer + _bytePos, replacementSpan.Length));
                        _bytePos += replacementSpan.Length;

                        p += 1;
                        remaining -= 1;
                        break;
                    case OperationStatus.InvalidData:
                        replacementSpan = "\ufffd"u8;
                            
                        if (_bytePos + replacementSpan.Length > _byteSize)
                            ByteFlush();
                            
                        replacementSpan.CopyTo(new Span<byte>(_byteBuffer + _bytePos, replacementSpan.Length));
                        _bytePos += replacementSpan.Length;

                        p += 1;
                        remaining -= 1;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(chars), status, $"未知状态：{status}");
                }
            }
        }
        
        // 不经字符缓冲，直接转码写出；isLine 时补换行并按需触发冲刷。
        private unsafe void WriteUtf16Core(char* chars, int length, bool isLine)
        {
            ConvertUtf16ToByteBuffer(chars, length);

            if (!isLine)
                return;

            if (_bytePos >= _byteSize)
                ByteFlush();

            _byteBuffer[_bytePos++] = 0x0A;

            if (_bytePos > 0)
                ByteFlush();
        
            if (_enableAutoFlush)
                CharFlush();
        }
        
        // 把字符攒进字符缓冲（关闭缓冲时退化为直接转码）；满则冲刷，isLine 时追加 '\n'。
        private unsafe void WriteChars(char* chars, int length, bool isLine = false)
        {
            if (length == 0)
                return;

            if (!_enableCharBuffer)
            {
                WriteUtf16Core(chars, length, isLine);
                return;
            }

            var p = chars;
            var remaining = length;

            while (remaining > 0)
            {
                var canCopy = Math.Min(remaining, _charSize - _charPos);

                new ReadOnlySpan<char>(p, canCopy)
                    .CopyTo(new Span<char>(_charBuffer + _charPos, canCopy));

                _charPos += canCopy;
                p += canCopy;
                remaining -= canCopy;

                if (_charPos >= _charSize)
                    CharFlush();
            }

            if (!isLine) return;

            if (_charPos >= _charSize)
                CharFlush();

            _charBuffer[_charPos++] = '\n';

            if (_charPos >= _charSize)
                CharFlush();
        
            if (_enableAutoFlush)
                CharFlush();
        }
        
        // 字符级冲刷：把字符缓冲转码进字节缓冲，必要时再触发字节冲刷。
        private void CharFlush()
        {
            if (_charPos == 0 || _disposed)
                return;

            unsafe
            {
                ConvertUtf16ToByteBuffer(_charBuffer, _charPos);
            }

            _charPos = 0;

            if (_bytePos > 0)
                ByteFlush();
        }
        
        /// <summary>开关字符缓冲；关闭后字符将直接转码写出。</summary>
        internal void EnableCharBuffer(bool enableCharBuffer = true)
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                _enableCharBuffer = enableCharBuffer;
            }
        }
    }
}
