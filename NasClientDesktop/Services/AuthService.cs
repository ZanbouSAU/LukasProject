// NasClientDesktop/Services/AuthService.cs
// 认证接口。

using System.Net.Http;
using System.Threading.Tasks;
using NasClientDesktop.Models;
using NasClientDesktop.Serialization;

namespace NasClientDesktop.Services;

public sealed class AuthService(HttpService http)
{
    /// <summary>注册。成功后不自动登录（与前端一致，需用户再次登录）。</summary>
    public async Task RegisterAsync(string email, string password, string confirmPassword, string? fullName)
    {
        using var res = await http.SendAsync(HttpMethod.Post, "/api/auth/register", auth: false, contentFactory: Body);
        return;

        HttpContent Body() => HttpService.JsonContent(new RegisterRequest(email, password, confirmPassword, fullName), AppJsonContext.Default.RegisterRequest);
        // 200 即成功；失败已在 SendAsync 内抛 ApiException。
    }

    /// <summary>登录。成功后保存令牌。</summary>
    public async Task<AuthResponse> LoginAsync(string email, string password)
    {
        var auth = await http.SendJsonAsync(
            HttpMethod.Post, "/api/auth/login",
            AppJsonContext.Default.AuthResponse, auth: false, contentFactory: Body);
        http.Tokens.Save(auth.AccessToken, auth.RefreshToken, auth.Email);
        return auth;
        
        HttpContent Body() => HttpService.JsonContent(new LoginRequest(email, password), AppJsonContext.Default.LoginRequest);
    }

    /// <summary>登出当前设备：尽力吊销本地刷新令牌；无论成败本地会话一律清空。</summary>
    public async Task LogoutAsync()
    {
        var refreshToken = http.Tokens.Refresh;
        try
        {
            if (!string.IsNullOrEmpty(refreshToken))
            {
                HttpContent Body() => HttpService.JsonContent(new LogoutRequest(refreshToken), AppJsonContext.Default.LogoutRequest);

                using var res = await http.SendAsync(HttpMethod.Post, "/api/auth/logout", auth: true, contentFactory: Body);
            }
        }
        catch
        {
            // 网络故障或令牌已失效都不应阻止本地登出。
        }
        finally
        {
            http.Tokens.Clear();
        }
    }

    /// <summary>登出所有设备：吊销该账号全部刷新令牌。失败时保留会话并抛出，由界面提示。</summary>
    public async Task LogoutAllAsync()
    {
        using var res = await http.SendAsync(HttpMethod.Post, "/api/auth/logout-all", auth: true);
        http.Tokens.Clear();
    }
}
