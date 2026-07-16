// NasServer/DTOs/NewFileRequest.cs

namespace NasServer.DTOs;

/// <summary>新建空文件请求；<paramref name="Path"/> 为用户根目录内的相对路径（可多级，父目录自动创建）。</summary>
public record NewFileRequest(string Path);
