// NasServer/Configuration/JwtSettings.cs

namespace NasServer.Configuration;

/// <summary>
/// JWT 配置设置类
/// </summary>
public class JwtSettings
{
    /// <summary>
    /// JWT 签名密钥（用于生成和验证令牌）
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// JWT 颁发者标识
    /// </summary>
    public string Issuer { get; set; } = "NasServer";
    
    /// <summary>
    /// JWT 受众标识（客户端标识）
    /// </summary>
    public string Audience { get; set; } = "Client";
    
    /// <summary>
    /// 访问令牌过期时间（分钟）
    /// </summary>
    public int AccessTokenExpirationMinutes { get; set; } = 15;
    
    /// <summary>
    /// 刷新令牌过期时间（天）
    /// </summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
