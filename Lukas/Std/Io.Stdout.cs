// Lukas/Io.Stdout.cs

using System;
using System.Buffers;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>标准输出（stdout）的静态门面，转发到一个进程级共享的 <see cref="OutBase"/> 实例。</summary>
    public static class Stdout
    {
        /// <summary>
        /// 创建 <see cref="Io.OutBase"/> 类，引入全局缓冲机制，负责标准控制台输出的管理和优化
        /// </summary>
        /// 
        /// <remarks>
        /// 该类通过全局缓冲减少频繁的 I/O 操作，提升控制台输出性能
        /// </remarks>
        private static readonly OutBase OutBase = new(Pal.StdOutHandle);

        /// <summary>
        /// 接收 Span{byte}, ReadOnlySpan{byte} 和 byte[] 类型进行控制台输出
        /// </summary>
        /// 
        /// <param name="value">要输出的字节</param>
        /// <param name="isLine">是否换行</param>
        public static void Write(ReadOnlySpan<byte> value, bool isLine = false)
            => OutBase.Write(value, isLine);
        
        /// <summary>
        /// 接收 Span{char}, ReadOnlySpan{char}, char[] 和 string 类型进行控制台输出
        /// </summary>
        /// 
        /// <param name="value">要输出的字符</param>
        /// <param name="isLine">是否换行</param>
        public static void Write(ReadOnlySpan<char> value, bool isLine = false)
            => OutBase.Write(value, isLine);
        
        /// <summary>
        /// 要求类型支持/继承自 <see cref="IUtf8SpanFormattable"/>
        /// 直接把可格式化值写成 UTF-8，避免装箱与中间字符串。
        /// 依次尝试 256 字节栈缓冲 → 2048 字节栈缓冲 → 4096 字节池化缓冲，都放不下才退回 ToString()。
        /// </summary>
        ///
        /// <param name="value">要输出的值</param>
        /// <param name="isLine">是否换行</param>
        public static void Write<T>(T value, bool isLine = false) where T : IUtf8SpanFormattable
        {
            Span<byte> bytes = stackalloc byte[256];
            if (value.TryFormat(bytes, out var written, default, null))
            {
                OutBase.Write(bytes[..written], isLine);
                return;
            }

            const int maxStackAlloc = 2048;
            if (maxStackAlloc > 256)
            {
                Span<byte> largerStackBuffer = stackalloc byte[maxStackAlloc];
                if (value.TryFormat(largerStackBuffer, out written, default, null))
                {
                    OutBase.Write(largerStackBuffer[..written], isLine);
                    return;
                }
            }

            byte[]? rentedBuffer = null;
            try
            {
                const int defaultRentSize = 4096;
                rentedBuffer = ArrayPool<byte>.Shared.Rent(defaultRentSize);
                var pooledSpan = rentedBuffer.AsSpan(0, defaultRentSize);
        
                if (value.TryFormat(pooledSpan, out written, default, null))
                {
                    OutBase.Write(pooledSpan[..written], isLine);
                    return;
                }
            
                OutBase.Write(value.ToString(), isLine);
            }
            finally
            {
                if (rentedBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
            }
        }

        /// <summary>
        /// 一切不确定的 <see cref="object"/>? 类型走此重载，
        /// 将对象通过 <see cref="object.ToString()"/> 处理移交给
        /// <see cref="Io.Stdout.Write(string?, bool)"/> 重载处理
        /// </summary>
        /// 
        /// <param name="value">要输出的对象</param>
        /// <param name="isLine">是否换行</param>
        public static void Write(object? value, bool isLine = false)
        {
            if (value is null)
                return;
            
            Write(value.ToString(), isLine);
        }

        /// <summary>
        /// 将 <see cref="string"/> 转换为 <see cref="ReadOnlySpan{char}"/> 交给 
        /// <see cref="OutBase.Write(ReadOnlySpan{byte}, bool)"/> 处理，
        /// 无须经过 <see cref="Io.Stdout.Write(ReadOnlySpan{char}, bool)"/> 重载
        /// </summary>
        /// 
        /// <param name="value">要输出的字符串</param>
        /// <param name="isLine">是否换行</param>
        public static void Write(string? value, bool isLine = false)
        {
            if (value is null)
                return;
            
            OutBase.Write(value.AsSpan(), isLine);
        }

        /// <summary>
        /// 写入一个新的空行
        /// </summary>
        public static void WriteLine()
            => OutBase.Write(ReadOnlySpan<byte>.Empty, isLine: true);

        /// <summary>刷新缓冲区</summary>
        public static void Flush()
            => OutBase.FlushCore();

        /// <summary>设置缓冲区大小</summary>
        /// 
        /// <param name="charSize">字符缓冲区</param>
        /// <param name="byteSize">字节缓冲区</param>
        public static void SetBuffer(int charSize = 512, int byteSize = 4096)
            => OutBase.SetBufferSize(charSize, byteSize);

        /// <summary>启用字节缓冲区</summary>
        /// 
        /// <param name="enableByteBuffer">是否启用</param>
        public static void EnableByteBuffer(bool enableByteBuffer = true)
            => OutBase.EnableByteBuffer(enableByteBuffer);

        /// <summary>启用字符缓冲区</summary>
        /// 
        /// <param name="enableByteBuffer">是否启用</param>
        public static void EnableCharBuffer(bool enableByteBuffer = true)
            => OutBase.EnableCharBuffer(enableByteBuffer);

        /// <summary>启用缓冲区每次输出后自动刷新</summary>
        /// 
        /// <param name="enableAutoFlush">是否启用</param>
        public static void EnableAutoFlush(bool enableAutoFlush)
            => OutBase.EnableAutoFlush(enableAutoFlush);
    }
}
