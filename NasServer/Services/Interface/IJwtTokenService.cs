// NasServer/Services/Interface/IJwtTokenService.cs

using System;
using NasServer.Models;

namespace NasServer.Services.Interface;

public interface IJwtTokenService
{
    string GenerateToken(User user);
    string GenerateRefreshToken();

    /// <summary>签发一次性下载票据（短时效 JWT，绑定用户、相对路径与处置方式）。</summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="relativePath">相对路径</param>
    /// <param name="inline">true=内联预览（Content-Disposition: inline）；false=附件下载。</param>
    string GenerateDownloadTicket(Guid userId, string relativePath, bool inline);

    /// <summary>校验下载票据，成功时取出用户 Id、相对路径与处置方式。</summary>
    bool TryValidateDownloadTicket(string ticket, out Guid userId, out string relativePath, out bool inline);
}
