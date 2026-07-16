// Lukas/Io.InBase.cs

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Lukas.Str;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 同步带缓冲的输入端，直接读裸句柄（Windows 用 ReadFile，其余平台用 read）。
    ///
    /// 与 <see cref="OutBase"/> 对称，维护字节缓冲 <c>_byteBuffer</c> 与字符缓冲 <c>_charBuffer</c>：
    /// 字节缓冲存原始读入数据，经 UTF-8 解码后填入字符缓冲。提供按字符、按行以及按字节/字符块的多种读取方式。
    /// 换行兼容 <c>\n</c> 与 <c>\r\n</c>。所有操作都在 <see cref="_lock"/> 下进行。
    /// </summary>
    public partial class InBase
    {
        private readonly Lock _lock = new();
        
        private bool _disposed;
        
        private readonly nint _handle;
        
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        
        internal unsafe InBase(nint inputHandle, int charSize = 512, int byteSize = 4096)
        {
            FlushOut();   // 读输入前先把待输出内容刷出，保证提示语已显示给用户
            
            _handle = inputHandle;
            _charSize = charSize;
            _charBuffer = (char*)NativeMemory.Alloc((nuint)_charSize * sizeof(char));

            _byteSize = byteSize;
            _byteBuffer = (byte*)NativeMemory.Alloc((nuint)_byteSize);
        }
        
        unsafe ~InBase()
        {
            FlushOut();

            var lockTaken = false;
            try
            {
                lockTaken = _lock.TryEnter();

                if (_charBuffer != null)
                {
                    NativeMemory.Free(_charBuffer);
                    _charBuffer = null;
                }

                if (_byteBuffer != null)
                {
                    NativeMemory.Free(_byteBuffer);
                    _byteBuffer = null;
                }
            }
            finally
            {
                if (lockTaken)
                    _lock.Exit();
            }
        }
        
        internal void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }
        
        private unsafe void DisposeCore()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
        
                if (_charBuffer != null)
                {
                    NativeMemory.Free(_charBuffer);
                    _charBuffer = null;
                }
        
                if (_byteBuffer != null)
                {
                    NativeMemory.Free(_byteBuffer);
                    _byteBuffer = null;
                }
            }
        }
        
        /// <summary>读取下一个字符；到达末尾返回 -1。</summary>
        internal unsafe int Read()
        {
            lock (_lock)
            {
                if (_disposed)
                    return -1;
        
                if (_charPos >= _charLen && !FillCharBuffer())
                    return -1;

                return _charBuffer[_charPos++];
            }
        }
        
        /// <summary>
        /// 读取一行（不含行尾换行符）；兼容 <c>\r\n</c>。
        /// 流已结束且无任何字符时返回 <c>null</c>，末行无换行也会正常返回。
        /// </summary>
        internal unsafe string? ReadLine()
        {
            lock (_lock)
            {
                if (_disposed)
                    return null;

                StringBuilder? builder = null;
                var any = false;

                while (true)
                {
                    if (_charPos >= _charLen && !FillCharBuffer())
                        break;

                    var c = _charBuffer[_charPos++];
                    any = true;

                    if (c == '\n')
                    {
                        if (builder is { Length: > 0 } && builder[^1] == '\r')
                            builder.Length--;
                        return builder?.ToString() ?? string.Empty;
                    }

                    builder ??= new StringBuilder();
                    builder.Append(c);
                }

                if (!any)
                    return null;

                if (builder is { Length: > 0 } && builder[^1] == '\r')
                    builder.Length--;

                return builder?.ToString() ?? string.Empty;
            }
        }
        
        /// <summary>把数据读入字节缓冲区，返回实际读取的字节数（可能小于请求长度）。</summary>
        internal unsafe int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            lock (_lock)
            {
                if (_disposed)
                    return 0;

                var totalRead = 0;

                while (!buffer.IsEmpty)
                {
                    if (_bytePos >= _byteLen && !FillByteBuffer())
                        break;

                    var available = _byteLen - _bytePos;
                    var toCopy = Math.Min(available, buffer.Length);

                    new ReadOnlySpan<byte>(_byteBuffer + _bytePos, toCopy)
                        .CopyTo(buffer);

                    _bytePos += toCopy;
                    buffer = buffer[toCopy..];
                    totalRead += toCopy;
                }

                return totalRead;
            }
        }
        
        /// <summary>把数据读入字符缓冲区，返回实际读取的字符数（可能小于请求长度）。</summary>
        internal unsafe int Read(Span<char> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            lock (_lock)
            {
                if (_disposed)
                    return 0;

                var totalRead = 0;

                while (!buffer.IsEmpty)
                {
                    if (_charPos >= _charLen && !FillCharBuffer())
                        break;

                    var available = _charLen - _charPos;
                    var toCopy = Math.Min(available, buffer.Length);

                    new ReadOnlySpan<char>(_charBuffer + _charPos, toCopy)
                        .CopyTo(buffer);

                    _charPos += toCopy;
                    buffer = buffer[toCopy..];
                    totalRead += toCopy;
                }

                return totalRead;
            }
        }
        
        /// <summary>
        /// 把句柄中剩余的全部数据读入 <see cref="Utf8StringBuilder"/>（先吐出缓冲里残留的字节，再直读到 EOF）。
        /// 返回累计读取的字节数。
        /// </summary>
        internal unsafe int Read(ref Utf8StringBuilder sb)
        {
            lock (_lock)
            {
                if (_disposed)
                    return 0;

                var totalRead = 0;
                
                if (_bytePos < _byteLen)
                {
                    var pending = _byteLen - _bytePos;
                    sb.Append(new ReadOnlySpan<byte>(_byteBuffer + _bytePos, pending));
                    _bytePos = _byteLen;
                    totalRead += pending;
                }
                
                const int chunk = 4096;
                while (true)
                {
                    sb.EnsureFreeSpace(chunk);
                    var free = sb.FreeSpan;

                    int n;
                    fixed (byte* dst = free)
                    { 
                        n = OperatingSystem.IsWindows()
                            ? Pal.ReadFile(_handle, dst, free.Length)
                            : Pal.Read(_handle, dst, free.Length);
                    }

                    if (n <= 0)
                        break;

                    sb.Advance(n);
                    totalRead += n;
                }

                return totalRead;
            }
        }
        
        /// <summary>
        /// 按字节直接读取一行追加到 <paramref name="sb"/>（不含行尾换行符），返回是否读到了内容。
        /// <c>pendingCr</c> 用于处理 <c>\r</c> 落在缓冲边界、<c>\n</c> 在下一次填充才出现的跨缓冲情形：
        /// 暂存这个 <c>\r</c>，下一轮若紧跟 <c>\n</c> 则当作 <c>\r\n</c> 丢弃，否则把它补回内容。
        /// </summary>
        internal unsafe bool ReadLine(ref Utf8StringBuilder sb)
        {
            lock (_lock)
            {
                if (_disposed)
                    return false;

                var any = false;
                var pendingCr = false;

                while (true)
                {
                    if (_bytePos >= _byteLen && !FillByteBuffer())
                    {
                        if (pendingCr)
                            any = true;
                        return any;
                    }
                    
                    var nl = -1;
                    for (var i = _bytePos; i < _byteLen; i++)
                    {
                        if (_byteBuffer[i] == (byte)'\n')
                        {
                            nl = i;
                            break;
                        }
                    }
                    
                    if (pendingCr)
                    {
                        if (nl != _bytePos)
                        {
                            sb.Append((byte)'\r');
                            any = true;
                        }
                        pendingCr = false;
                    }

                    if (nl >= 0)
                    {
                        var len = nl - _bytePos;
                        if (len > 0 && _byteBuffer[nl - 1] == (byte)'\r')
                            len -= 1;

                        if (len > 0)
                            sb.Append(new ReadOnlySpan<byte>(_byteBuffer + _bytePos, len));

                        _bytePos = nl + 1;
                        return true;
                    }
                    
                    var segLen = _byteLen - _bytePos;
                    if (segLen > 0 && _byteBuffer[_byteLen - 1] == (byte)'\r')
                    {
                        segLen -= 1;
                        pendingCr = true;
                    }

                    if (segLen > 0)
                    {
                        sb.Append(new ReadOnlySpan<byte>(_byteBuffer + _bytePos, segLen));
                        any = true;
                    }

                    _bytePos = _byteLen;
                }
            }
        }
        
        /// <summary>重设两级缓冲容量；分配失败时回收已分配内存后抛出，并清零读位置。</summary>
        internal unsafe void SetBufferSize(int charSize = 512, int byteSize = 4096)
        {
            if (charSize < 1)
                throw new ArgumentOutOfRangeException(nameof(charSize), charSize, "The character buffer size must be greater than 0.");
            if (byteSize < 1)
                throw new ArgumentOutOfRangeException(nameof(byteSize), byteSize, "The byte buffer size must be greater than 0.");

            if (_disposed) return;

            lock (_lock)
            {
                var newCharBuffer = (char*)NativeMemory.Alloc((nuint)charSize * sizeof(char));
                byte* newByteBuffer;
                try
                {
                    newByteBuffer = (byte*)NativeMemory.Alloc((nuint)byteSize);
                }
                catch
                {
                    NativeMemory.Free(newCharBuffer);
                    throw;
                }
            
                if (_charBuffer != null) NativeMemory.Free(_charBuffer);
                if (_byteBuffer != null) NativeMemory.Free(_byteBuffer);
            
                _charBuffer = newCharBuffer;
                _byteBuffer = newByteBuffer;
                _charSize = charSize;
                _byteSize = byteSize;
                _charPos = 0;
                _bytePos = 0;
            }
        }
    }
}
