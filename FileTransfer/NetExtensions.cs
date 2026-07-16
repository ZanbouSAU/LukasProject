// FileTransfer/NetExtensions.cs

using System;
using System.Buffers.Binary;

namespace FileTransfer;

internal static class NetExtensions
{
    extension(ushort value)
    {
        public ushort Htons
            => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

        public ushort Ntohs
            => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    extension(uint value)
    {
        public uint Htonl
            => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

        public uint Ntohl
            => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }
}
