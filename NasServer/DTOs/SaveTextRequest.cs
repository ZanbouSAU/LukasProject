// NasServer/DTOs/SaveTextRequest.cs

namespace NasServer.DTOs;

/// <summary>保存（覆盖）文本文件内容的请求。</summary>
public record SaveTextRequest(
    string Path,
    string Content
);
