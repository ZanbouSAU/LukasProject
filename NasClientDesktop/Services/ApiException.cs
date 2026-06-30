// NasClientDesktop/Services/ApiException.cs

using System;

namespace NasClientDesktop.Services;

/// <summary>
/// 后端返回的错误（带 HTTP 状态码与服务端 message）。对应前端的 ApiError。
/// status==0 表示网络层错误或非 HTTP 失败（如连接中断、取消）。
/// </summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>是否为同名冲突（409），用于上传后的「覆盖重传」判定。</summary>
    public bool IsConflict => Status == 409;
}
