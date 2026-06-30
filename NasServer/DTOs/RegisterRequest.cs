// NasServer/DTOs/RegisterRequest.cs

using System.Text.Json.Serialization;
using NasServer.Serialization;

namespace NasServer.DTOs;

/// <summary>
/// 注册请求。邮箱/姓名按文本处理（需归一化、长度按字符数校验、落库为文本列）；
/// 密码以 UTF-8 字节承载（见 <see cref="Utf8PasswordConverter"/>），AuthService 用完即清零，
/// 尽量缩短明文在内存中的存活时间。
/// </summary>
public record RegisterRequest(
    string Email,
    [property: JsonConverter(typeof(Utf8PasswordConverter))] byte[] Password,
    [property: JsonConverter(typeof(Utf8PasswordConverter))] byte[] ConfirmPassword,
    string? FullName = null
);
