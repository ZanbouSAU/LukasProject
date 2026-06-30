// NasServer/Services/JwtTokenService.cs

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NasServer.Configuration;
using NasServer.Models;
using NasServer.Services.Interface;

namespace NasServer.Services;

public sealed class JwtTokenService(IOptions<JwtSettings> jwtOptions) : IJwtTokenService
{
    private readonly JwtSettings _jwt = jwtOptions.Value;
    private static readonly JsonWebTokenHandler Handler = new();

    public string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            Expires = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpirationMinutes),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Email] = user.Email,
                [JwtRegisteredClaimNames.Name] = user.FullName ?? string.Empty,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            }
        };

        return Handler.CreateToken(descriptor);
    }

    public string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    // 下载票据：与访问令牌同密钥签名，但用专属 audience（"download"）隔离用途，
    // 时效很短（2 分钟，仅够浏览器发起下载），并把目标相对路径写进自定义声明 "fp"，
    // 处置方式（内联预览/附件下载）写进 "dp"。
    private const string DownloadAudience = "download";
    private const string PathClaim = "fp";
    private const string DispositionClaim = "dp";

    public string GenerateDownloadTicket(Guid userId, string relativePath, bool inline)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = DownloadAudience,
            Expires = DateTime.UtcNow.AddMinutes(2),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [PathClaim] = relativePath,
                [DispositionClaim] = inline ? "inline" : "attachment",
            }
        };

        return Handler.CreateToken(descriptor);
    }

    public bool TryValidateDownloadTicket(string ticket, out Guid userId, out string relativePath, out bool inline)
    {
        userId = Guid.Empty;
        relativePath = string.Empty;
        inline = false;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwt.Issuer,
            ValidAudience = DownloadAudience,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.Zero,
        };

        var result = Handler.ValidateTokenAsync(ticket, parameters).GetAwaiter().GetResult();
        if (!result.IsValid)
            return false;

        var sub = result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var subValue)
            ? subValue as string
            : null;
        var path = result.Claims.TryGetValue(PathClaim, out var pathValue)
            ? pathValue as string
            : null;

        if (sub is null || path is null || !Guid.TryParse(sub, out userId))
            return false;

        inline = result.Claims.TryGetValue(DispositionClaim, out var dpValue)
                 && dpValue as string == "inline";
        relativePath = path;
        return true;
    }
}
