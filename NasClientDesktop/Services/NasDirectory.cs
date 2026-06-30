// NasClientDesktop/Services/NasDirectory.cs
// 本地目录「列举」桥接。
//
// 说明：NasLib（Lukas.Io.Directory）公开的目录能力为 Exists / Delete（含递归），
// 其底层虽有 opendir/readdir、FindFirstFile 等枚举原语，但均为 internal，未公开「列目录项」API。
// 文件夹上传需要遍历本地目录并保留相对结构，这一步用 BCL 的 System.IO 目录枚举完成
// （完全 AOT 安全、跨平台），其余文件/目录操作（创建、删除、移动、存在性、读写、路径、控制台）
// 一律走 NasLib。这样既满足「IO 采用 NasLib」的主旨，又不绕过其未提供的能力。

using System.Collections.Generic;
using System.IO;

namespace NasClientDesktop.Services;

public static class NasDirectory
{
    /// <summary>列出目录下的直接子项，返回 (名称, 是否目录)。不抛异常（失败返回空）。</summary>
    public static IEnumerable<(string name, bool isDir)> Enumerate(string absDir)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(absDir);
        }
        catch
        {
            yield break;
        }

        foreach (var full in entries)
        {
            var name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(name)) continue;
            bool isDir;
            try { isDir = Directory.Exists(full); }
            catch { isDir = false; }
            yield return (name, isDir);
        }
    }
}
