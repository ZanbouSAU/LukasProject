// NasClientDesktop/Services/HttpService.cs
// HTTP 基础层：
//  - access token 只放内存；refresh token 由 TokenStore 落盘以便重启续期。
//  - 401 时做「单飞」刷新：并发请求共享同一次 refresh，刷新成功后各自重试一次。
//  - 后端错误统一为 ApiException（带状态码与后端 message）。
//  - 全程使用 System.Text.Json 源生成（AOT 安全），HttpClient 默认 SocketsHttpHandler。

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NasClientDesktop.Models;
using NasClientDesktop.Serialization;

namespace NasClientDesktop.Services;

/// <summary>会话彻底失效（刷新令牌也救不回来）时触发，由上层跳回登录页。</summary>
public sealed class HttpService
{
    /// <summary>会话失效回调（在任意线程触发，订阅方需自行切回 UI 线程）。</summary>
    public event Action? SessionExpired;

    public TokenStore Tokens { get; }

    public HttpService(TokenStore tokens)
    {
        Tokens = tokens;

        // 显式使用 SocketsHttpHandler：跨平台一致、AOT 安全、TLS 走各平台原生栈。
        var handler = new SocketsHttpHandler
        {
            // 自动跟随重定向关闭：下载票据等接口我们要自己处理响应头。
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        Raw = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppConfig.ApiBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(10), // 大文件下载/上传放宽超时
        };
        Raw.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>暴露底层 HttpClient 给需要流式传输的服务（上传/下载），它们复用同一连接池与基址。</summary>
    public HttpClient Raw { get; }

    /// <summary>构造带认证头的请求消息（可选）。</summary>
    internal HttpRequestMessage NewRequest(HttpMethod method, string path, bool auth)
    {
        var req = new HttpRequestMessage(method, path);
        if (auth)
        {
            var token = Tokens.Access;
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (AppConfig.SendApiKeyHeader)
            req.Headers.TryAddWithoutValidation(AppConfig.ApiKeyHeaderName, AppConfig.ApiKey);
        return req;
    }

    // ---------------------------------------------------------------- 刷新（单飞）

    private readonly Lock _refreshGate = new();
    private Task<bool>? _refreshInFlight;

    /// <summary>用 refresh token 换新令牌对。成功 true；失败清空会话、触发 SessionExpired 并返回 false。</summary>
    public Task<bool> TryRefreshAsync()
    {
        // 单飞：已有刷新在进行就复用它。
        lock (_refreshGate)
        {
            if (_refreshInFlight is { IsCompleted: false })
                return _refreshInFlight;
            _refreshInFlight = RefreshCoreAsync();
            return _refreshInFlight;
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        var refreshToken = Tokens.Refresh;
        if (string.IsNullOrEmpty(refreshToken))
        {
            Tokens.Clear();
            SessionExpired?.Invoke();
            return false;
        }

        try
        {
            using var req = NewRequest(HttpMethod.Post, "/api/auth/refresh", auth: false);
            req.Content = JsonContent(new RefreshRequest(refreshToken), AppJsonContext.Default.RefreshRequest);
            using var res = await Raw.SendAsync(req, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Tokens.Clear();
                SessionExpired?.Invoke();
                return false;
            }
            var auth = await ReadJsonAsync(res, AppJsonContext.Default.AuthResponse).ConfigureAwait(false);
            if (auth is null)
            {
                Tokens.Clear();
                SessionExpired?.Invoke();
                return false;
            }
            Tokens.Save(auth.AccessToken, auth.RefreshToken, auth.Email);
            return true;
        }
        catch
        {
            Tokens.Clear();
            SessionExpired?.Invoke();
            return false;
        }
    }

    // ---------------------------------------------------------------- 请求封装

    /// <summary>
    /// 发送请求并确保成功（res.IsSuccessStatusCode）。auth=true 时 401 会先尝试刷新并重试一次。
    /// 返回的 HttpResponseMessage 由调用方负责释放。
    /// <para>请求体通过工厂 <paramref name="contentFactory"/> 提供：每次尝试都新建一份，
    /// 因为 HttpContent 发送一次后即被消费，401 重试时不能复用同一实例。</para>
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        bool auth = true,
        Func<HttpContent>? contentFactory = null,
        CancellationToken ct = default)
    {
        async Task<HttpResponseMessage> DoOnce()
        {
            var req = NewRequest(method, path, auth);
            if (contentFactory is not null) req.Content = contentFactory();
            return await Raw.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }

        var res = await DoOnce().ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && auth)
        {
            res.Dispose();
            if (await TryRefreshAsync().ConfigureAwait(false))
            {
                res = await DoOnce().ConfigureAwait(false);
            }
            else
            {
                throw new ApiException(401, "会话已过期，请重新登录");
            }
        }

        if (!res.IsSuccessStatusCode)
        {
            var ex = await ParseErrorAsync(res).ConfigureAwait(false);
            res.Dispose();
            throw ex;
        }
        return res;
    }

    /// <summary>发送请求并把响应体解析为 JSON。</summary>
    public async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        bool auth = true,
        Func<HttpContent>? contentFactory = null,
        CancellationToken ct = default)
    {
        using var res = await SendAsync(method, path, auth, contentFactory, ct).ConfigureAwait(false);
        var value = await ReadJsonAsync(res, typeInfo).ConfigureAwait(false);
        if (value is null) throw new ApiException(0, "响应解析失败：服务端返回了空内容");
        return value;
    }

    // ---------------------------------------------------------------- JSON 助手

    /// <summary>用源生成元数据序列化请求体。</summary>
    public static HttpContent JsonContent<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage res,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo).ConfigureAwait(false);
    }

    /// <summary>把失败响应体解析为 ApiException（尽力提取后端 message）。</summary>
    private static async Task<ApiException> ParseErrorAsync(HttpResponseMessage res)
    {
        var status = (int)res.StatusCode;
        var message = $"请求失败（HTTP {status}）";
        try
        {
            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var body = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ErrorResponse).ConfigureAwait(false);
            if (body is not null && !string.IsNullOrEmpty(body.Message))
                message = body.Message;
        }
        catch
        {
            // 非 JSON 响应体，沿用默认消息。
        }
        return new ApiException(status, message);
    }
}
