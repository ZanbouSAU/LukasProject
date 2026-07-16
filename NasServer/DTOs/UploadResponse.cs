// NasServer/DTOs/UploadResponse.cs

namespace NasServer.DTOs;

/// <summary>
/// 文件上传响应数据传输对象
/// </summary>
public record UploadResponse(string Path, long Size);
