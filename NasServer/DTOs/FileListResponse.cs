// NasServer/DTOs/FileListResponse.cs

using System.Collections.Generic;

namespace NasServer.DTOs;

/// <summary>
/// 文件列表响应数据传输对象
/// </summary>
public record FileListResponse(string Path, List<FileEntry> Entries);
