// NasServer/DTOs/AuthResponse.cs

using System;

namespace NasServer.DTOs;

/// <summary>
/// 认证响应数据传输对象
/// </summary>
public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    string Email
);
