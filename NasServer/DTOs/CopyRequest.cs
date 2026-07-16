// NasServer/DTOs/CopyRequest.cs

namespace NasServer.DTOs;

/// <summary>
/// 复制请求。<paramref name="Source"/> 与 <paramref name="Dest"/> 均为用户根目录内的相对路径（完整目标路径）。
/// 支持文件与目录（目录递归复制）。<paramref name="Overwrite"/> 为 true 时允许覆盖已存在的目标文件。
/// </summary>
public record CopyRequest(string Source, string Dest, bool Overwrite = false);
