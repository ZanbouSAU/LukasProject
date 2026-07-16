// NasServer/Services/Interface/IAuthService.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using NasServer.DTOs;

namespace NasServer.Services.Interface;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>登出：吊销调用者提交的刷新令牌（必须属于该用户本人）。幂等。</summary>
    Task LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default);

    /// <summary>登出所有设备：吊销该用户全部刷新令牌。</summary>
    Task LogoutAllAsync(Guid userId, CancellationToken ct = default);
}
