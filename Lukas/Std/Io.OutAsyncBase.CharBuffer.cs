// Lukas/Io.OutAsyncBase.CharBuffer.cs

using System;
using System.Buffers;
using System.IO;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace Lukas.Std;

public static partial class Io
{
    internal sealed partial class OutAsyncBase
    {
        private char[] _charBuffer;

        private int _charSize;

        private int _charPos;

        private bool _enableCharBuffer = true;
        
        private char _pendingHighSurrogate;
        private bool _hasPendingHighSurrogate;
        
        // 异步把 UTF-16 转码进字节缓冲。难点在跨调用的代理对处理：
        // 若上一次结尾留下一个高代理项（high surrogate），本次开头若是低代理项就合成完整码点，
        // 否则把它当作非法数据写成替换字符 U+FFFD。NeedMoreData 表示结尾出现孤立高代理项，暂存到下次。
        private async ValueTask ConvertUtf16ToByteBufferAsync(ReadOnlyMemory<char> chars)
        {
            if (_hasPendingHighSurrogate)
            {
                if (chars.IsEmpty)
                    return;

                var high = _pendingHighSurrogate;
                var next = chars.Span[0];

                if (char.IsLowSurrogate(next))
                {
                    await EnsureByteSpaceAsync(4).ConfigureAwait(false);
                    EncodeSurrogatePairIntoByteBuffer(high, next);

                    _hasPendingHighSurrogate = false;
                    chars = chars[1..];
                }
                else
                {
                    _hasPendingHighSurrogate = false;
                    await WriteReplacementAsync().ConfigureAwait(false);
                }
            }

            var remaining = chars;

            while (!remaining.IsEmpty)
            {
                if (_bytePos >= _byteSize)
                    await ByteFlushAsync().ConfigureAwait(false);
                
                var status = Utf8.FromUtf16(
                    remaining.Span,
                    _byteBuffer.AsSpan(_bytePos, _byteSize - _bytePos),
                    out var charsRead,
                    out var bytesWritten,
                    replaceInvalidSequences: true,
                    isFinalBlock: false);

                _bytePos += bytesWritten;
                remaining = remaining[charsRead..];

                switch (status)
                {
                    case OperationStatus.Done:
                        break;

                    case OperationStatus.DestinationTooSmall:
                        if (_bytePos == 0)
                            throw new IOException("字节缓冲区太小，无法容纳单个字符的 UTF-8 编码。");
                        await ByteFlushAsync().ConfigureAwait(false);
                        break;

                    case OperationStatus.NeedMoreData:
                        if (!remaining.IsEmpty)
                        {
                            _pendingHighSurrogate = remaining.Span[0];
                            _hasPendingHighSurrogate = true;
                            remaining = ReadOnlyMemory<char>.Empty;
                        }
                        break;

                    case OperationStatus.InvalidData:
                        await WriteReplacementAsync().ConfigureAwait(false);
                        if (!remaining.IsEmpty)
                            remaining = remaining[1..];
                        break;

                    default:
                        throw new InvalidOperationException($"未预期的转换状态：{status}");
                }
            }
        }
        
        // 把一对代理项合成的码点编码进字节缓冲（调用前已确保至少有 4 字节空间）。
        private void EncodeSurrogatePairIntoByteBuffer(char high, char low)
        {
            Span<char> pair = stackalloc char[2];
            pair[0] = high;
            pair[1] = low;
            Utf8.FromUtf16(
                pair,
                _byteBuffer.AsSpan(_bytePos, _byteSize - _bytePos),
                out _,
                out var wrote,
                replaceInvalidSequences: true,
                isFinalBlock: true);
            _bytePos += wrote;
        }
        
        // 确保字节缓冲至少有 need 字节可用，不够先冲刷；仍不够说明缓冲本身过小，直接报错。
        private async ValueTask EnsureByteSpaceAsync(int need)
        {
            if (_byteSize - _bytePos < need)
                await ByteFlushAsync().ConfigureAwait(false);
            if (_byteSize - _bytePos < need)
                throw new IOException($"字节缓冲区太小，无法容纳 {need} 字节。");
        }
        
        // 写入 UTF-8 替换字符 U+FFFD（EF BF BD），用于表示非法或不完整的码元。
        private async ValueTask WriteReplacementAsync()
        {
            await EnsureByteSpaceAsync(3).ConfigureAwait(false);
            _byteBuffer[_bytePos] = 0xEF;
            _byteBuffer[_bytePos + 1] = 0xBF;
            _byteBuffer[_bytePos + 2] = 0xBD;
            _bytePos += 3;
        }
        
        // 收尾：若还有滞留的孤立高代理项，把它写成替换字符。冲刷/释放前必须调用，否则会丢字符。
        private async ValueTask FlushDanglingHighSurrogateAsync()
        {
            if (!_hasPendingHighSurrogate)
                return;

            _hasPendingHighSurrogate = false;
            await WriteReplacementAsync().ConfigureAwait(false);
        }

        // 不经字符缓冲直接转码写出；isLine 时先收尾代理项再补换行。
        private async ValueTask WriteUtf16CoreAsync(ReadOnlyMemory<char> value, bool isLine)
        {
            await ConvertUtf16ToByteBufferAsync(value).ConfigureAwait(false);

            if (!isLine)
                return;
            
            await FlushDanglingHighSurrogateAsync().ConfigureAwait(false);

            if (_bytePos >= _byteSize)
                await ByteFlushAsync().ConfigureAwait(false);

            _byteBuffer[_bytePos++] = 0x0A;

            if (_bytePos > 0)
                await ByteFlushAsync().ConfigureAwait(false);

            if (_enableAutoFlush)
                await CharFlushAsync().ConfigureAwait(false);
        }
        
        // 把字符攒进字符缓冲（关闭缓冲时退化为直接转码）；满则冲刷，isLine 追加 '\n'。
        private async ValueTask WriteCharsAsync(ReadOnlyMemory<char> value, bool isLine)
        {
            if (value.IsEmpty)
                return;

            if (!_enableCharBuffer)
            {
                await WriteUtf16CoreAsync(value, isLine).ConfigureAwait(false);
                return;
            }

            var remaining = value;

            while (!remaining.IsEmpty)
            {
                var canCopy = Math.Min(remaining.Length, _charSize - _charPos);

                remaining.Span[..canCopy].CopyTo(_charBuffer.AsSpan(_charPos, canCopy));

                _charPos += canCopy;
                remaining = remaining[canCopy..];

                if (_charPos >= _charSize)
                    await CharFlushAsync().ConfigureAwait(false);
            }

            if (!isLine)
                return;

            if (_charPos >= _charSize)
                await CharFlushAsync().ConfigureAwait(false);

            _charBuffer[_charPos++] = '\n';

            if (_charPos >= _charSize)
                await CharFlushAsync().ConfigureAwait(false);

            if (_enableAutoFlush)
                await CharFlushAsync().ConfigureAwait(false);
        }
        
        // 把字符缓冲转码进字节缓冲，必要时再触发字节冲刷。
        private async ValueTask CharFlushAsync()
        {
            if (_charPos == 0 || _disposed)
                return;

            await ConvertUtf16ToByteBufferAsync(_charBuffer.AsMemory(0, _charPos)).ConfigureAwait(false);

            _charPos = 0;

            if (_bytePos > 0)
                await ByteFlushAsync().ConfigureAwait(false);
        }

        /// <summary>开关字符缓冲；关闭后字符直接转码写出。</summary>
        internal void EnableCharBuffer(bool enableCharBuffer = true)
        {
            _gate.Wait();
            try
            {
                if (_disposed)
                    return;
                _enableCharBuffer = enableCharBuffer;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
