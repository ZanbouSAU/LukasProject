// NasServer/Endpoints/AuthEndpoints.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using NasServer.DTOs;
using NasServer.Serialization;
using NasServer.Services;
using NasServer.Services.Interface;
using Lukas.Std;

namespace NasServer.Endpoints;

public static class AuthEndpoints
{
    /// <summary>限流策略名：注册/登录/刷新这类可被撞库爆破的端点共用。</summary>
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/register", async (
            RegisterRequest request,
            IAuthService authService,
            CancellationToken ct) =>
        {
#if DEBUG
            Log.Info($"【AuthEndpoints.Register】进入注册端点，Email={request.Email}");
#endif
            try
            {
                var response = await authService.RegisterAsync(request, ct);
#if DEBUG
                Log.Info($"【AuthEndpoints.Register】注册成功，Email={request.Email}");
#endif
                return Results.Ok(response);
            }
            catch (AuthException ex)
            {
#if DEBUG
                Log.Error($"【AuthEndpoints.Register】注册失败，Email={request.Email}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        group.MapPost("/login", async (
            LoginRequest request,
            IAuthService authService,
            CancellationToken ct) =>
        {
#if DEBUG
            Log.Info($"【AuthEndpoints.Login】进入登录端点，Email={request.Email}");
#endif
            try
            {
                var response = await authService.LoginAsync(request, ct);
#if DEBUG
                Log.Info($"【AuthEndpoints.Login】登录成功，Email={request.Email}");
#endif
                return Results.Ok(response);
            }
            catch (AuthException ex)
            {
#if DEBUG
                Log.Error($"【AuthEndpoints.Login】登录失败，Email={request.Email}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        group.MapPost("/refresh", async (
            RefreshRequest request,
            IAuthService authService,
            CancellationToken ct) =>
        {
#if DEBUG
            var tokenPreview = request.RefreshToken.Length > 0
                ? request.RefreshToken[..Math.Min(8, request.RefreshToken.Length)]
                : "(null)";

            Log.Info($"【AuthEndpoints.Refresh】进入刷新端点，RefreshToken={tokenPreview}...");
#endif
            try
            {
                var response = await authService.RefreshTokenAsync(request.RefreshToken, ct);
#if DEBUG
                Log.Info("【AuthEndpoints.Refresh】刷新成功");
#endif
                return Results.Ok(response);
            }
            catch (AuthException ex)
            {
#if DEBUG
                Log.Error($"【AuthEndpoints.Refresh】刷新失败，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // 登出：吊销提交的刷新令牌。需要携带有效的 Access Token，只对调用者本人的令牌生效。
        group.MapPost("/logout", async Task<IResult> (
            LogoutRequest request,
            HttpContext httpContext,
            IAuthService authService,
            CancellationToken ct) =>
        {
#if DEBUG
            Log.Info("【AuthEndpoints.Logout】进入登出端点");
#endif
            if (!CurrentUser.TryGetUserId(httpContext, out var userId))
            {
#if DEBUG
                Log.Error("【AuthEndpoints.Logout】未认证，返回 401");
#endif
                return Unauthorized();
            }

#if DEBUG
            var tokenPreview = request.RefreshToken.Length > 0
                ? request.RefreshToken[..Math.Min(8, request.RefreshToken.Length)]
                : "(null)";

            Log.Info($"【AuthEndpoints.Logout】UserId={userId}，RefreshToken={tokenPreview}...");
#endif

            await authService.LogoutAsync(userId, request.RefreshToken, ct);

#if DEBUG
            Log.Info($"【AuthEndpoints.Logout】登出成功，UserId={userId}");
#endif

            return Ok("已登出");
        }).RequireAuthorization();

        // 登出所有设备：吊销该用户全部刷新令牌（如怀疑令牌泄露时使用）。
        group.MapPost("/logout-all", async Task<IResult> (
            HttpContext httpContext,
            IAuthService authService,
            CancellationToken ct) =>
        {
#if DEBUG
            Log.Info($"【AuthEndpoints.LogoutAll】进入登出所有设备端点");
#endif
            if (!CurrentUser.TryGetUserId(httpContext, out var userId))
            {
#if DEBUG
                Log.Error("【AuthEndpoints.LogoutAll】未认证，返回 401");
#endif
                return Unauthorized();
            }

#if DEBUG
            Log.Info($"【AuthEndpoints.LogoutAll】UserId={userId}，执行登出所有设备");
#endif

            await authService.LogoutAllAsync(userId, ct);

#if DEBUG
            Log.Info($"【AuthEndpoints.LogoutAll】登出所有设备成功，UserId={userId}");
#endif

            return Ok("已登出所有设备");
        }).RequireAuthorization();
    }

    private static JsonHttpResult<MessageResponse> Ok(string message) =>
        TypedResults.Json(new MessageResponse(message), AppJsonSerializerContext.Default.MessageResponse);

    private static JsonHttpResult<ErrorResponse> Unauthorized() =>
        TypedResults.Json(
            new ErrorResponse("未认证"),
            AppJsonSerializerContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status401Unauthorized);

    private static JsonHttpResult<ErrorResponse> ToError(AuthException ex)
    {
        var payload = new ErrorResponse(ex.Message);

        var statusCode = ex.Kind switch
        {
            AuthErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            AuthErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return TypedResults.Json(
            payload,
            AppJsonSerializerContext.Default.ErrorResponse,
            statusCode: statusCode);
    }
}
