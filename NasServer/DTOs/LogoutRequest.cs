// NasServer/DTOs/LogoutRequest.cs

namespace NasServer.DTOs;

/// <summary>
/// 登出请求数据传输对象
/// </summary>
public record LogoutRequest(string RefreshToken);
