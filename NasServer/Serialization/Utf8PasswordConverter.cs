// NasServer/Serialization/Utf8PasswordConverter.cs

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasServer.Serialization;

/// <summary>
/// 把 JSON 字符串里的密码直接读成 UTF-8 字节（<c>byte[]</c>），全程不经过 <see cref="string"/>。
///
/// 动机：<see cref="string"/> 不可变、会被 GC 搬迁、无法主动清零，密码明文一旦成为 string
/// 便可能在托管堆/内存转储中长期残留。改由 <see cref="Utf8JsonReader"/> 直接拷贝 UTF-8 字节到
/// 调用方可控、可 <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory(System.Span{byte})"/>
/// 清零的 <c>byte[]</c>，把明文的内存暴露面压到最小。
///
/// 该转换器只读不写：密码绝不会被序列化回响应体。
/// </summary>
public sealed class Utf8PasswordConverter : JsonConverter<byte[]>
{
    // 防御性上限：避免恶意特大字符串撑爆内存（远大于业务允许的密码长度，真正的长度校验在 AuthService）。
    private const int MaxBytes = 4096;

    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("密码字段必须是 JSON 字符串。");

        // 无转义、单段时：ValueSpan 就是请求体里这段字符串的原始 UTF-8 字节，直接拷贝。
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
        {
            var span = reader.ValueSpan;
            return span.Length > MaxBytes ? throw new JsonException("密码过长。") : span.ToArray();
        }

        // 有转义（如 \n、\uXXXX）或跨缓冲分段时：先按未转义字节长度的上界分配缓冲
        // （解码后只会更短或相等），再用 CopyString 解码。这样 CopyString 不会因目标过小而抛异常。
        var maxLen = reader.HasValueSequence
            ? reader.ValueSequence.Length
            : reader.ValueSpan.Length;
        if (maxLen > MaxBytes)
            throw new JsonException("密码过长。");

        var rented = ArrayPool<byte>.Shared.Rent((int)maxLen);
        try
        {
            var written = reader.CopyString(rented); // 写入解码后的 UTF-8 字节，返回字节数
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            // 临时缓冲可能残留明文，归还前清零。
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        => throw new NotSupportedException("密码不可被序列化输出。");
}
