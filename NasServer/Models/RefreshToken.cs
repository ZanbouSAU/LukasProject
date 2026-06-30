// NasServer/Models/RefreshToken.cs

using System;

namespace NasServer.Models;

/// <summary>
/// 刷新令牌（持久化形态）。数据库中只保存令牌的 SHA-256 哈希（<see cref="TokenHash"/>），
/// 即便数据库泄露，攻击者也无法用库中数据直接换取访问令牌。
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; init; }
    public string TokenHash { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public DateTime ExpiresAt { get; init; }
    public bool IsRevoked { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
