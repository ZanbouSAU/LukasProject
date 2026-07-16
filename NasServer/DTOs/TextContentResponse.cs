// NasServer/DTOs/TextContentResponse.cs

namespace NasServer.DTOs;

/// <summary>文本文件在线阅读的响应。</summary>
public record TextContentResponse(
    string Path,
    string Content,
    long Size
);
