// NasServer/Endpoints/CurrentUser.cs

using System;
using Microsoft.AspNetCore.Http;

namespace NasServer.Endpoints;

/// <summary>从已通过 JWT 认证的请求中取出用户 Id（"sub" 声明）。</summary>
public static class CurrentUser
{
    public static bool TryGetUserId(HttpContext context, out Guid userId)
    {
        userId = Guid.Empty;
        var sub = context.User.FindFirst("sub")?.Value
                  ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return sub is not null && Guid.TryParse(sub, out userId);
    }
}
