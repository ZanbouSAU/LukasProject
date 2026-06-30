// NasServer/Program.cs

using System;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NasServer.Configuration;
using NasServer.Data;
using NasServer.Endpoints;
using NasServer.Serialization;
using NasServer.Services;
using NasServer.Services.Interface;
using NasServer.Services.Storage;
using Npgsql;
using Lukas.AsyncEngine;
using Lukas.Std;

var builder = WebApplication.CreateSlimBuilder(args);

// ---------------------------------------------------------------------------
// JSON：使用源生成上下文，保证 Native AOT 下可序列化（反射序列化在 AOT 下不可用）。
// ---------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// ---------------------------------------------------------------------------
// Kestrel：不暴露 Server 头，减少指纹信息。
// ---------------------------------------------------------------------------
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// ---------------------------------------------------------------------------
// 配置绑定。环境变量（Jwt__Key 等）优先于 appsettings，便于生产环境不落盘秘密。
// ---------------------------------------------------------------------------
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));

builder.Services.PostConfigure<JwtSettings>(jwtSettings =>
{
    var envKey = Environment.GetEnvironmentVariable("Jwt__Key");
    if (!string.IsNullOrEmpty(envKey))
        jwtSettings.Key = envKey;

    var envIssuer = Environment.GetEnvironmentVariable("Jwt__Issuer");
    if (!string.IsNullOrEmpty(envIssuer))
        jwtSettings.Issuer = envIssuer;

    var envAudience = Environment.GetEnvironmentVariable("Jwt__Audience");
    if (!string.IsNullOrEmpty(envAudience))
        jwtSettings.Audience = envAudience;
});

builder.Services.Configure<StorageSettings>(builder.Configuration.GetSection("Storage"));

// ---------------------------------------------------------------------------
// 数据库：Npgsql Slim 数据源（AOT 友好，去掉反射重的可选组件）。
// ---------------------------------------------------------------------------
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") 
                       ?? builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? throw new InvalidOperationException("No database connection string configured.");

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var dataSourceBuilder = new NpgsqlSlimDataSourceBuilder(connectionString);
    return dataSourceBuilder.Build();
});

builder.Services.AddScoped<IUserRepository, NpgsqlUserRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

// ---------------------------------------------------------------------------
// 文件存储相关服务。
// IAsyncIoEngine 为单例（Linux 上是 io_uring 引擎），容器在应用退出时负责 Dispose。
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IAsyncIoEngine>(_ => AsyncEngineFactory.Create());
builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<PathLockPool>();
builder.Services.AddSingleton<IFileService, FileService>();

// ---------------------------------------------------------------------------
// 限流：对 /api/auth 下的注册/登录/刷新做按客户端 IP 的固定窗口限流，
// 缓解口令爆破与令牌枚举。注意：若部署在反向代理之后，需配置 ForwardedHeaders
// 让 RemoteIpAddress 还原为真实客户端 IP，否则所有请求会共享同一个配额。
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy(AuthEndpoints.RateLimitPolicy, httpContext =>
    {
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

// ---------------------------------------------------------------------------
// CORS：仅开发环境放开。
// ---------------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// ---------------------------------------------------------------------------
// JWT 认证。
// ---------------------------------------------------------------------------
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = Environment.GetEnvironmentVariable("Jwt__Key") ?? jwtSection["Key"]
    ?? throw new InvalidOperationException("JWT signing key not configured.");
var issuer = Environment.GetEnvironmentVariable("Jwt__Issuer") ?? jwtSection["Issuer"] ?? "NasServer";
var audience = Environment.GetEnvironmentVariable("Jwt__Audience") ?? jwtSection["Audience"] ?? "Client";

// 启动期安全检查：签名密钥至少 32 字节；生产环境拒绝使用仓库中自带的开发密钥。
const string devOnlyJwtKey = "c6fI5W+biTatixoLny7+PZi3ECEiqJis2Ro4BPQ2i2U=";
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("JWT signing key must be at least 32 bytes (256 bits).");
if (!builder.Environment.IsDevelopment() && signingKey == devOnlyJwtKey)
    throw new InvalidOperationException(
        "Refusing to start: the development JWT key from appsettings.json is being used outside Development. " +
        "Set a unique key via the Jwt__Key environment variable.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 保持令牌中的原始 claim 名（"sub" 等），不要映射成微软的长 URI；CurrentUser 兼容两种取法。
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// 启动时尽力创建存储根目录；失败（如无权限）只警告，首个请求会再次尝试并把错误暴露给运维。
try
{
    var storagePaths = app.Services.GetRequiredService<StoragePaths>();
    Io.File.CreateDirectories(storagePaths.Root);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "Could not create storage root at startup; ensure the directory exists and is writable by the service account.");
}

if (app.Environment.IsDevelopment())
    app.UseCors("AllowAll");

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapFileEndpoints();

app.Run();
