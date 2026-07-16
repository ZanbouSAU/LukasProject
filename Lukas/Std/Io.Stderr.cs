// Lukas/Io.Stderr.cs

using System;
using System.Buffers;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>
    /// 标准错误（stderr）的静态门面。默认开启自动冲刷，使错误信息即时可见、不滞留在缓冲里。
    /// </summary>
    public static class Stderr
    {
        private static readonly OutBase OutBase = new(Pal.StdErrHandle);

        static Stderr()
        {
            EnableAutoFlush();   // stderr 默认即时冲刷
        }
        
        public static void Write(ReadOnlySpan<byte> value, bool isLine = false)
            => OutBase.Write(value, isLine);
        
        public static void Write(ReadOnlySpan<char> value, bool isLine = false)
            => OutBase.Write(value, isLine);
        
        /// <summary>把可格式化值直接写成 UTF-8；缓冲策略同 <see cref="Stdout.Write{T}"/>。</summary>
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
        
        public static void Write(object? value, bool isLine = false)
        {
            if (value is null)
                return;
            
            Write(value.ToString(), isLine);
        }
        
        public static void Write(string? value, bool isLine = false)
        {
            if (value is null)
                return;
            
            OutBase.Write(value.AsSpan(), isLine);
        }

        public static void WriteLine(ReadOnlySpan<byte> value)
            => Write(value, isLine: true);
        
        public static void WriteLine(ReadOnlySpan<char> value)
            => Write(value, isLine: true);
        
        public static void WriteLine<T>(T value) where T : IUtf8SpanFormattable
            => Write(value, isLine: true);
        
        public static void WriteLine(object? value)
            => Write(value, isLine: true);
        
        public static void WriteLine(string? value)
            => Write(value, isLine: true);

        public static void WriteLine()
            => OutBase.Write(ReadOnlySpan<byte>.Empty, isLine: true);
        
        public static void Flush()
            => OutBase.FlushCore();

        public static void SetBuffer(int charSize = 512, int byteSize = 4096)
            => OutBase.SetBufferSize(charSize, byteSize);

        public static void EnableByteBuffer(bool enableByteBuffer = true)
            => OutBase.EnableByteBuffer(enableByteBuffer);
        
        public static void EnableCharBuffer(bool enableByteBuffer = true)
            => OutBase.EnableCharBuffer(enableByteBuffer);
        
        public static void EnableAutoFlush(bool enableAutoFlush = true)
            => OutBase.EnableAutoFlush(enableAutoFlush);
    }
}
