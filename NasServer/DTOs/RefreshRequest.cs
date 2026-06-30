// NasServer/DTOs/RefreshRequest.cs

namespace NasServer.DTOs;

/// <summary>
/// 刷新令牌请求数据传输对象
/// </summary>
public record RefreshRequest(string RefreshToken);
