// NasClientDesktop/Serialization/AppJsonContext.cs
// Native AOT 下反射序列化不可用，必须使用源生成的 JsonSerializerContext。
// PropertyNamingPolicy=CamelCase 让 C# 的 PascalCase 属性与服务端 camelCase JSON 对齐。
// 每个跨网络的类型（含集合形态）都要在此登记，漏登记会在运行时抛 NotSupportedException。

using System.Text.Json.Serialization;
using NasClientDesktop.Models;

namespace NasClientDesktop.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
// 认证
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(AuthResponse))]
// 通用
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(ErrorResponse))]
// 文件
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(System.Collections.Generic.List<FileEntry>))]
[JsonSerializable(typeof(FileListResponse))]
[JsonSerializable(typeof(MkDirRequest))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(UploadZipResponse))]
[JsonSerializable(typeof(DownloadTicketResponse))]
[JsonSerializable(typeof(SaveTextRequest))]
[JsonSerializable(typeof(NewFileRequest))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(CopyRequest))]
[JsonSerializable(typeof(TextContentResponse))]
public partial class AppJsonContext : JsonSerializerContext;
