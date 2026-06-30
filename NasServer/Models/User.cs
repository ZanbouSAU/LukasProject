// NasServer/Models/User.cs

using System;

namespace NasServer.Models;

/// <summary>
/// 用户实体模型
/// </summary>
public sealed class User
{
    /// <summary>
    /// 用户唯一标识符
    /// </summary>
    public Guid Id { get; init; }
    
    /// <summary>
    /// 用户邮箱地址
    /// </summary>
    public string Email { get; init; } = string.Empty;
    
    /// <summary>
    /// 密码哈希值（字节数组格式）
    /// </summary>
    public byte[] PasswordHash { get; init; } = [];
    
    /// <summary>
    /// 用户全名（可选）
    /// </summary>
    public string? FullName { get; init; }
    
    /// <summary>
    /// 用户账户创建时间（UTC时间）
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
