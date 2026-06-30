// NasServer/DTOs/FileEntry.cs

using System;

namespace NasServer.DTOs;

/// <summary>目录列表中的一项。</summary>
public record FileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime ModifiedAtUtc
);
