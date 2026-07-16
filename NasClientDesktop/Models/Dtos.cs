// NasClientDesktop/Models/Dtos.cs
// 与 NasServer 的 DTO 一一对应。服务端 JSON 为 camelCase（见 NasServer 的 AppJsonSerializerContext），
// 这里通过源生成上下文 AppJsonContext 上的 PropertyNamingPolicy=CamelCase 自动匹配，无需逐字段标注。

using System;
using System.Collections.Generic;

namespace NasClientDesktop.Models;

// ---------------------------------------------------------------- 认证

/// <summary>注册请求。密码为明文字符串（HTTPS 传输）。
/// 注意：服务端把该字段声明为 byte[] + Utf8PasswordConverter，但该转换器从 JSON「字符串」读取，
/// 故客户端发送 string 在线上格式完全一致，不可改为 byte[]（会被序列化成 JSON 数组而不被接受）。</summary>
public sealed record RegisterRequest(
    string Email,
    string Password,
    string ConfirmPassword,
    string? FullName);

/// <summary>登录请求。密码字段同 RegisterRequest 的说明：以 JSON 字符串发送。</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>刷新令牌请求。</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>登出请求（吊销指定刷新令牌）。</summary>
public sealed record LogoutRequest(string RefreshToken);

/// <summary>认证响应。</summary>
public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    string Email);

// ---------------------------------------------------------------- 通用

/// <summary>通用消息响应。</summary>
public sealed record MessageResponse(string Message);

/// <summary>错误响应。</summary>
public sealed record ErrorResponse(string Message);

// ---------------------------------------------------------------- 文件

/// <summary>目录列表中的一项。</summary>
public sealed record FileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime ModifiedAtUtc);

/// <summary>目录列表响应。</summary>
public sealed record FileListResponse(string Path, List<FileEntry> Entries);

/// <summary>创建目录请求。</summary>
public sealed record MkDirRequest(string Path);

/// <summary>单文件上传/文本保存的响应。</summary>
public sealed record UploadResponse(string Path, long Size);

/// <summary>zip 目录上传的响应。</summary>
public sealed record UploadZipResponse(string Path, int Files, int Directories, long TotalBytes);

/// <summary>下载票据响应。</summary>
public sealed record DownloadTicketResponse(string Ticket);

/// <summary>保存文本请求。</summary>
public sealed record SaveTextRequest(string Path, string Content);

/// <summary>在线读取文本响应。</summary>
public sealed record TextContentResponse(string Path, string Content, long Size);

/// <summary>新建空文件请求。</summary>
public sealed record NewFileRequest(string Path);

/// <summary>移动 / 重命名请求（传完整目标路径）。</summary>
public sealed record MoveRequest(string Source, string Dest, bool Overwrite);

/// <summary>复制请求（传完整目标路径，文件或目录）。</summary>
public sealed record CopyRequest(string Source, string Dest, bool Overwrite);
