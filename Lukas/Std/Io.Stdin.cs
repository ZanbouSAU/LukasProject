// Lukas/Io.Stdin.cs

using System;
using Lukas.Str;

namespace Lukas.Std;

public static partial class Io
{
    /// <summary>标准输入（stdin）的静态门面，转发到一个进程级共享的 <see cref="InBase"/> 实例。</summary>
    public static class Stdin
    {
        private static readonly InBase InBase = new(Pal.StdInHandle);

        public static int Read()
        {
            return InBase.Read();
        }

        public static int Read(Span<byte> buffer)
        {
            return InBase.Read(buffer);
        }
        
        public static int Read(Span<char> buffer)
        {
            return InBase.Read(buffer);
        }
        
        public static string? ReadLine()
        {
            return InBase.ReadLine();
        }

        public static bool ReadLine(ref Utf8StringBuilder sb)
        {
            return InBase.ReadLine(ref sb);
        }

        /// <summary>把标准输入剩余的全部数据读入 <paramref name="sb"/>，返回读取的字节数。</summary>
        public static int ReadToEnd(ref Utf8StringBuilder sb)
        {
            return InBase.Read(ref sb);
        }
        
        public static void SetBuffer(int charSize = 512, int byteSize = 4096)
            => InBase.SetBufferSize(charSize, byteSize);
    }
}
