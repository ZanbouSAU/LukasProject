// NasServer/DTOs/LoginRequest.cs

using System.Text.Json.Serialization;
using NasServer.Serialization;

namespace NasServer.DTOs;

/// <summary>登录请求。密码以 UTF-8 字节承载（见 <see cref="Utf8PasswordConverter"/>），用完即清零。</summary>
public record LoginRequest(
    string Email,
    [property: JsonConverter(typeof(Utf8PasswordConverter))] byte[] Password
);
