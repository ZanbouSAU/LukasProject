// Lukas/Io.OutBase.cs

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 同步带缓冲的输出端，直接写裸句柄（Windows 用 WriteFile，其余平台用 write）。
    ///
    /// 内部维护两级缓冲：字符缓冲 <c>_charBuffer</c> 先攒 char，再按 UTF-8 转码进字节缓冲 <c>_byteBuffer</c>，
    /// 满了或显式 Flush 时才真正落盘。写入超过缓冲容量的数据会绕过缓冲直接写，避免无谓拷贝。
    /// 所有公开操作都在 <see cref="_lock"/> 下进行，可多线程安全调用。
    /// </summary>
    internal partial class OutBase
    {
        private readonly Lock _lock = new();
        
        private bool _enableAutoFlush;

        private bool _disposed;

        private readonly nint _handle;

        /// <summary>
        /// 输出基础类的构造函数，获取 handle 并分配缓冲区
        /// </summary>
        /// <param name="handle">文件描述符/句柄</param>
        /// <param name="charSize">字符缓冲区大小</param>
        /// <param name="byteSize">字节缓冲区大小</param>
        internal unsafe OutBase(nint handle, int charSize = 512, int byteSize = 4096)
        {
            _handle = handle;
            _charSize = charSize;
            _charBuffer = (char*)NativeMemory.Alloc((nuint)_charSize * sizeof(char));

            _byteSize = byteSize;
            _byteBuffer = (byte*)NativeMemory.Alloc((nuint)_byteSize);
        }
        
        // 终结器：尽量冲刷残留数据并释放原生缓冲；拿不到锁就只释放内存，不强行冲刷。
        unsafe ~OutBase()
        {
            var lockTaken = false;
            try
            {
                lockTaken = _lock.TryEnter();

                if (lockTaken && !_disposed)
                {
                    try
                    {
                        CharFlush();
                        ByteFlush();
                    }
                    catch { /* 释放时忽略异常 */ }
                }

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
        
        /// <summary>
        /// 析构函数，同时调用析构内部方法和 GC 释放方法
        /// </summary>
        internal void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 析构内部方法，负责清空缓冲区并释放内存
        /// </summary>
        private unsafe void DisposeCore()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                try
                {
                    CharFlush();
                }
                catch { /* 释放时忽略异常 */ }

                try
                {
                    ByteFlush();
                }
                catch { /* 释放时忽略异常 */ }
            
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
        
        /// <summary>
        /// 数据若不小于字节缓冲容量则先冲刷缓冲再直写，否则攒进缓冲。
        /// </summary>
        /// 
        /// <param name="value">要写入的数据</param>
        /// <param name="isLine">为真时追加换行符 <c>0x0A</c>。</param>
        internal unsafe void Write(ReadOnlySpan<byte> value, bool isLine = false)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    static void ProcessData(ReadOnlySpan<byte> data, OutBase self)
                    {
                        if (data.IsEmpty)
                            return;

                        if (self._enableByteBuffer)
                        {
                            if (data.Length >= self._byteSize)
                            {
                                self.ByteFlush();
                                self.WriteAllDirect(data);
                                return;
                            }
                            if (self._bytePos + data.Length > self._byteSize)
                                self.ByteFlush();

                            data.CopyTo(new Span<byte>(self._byteBuffer + self._bytePos, data.Length));
                            self._bytePos += data.Length;
                        }
                        else
                        {
                            self.ByteFlush();
                            self.WriteAllDirect(data);
                        }
                    }

                    if (isLine)
                    {
                        var lineLength = value.Length + 1;

                        // 行数据较小时用栈缓冲拼接「内容+换行」，过大则临时在本地堆上分配，避免大栈分配。
                        if (lineLength <= _byteSize && lineLength <= 4096)
                        {
                            Span<byte> lineBuffer = stackalloc byte[lineLength];
                            value.CopyTo(lineBuffer);
                            lineBuffer[value.Length] = 0x0A;
                            ProcessData(lineBuffer, this);
                        }
                        else
                        {
                            var lineBuffer = (byte*)NativeMemory.Alloc((nuint)lineLength);
                            try
                            {
                                value.CopyTo(new Span<byte>(lineBuffer, value.Length));
                                lineBuffer[value.Length] = 0x0A;
                                var lineData = new ReadOnlySpan<byte>(lineBuffer, lineLength);
                                ProcessData(lineData, this);
                            }
                            finally
                            {
                                NativeMemory.Free(lineBuffer);
                            }
                        }

                        if (_enableAutoFlush)
                            ByteFlush();
                    }
                    else
                    {
                        ProcessData(value, this);
                    }
                }
                else
                {
                    throw new ObjectDisposedException(nameof(OutBase));
                }
            }
        }
        
        /// <summary>循环调用底层 write，直到整段数据写完；任何一次返回非正值即视为失败。</summary>
        ///
        /// <param name="data">要写入的数据</param>
        private unsafe void WriteAllDirect(ReadOnlySpan<byte> data)
        {
            fixed (byte* basePtr = data)
            {
                var offset = 0;
                while (offset < data.Length)
                {
                    var written = OperatingSystem.IsWindows()
                        ? Pal.WriteFile(_handle, basePtr + offset, data.Length - offset)
                        : Pal.Write(_handle, basePtr + offset, data.Length - offset);
                    if (written <= 0)
                        throw new IOException("Failed to write.");

                    offset += written;
                }
            }
        }
        
        /// <summary>
        /// 写入一段字符；内部转入字符缓冲并按需转码、冲刷。
        /// </summary>
        ///
        /// <param name="value">要写入的数据</param>
        /// <param name="isLine">为真时追加换行符 <c>0x0A</c>。</param>
        internal unsafe void Write(ReadOnlySpan<char> value, bool isLine = false)
        {
            if (value.IsEmpty)
            {
                if (isLine)
                    Write(ReadOnlySpan<byte>.Empty, isLine: true);
                return;
            }

            lock (_lock)
            {
                fixed (char* chars = value)
                {
                    WriteChars(chars, value.Length, isLine);
                }
            }
        }
        
        /// <summary>把字符缓冲和字节缓冲全部冲刷落盘。</summary>
        internal void FlushCore()
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                CharFlush();
                ByteFlush();
            }
        }
        
        /// <summary>
        /// 重设两级缓冲的容量。会先冲刷旧缓冲，再分配新缓冲；
        /// 若字节缓冲分配失败，会回收已分配的字符缓冲后抛出，不留下半残状态。
        /// </summary>
        ///
        /// <param name="charSize">字符缓冲区大小</param>
        /// <param name="byteSize">字节缓冲区大小</param>
        internal unsafe void SetBufferSize(int charSize = 512, int byteSize = 4096)
        {
            if (charSize < 1)
                throw new ArgumentOutOfRangeException(nameof(charSize), charSize, "The character buffer size must be greater than 0.");
            if (byteSize < 1)
                throw new ArgumentOutOfRangeException(nameof(byteSize), byteSize, "The byte buffer size must be greater than 0.");

            if (_disposed)
                return;

            lock (_lock)
            {
                CharFlush();
                ByteFlush();
                
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
        
        /// <summary>开启后每写完一行就立即落盘。</summary>
        ///
        /// <param name="enableAutoFlush">开关自动冲刷</param>
        internal void EnableAutoFlush(bool enableAutoFlush)
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                _enableAutoFlush = enableAutoFlush;
            }
        }
    }
}
