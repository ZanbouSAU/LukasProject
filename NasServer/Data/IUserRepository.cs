// NasServer/Data/IUserRepository.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using NasServer.Models;

namespace NasServer.Data;

public interface IUserRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task AddUserAsync(User user, CancellationToken ct = default);

    Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>按令牌哈希查找刷新令牌及其所属用户。</summary>
    Task<(RefreshToken Token, User User)?> FindRefreshTokenWithUserAsync(
        string tokenHash, CancellationToken ct = default);

    Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken ct = default);

    /// <summary>吊销某用户所有未吊销的刷新令牌（登出所有设备 / 令牌疑似泄露时的应急措施）。</summary>
    Task RevokeAllRefreshTokensAsync(Guid userId, CancellationToken ct = default);
}
