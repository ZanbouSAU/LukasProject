// NasServer/Services/FileServiceException.cs

using System;

namespace NasServer.Services;

/// <summary>文件服务的领域错误类别，由端点层映射为对应的 HTTP 状态码。</summary>
public enum FileErrorKind
{
    BadRequest,
    NotFound,
    Conflict,
    PayloadTooLarge,
    UnsupportedMedia
}

/// <summary>
/// 文件服务在业务校验失败时抛出的领域异常（路径非法、文件冲突、超限等）。
/// 与 <see cref="AuthException"/> 同构：业务逻辑只管抛，端点层统一捕获并转成 <c>IResult</c>。
/// 走异常而非 Result&lt;T&gt;，是因为这些都是非正常路径——正常上传/下载零异常开销，
/// 同时保持与既有 AuthService 一致的错误风格，便于理解。
/// </summary>
public sealed class FileServiceException(FileErrorKind kind, string message) : Exception(message)
{
    public FileErrorKind Kind { get; } = kind;
}
