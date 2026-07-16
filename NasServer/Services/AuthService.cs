// NasServer/Services/AuthService.cs

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NasServer.Configuration;
using NasServer.Data;
using NasServer.DTOs;
using NasServer.Models;
using NasServer.Services.Interface;
using Npgsql;
using Lukas.Security;
using Lukas.Std;

namespace NasServer.Services;

public sealed class AuthService(
    IUserRepository repository,
    IJwtTokenService jwtTokenService,
    IOptions<JwtSettings> jwtOptions) : IAuthService
{
    private readonly JwtSettings _jwt = jwtOptions.Value;

    /// <summary>BCrypt 工作因子；12 在 2026 年的硬件上约几十毫秒/次，可有效抵御离线爆破。</summary>
    private const int BCryptWorkFactor = 12;

    // 密码长度按 UTF-8 字节计（BCrypt 本身在字节上工作）。8~128 字节对 ASCII 即 8~128 字符；
    // 含多字节字符时字节数更多，但区间足够宽松，不会误伤正常口令。
    private const int MinPasswordBytes = 8;
    private const int MaxPasswordBytes = 128;
    private const int MaxEmailLength = 254;
    private const int MaxFullNameLength = 50;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        try
        {
#if DEBUG
            Log.Info($"【AuthService.RegisterAsync】进入异步注册方法，Email={request.Email}");
#endif
            var email = NormalizeEmail(request.Email);
            ValidateEmail(email);
            ValidatePassword(request.Password);

            // 定长字节比对，避免把密码转成 string 比较。
            if (!CryptographicOperations.FixedTimeEquals(request.Password, request.ConfirmPassword))
            {
#if DEBUG
                Log.Info("【AuthService.RegisterAsync】两次密码不一致，抛出 BadRequest");
#endif
                throw new AuthException(AuthErrorKind.BadRequest, "两次密码不一致");
            }

            var fullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName.Trim();
            if (fullName is { Length: > MaxFullNameLength })
            {
#if DEBUG
                Log.Info($"【AuthService.RegisterAsync】姓名长度 {fullName.Length} 超过上限 {MaxFullNameLength}，抛出 BadRequest");
#endif
                throw new AuthException(AuthErrorKind.BadRequest, $"姓名长度不能超过 {MaxFullNameLength} 个字符");
            }

            if (await repository.EmailExistsAsync(email, ct))
            {
#if DEBUG
                Log.Info($"【AuthService.RegisterAsync】邮箱 {email} 已被注册，抛出 Conflict");
#endif
                throw new AuthException(AuthErrorKind.Conflict, "该邮箱已被注册");
            }

#if DEBUG
            Log.Info($"【AuthService.RegisterAsync】执行 BCrypt 加密，Email={email}");
#endif
            var passwordHash = HashPassword(request.Password);

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                PasswordHash = passwordHash,
                FullName = fullName,
                CreatedAt = DateTime.UtcNow
            };

#if DEBUG
            Log.Info($"【AuthService.RegisterAsync】用户对象创建完成，UserId={user.Id}，Email={user.Email}");
#endif

            try
            {
                await repository.AddUserAsync(user, ct);
#if DEBUG
                Log.Info($"【AuthService.RegisterAsync】用户已添加到数据库，UserId={user.Id}");
#endif
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // 并发注册同一邮箱时由数据库唯一索引兜底，转译成业务冲突。
#if DEBUG
                Log.Info("【AuthService.RegisterAsync】数据库唯一约束冲突，抛出 Conflict");
#endif
                throw new AuthException(AuthErrorKind.Conflict, "该邮箱已被注册");
            }

            var accessToken = jwtTokenService.GenerateToken(user);
            var refreshToken = await CreateAndStoreRefreshTokenAsync(user.Id, ct);

#if DEBUG
            Log.Info($"【AuthService.RegisterAsync】返回异步注册成功，UserId={user.Id}，Email={user.Email}");
#endif
            return BuildResponse(accessToken, refreshToken, user.Email);
        }
        finally
        {
#if DEBUG
            Log.Info("【AuthService.RegisterAsync】清空密码明文（ArrayPool）");
#endif
            // 无论成功失败，用完立刻擦除内存中的密码明文。
            CryptographicOperations.ZeroMemory(request.Password);
            CryptographicOperations.ZeroMemory(request.ConfirmPassword);
        }
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        try
        {
#if DEBUG
            Log.Info($"【AuthService.LoginAsync】进入异步登录方法，Email={request.Email}");
#endif
            var email = NormalizeEmail(request.Email);
            var user = string.IsNullOrEmpty(email)
                ? null
                : await repository.FindByEmailAsync(email, ct);

#if DEBUG
            Log.Info($"【AuthService.LoginAsync】执行密码验证，Email={email}，UserExists={user is not null}");
#endif
            // 用户不存在时也跑一次哈希校验，避免通过响应时间差探测邮箱是否注册（时序侧信道）。
            var hashToCheck = user?.PasswordHash ?? DummyHash;
            var passwordOk = request.Password.Length > 0
                             && VerifyPassword(request.Password, hashToCheck);

            if (user is null || !passwordOk)
            {
#if DEBUG
                Log.Info($"【AuthService.LoginAsync】登录失败，Email={email}，原因：用户不存在或密码错误，抛出 Unauthorized");
#endif
                throw new AuthException(AuthErrorKind.Unauthorized, "邮箱或密码错误");
            }

#if DEBUG
            Log.Info($"【AuthService.LoginAsync】密码验证成功，UserId={user.Id}，Email={user.Email}");
#endif

            var accessToken = jwtTokenService.GenerateToken(user);
            var refreshToken = await CreateAndStoreRefreshTokenAsync(user.Id, ct);

#if DEBUG
            Log.Info($"【AuthService.LoginAsync】返回异步登录成功，UserId={user.Id}，Email={user.Email}");
#endif
            return BuildResponse(accessToken, refreshToken, user.Email);
        }
        finally
        {
#if DEBUG
            Log.Info("【AuthService.LoginAsync】清空密码明文（ArrayPool）");
#endif
            CryptographicOperations.ZeroMemory(request.Password);
        }
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
#if DEBUG
        Log.Info($"【AuthService.RefreshTokenAsync】进入异步刷新方法，RefreshToken={refreshToken[..Math.Min(8, refreshToken.Length)]}...");
#endif
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
#if DEBUG
            Log.Info("【AuthService.RefreshTokenAsync】RefreshToken为空，抛出 Unauthorized");
#endif
            throw new AuthException(AuthErrorKind.Unauthorized, "无效或已过期的 Refresh Token");
        }

        var stored = await repository.FindRefreshTokenWithUserAsync(HashToken(refreshToken), ct);

        if (stored is null || stored.Value.Token.ExpiresAt < DateTime.UtcNow)
        {
#if DEBUG
            Log.Info("【AuthService.RefreshTokenAsync】RefreshToken无效或已过期，抛出 Unauthorized");
#endif
            throw new AuthException(AuthErrorKind.Unauthorized, "无效或已过期的 Refresh Token");
        }

        if (stored.Value.Token.IsRevoked)
        {
#if DEBUG
            Log.Info($"【AuthService.RefreshTokenAsync】RefreshToken已被吊销，UserId={stored.Value.Token.UserId}，吊销该用户全部RefreshToken以止损");
#endif
            // 已吊销的令牌再次被使用：典型的令牌被盗/重放信号，吊销该用户全部刷新令牌以止损。
            await repository.RevokeAllRefreshTokensAsync(stored.Value.Token.UserId, ct);
            throw new AuthException(AuthErrorKind.Unauthorized, "无效或已过期的 Refresh Token");
        }

        var user = stored.Value.User;

#if DEBUG
        Log.Info($"【AuthService.RefreshTokenAsync】执行令牌旋转，UserId={user.Id}，Email={user.Email}");
#endif
        // 旋转：旧令牌一次性作废，发新令牌。
        await repository.RevokeRefreshTokenAsync(stored.Value.Token.Id, ct);

        var newAccessToken = jwtTokenService.GenerateToken(user);
        var newRefreshToken = await CreateAndStoreRefreshTokenAsync(user.Id, ct);

#if DEBUG
        Log.Info($"【AuthService.RefreshTokenAsync】返回异步刷新成功，UserId={user.Id}，Email={user.Email}");
#endif
        return BuildResponse(newAccessToken, newRefreshToken, user.Email);
    }

    public async Task LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default)
    {
#if DEBUG
        Log.Info($"【AuthService.LogoutAsync】进入异步登出方法，UserId={userId}，RefreshToken={refreshToken[..Math.Min(8, refreshToken.Length)]}...");
#endif
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
#if DEBUG
            Log.Info("【AuthService.LogoutAsync】RefreshToken为空，幂等返回");
#endif
            return; // 幂等：没有令牌可吊销也算登出成功。
        }

        var stored = await repository.FindRefreshTokenWithUserAsync(HashToken(refreshToken), ct);
        if (stored is null)
        {
#if DEBUG
            Log.Info("【AuthService.LogoutAsync】RefreshToken不存在，幂等返回");
#endif
            return;
        }

        // 只能吊销自己的令牌；提交他人令牌不报错也不生效，避免成为探测他人令牌有效性的预言机。
        if (stored.Value.Token.UserId != userId)
        {
#if DEBUG
            Log.Info($"【AuthService.LogoutAsync】RefreshToken不属于当前用户，忽略");
#endif
            return;
        }

        if (!stored.Value.Token.IsRevoked)
        {
#if DEBUG
            Log.Info($"【AuthService.LogoutAsync】吊销RefreshToken，UserId={userId}");
#endif
            await repository.RevokeRefreshTokenAsync(stored.Value.Token.Id, ct);
        }
        else
        {
#if DEBUG
            Log.Info($"【AuthService.LogoutAsync】RefreshToken已被吊销，UserId={userId}");
#endif
        }
    }

    public Task LogoutAllAsync(Guid userId, CancellationToken ct = default)
    {
#if DEBUG
        Log.Info($"【AuthService.LogoutAllAsync】进入异步登出所有用户方法，UserId={userId}");
#endif
        return repository.RevokeAllRefreshTokensAsync(userId, ct);
    }

    private async Task<string> CreateAndStoreRefreshTokenAsync(Guid userId, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【AuthService.CreateAndStoreRefreshTokenAsync】创建RefreshToken，UserId={userId}");
#endif
        var refreshToken = jwtTokenService.GenerateRefreshToken();

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = HashToken(refreshToken),
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenExpirationDays),
            IsRevoked = false,
            CreatedAt = DateTime.UtcNow
        };

        await repository.AddRefreshTokenAsync(entity, ct);

#if DEBUG
        Log.Info($"【AuthService.CreateAndStoreRefreshTokenAsync】RefreshToken已存储，TokenId={entity.Id}，UserId={userId}");
#endif
        return refreshToken;
    }

    /// <summary>
    /// 用 <see cref="Lukas.Security.BCrypt"/> 对 UTF-8 密码字节求哈希。
    /// 哈希串本身是 ASCII（<c>$2b$...</c>），可安全地以 string 形式落库。
    /// 同步、定长输出，Span 仅活在本方法内、绝不跨 await。
    /// </summary>
    private static byte[] HashPassword(ReadOnlySpan<byte> password)
        => BCrypt.HashPassword(password, BCryptWorkFactor);

    /// <summary>校验 UTF-8 密码字节是否匹配已存哈希。哈希以 ASCII 字节传入底层实现。</summary>
    private static bool VerifyPassword(ReadOnlySpan<byte> password, byte[] storedHash)
        => BCrypt.Verify(password, storedHash);

    /// <summary>刷新令牌入库前先做 SHA-256：数据库泄露时库内数据无法直接当令牌用。</summary>
    private static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NormalizeEmail(string? email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static void ValidateEmail(string email)
    {
        if (email.Length is 0 or > MaxEmailLength)
        {
#if DEBUG
            Log.Info($"【AuthService.ValidateEmail】邮箱长度 {email.Length} 不在有效范围，抛出 BadRequest");
#endif
            throw new AuthException(AuthErrorKind.BadRequest, "邮箱格式不正确");
        }

        var at = email.IndexOf('@');
        var lastAt = email.LastIndexOf('@');
        var dotAfterAt = at >= 0 && email.IndexOf('.', at) > at + 1;

        if (at <= 0 || at != lastAt || at == email.Length - 1 || !dotAfterAt ||
            email.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
#if DEBUG
            Log.Info($"【AuthService.ValidateEmail】邮箱格式无效：{email}，抛出 BadRequest");
#endif
            throw new AuthException(AuthErrorKind.BadRequest, "邮箱格式不正确");
        }
    }

    private static void ValidatePassword(ReadOnlySpan<byte> password)
    {
        switch (password.Length)
        {
            case < MinPasswordBytes:
#if DEBUG
                Log.Info($"【AuthService.ValidatePassword】密码长度 {password.Length} 少于最小值 {MinPasswordBytes}，抛出 BadRequest");
#endif
                throw new AuthException(AuthErrorKind.BadRequest, $"密码长度不能少于 {MinPasswordBytes} 个字符");
            case > MaxPasswordBytes:
#if DEBUG
                Log.Info($"【AuthService.ValidatePassword】密码长度 {password.Length} 超过最大值 {MaxPasswordBytes}，抛出 BadRequest");
#endif
                throw new AuthException(AuthErrorKind.BadRequest, $"密码长度不能超过 {MaxPasswordBytes} 个字符");
        }
    }

    // 进程启动时由随机口令生成的合法 BCrypt 哈希，仅用于"用户不存在"分支的等时校验，永远不会匹配成功。
    private static readonly byte[] DummyHash =
        HashPassword(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));

    private AuthResponse BuildResponse(string accessToken, string refreshToken, string email) =>
        new(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpirationMinutes),
            Email: email
        );
}
