// NasServer/DTOs/MkDirRequest.cs

namespace NasServer.DTOs;

/// <summary>创建目录请求；<paramref name="Path"/> 为用户根目录内的相对路径，可多级（如 "a/b/c"）。</summary>
public record MkDirRequest(string Path);
