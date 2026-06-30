// NasServer/Serialization/AppJsonSerializerContext.cs

using System.Text.Json.Serialization;
using NasServer.DTOs;

namespace NasServer.Serialization;

/// <summary>
/// 应用程序 JSON 序列化上下文
/// 提供源生成器方式的 JSON 序列化支持，提高性能并避免运行时反射
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(Utf8PasswordConverter)])]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(MkDirRequest))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileListResponse))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(UploadZipResponse))]
[JsonSerializable(typeof(DownloadTicketResponse))]
[JsonSerializable(typeof(TextContentResponse))]
[JsonSerializable(typeof(SaveTextRequest))]
[JsonSerializable(typeof(NewFileRequest))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(CopyRequest))]
public partial class AppJsonSerializerContext : JsonSerializerContext;
