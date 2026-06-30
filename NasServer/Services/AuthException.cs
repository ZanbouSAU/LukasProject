// NasServer/Services/AuthException.cs

using System;

namespace NasServer.Services;

/// <summary>
/// 认证错误类型枚举
/// </summary>
public enum AuthErrorKind
{
    BadRequest,      // 错误请求（400）
    Unauthorized,    // 未授权（401）
    Conflict         // 冲突（409）- 通常用于邮箱已存在等情况
}

/// <summary>
/// 认证异常类，用于处理认证过程中的业务异常
/// </summary>
public sealed class AuthException(AuthErrorKind kind, string message) : Exception(message)
{
    /// <summary>
    /// 获取认证错误类型
    /// </summary>
    public AuthErrorKind Kind { get; } = kind;
}
