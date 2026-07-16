// NasServer/Services/Storage/StoragePaths.cs

using System;
using System.IO;
using Microsoft.Extensions.Options;
using NasServer.Configuration;
using Lukas;
using Lukas.Std;

namespace NasServer.Services.Storage;

/// <summary>
/// 用户存储路径解析器：把客户端传来的「用户目录内相对路径」校验、归一化为服务器上的绝对路径。
///
/// 这里按 '/' 拆段后逐段套用 <see cref="SafeName.TryMakeSafe"/>（拒绝 "."、".."、非法字符、盘符冒号），
/// 再做一次 <see cref="Path.GetFullPath(string)"/> 归一化并强校验结果仍位于该用户根目录之内，双重防线杜绝路径穿越。
/// </summary>
public sealed class StoragePaths
{
    private readonly StorageSettings _settings;

    public StoragePaths(IOptions<StorageSettings> options)
    {
        _settings = options.Value;
        Root = Path.GetFullPath(_settings.RootPath);
    }

    /// <summary>存储根目录（所有用户目录的父目录）的绝对路径。</summary>
    public string Root { get; }

    /// <summary>某用户的根目录绝对路径：<c>&lt;Root&gt;/&lt;user_id&gt;</c>。Guid "D" 格式只含 hex 与 '-'，天然安全。</summary>
    private string GetUserRoot(Guid userId) => Path.Combine(Root, userId.ToString("D"));

    /// <summary>确保某用户根目录存在（幂等）。</summary>
    public void EnsureUserRoot(Guid userId)
    {
        var userRoot = GetUserRoot(userId);
        Io.File.CreateDirectories(userRoot);
    }

    /// <summary>
    /// 校验并解析相对路径。空串/null 解析为用户根目录本身。
    /// 成功时 <paramref name="fullPath"/> 为绝对路径、<paramref name="normalized"/> 为以 '/' 分隔的规范相对路径；
    /// 任何越界、非法段或超长路径都会失败。
    /// </summary>
    public bool TryResolve(Guid userId, string? relativePath, out string fullPath, out string normalized)
    {
        fullPath = string.Empty;
        normalized = string.Empty;

        var userRoot = GetUserRoot(userId);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            fullPath = userRoot;
            return true;
        }

        if (relativePath.Length > _settings.MaxRelativePathChars)
            return false;

        // 统一分隔符并去掉首尾的 '/'，随后逐段校验。
        var candidate = relativePath.Replace('\\', '/').Trim().Trim('/');
        if (candidate.Length == 0)
        {
            fullPath = userRoot;
            return true;
        }

        var segments = candidate.Split('/');
        var cleaned = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!TryCleanSegment(segments[i], out var safe))
                return false;
            cleaned[i] = safe;
        }

        normalized = string.Join('/', cleaned);

        var combined = Path.Combine(userRoot, string.Join(Path.DirectorySeparatorChar, cleaned));
        var resolved = Path.GetFullPath(combined);

        // 双保险：归一化后的绝对路径必须仍然位于用户根目录之内。
        if (!resolved.StartsWith(userRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(resolved, userRoot, StringComparison.Ordinal))
        {
            normalized = string.Empty;
            return false;
        }

        fullPath = resolved;
        return true;
    }

    /// <summary>同 <see cref="TryResolve(Guid, string?, out string, out string)"/>，但额外要求路径非根（用于删除等不允许作用于根目录的操作）。</summary>
    public bool TryResolveNonRoot(Guid userId, string? relativePath, out string fullPath, out string normalized)
    {
        if (!TryResolve(userId, relativePath, out fullPath, out normalized))
            return false;
        if (normalized.Length == 0)
        {
            fullPath = string.Empty;
            return false;
        }
        return true;
    }

    // 单段清洗：先走 Lukas.SafeName（剥目录、拒绝 "."/".."、非法字符、冒号），
    // 再补充控制字符与跨平台保留字符的黑名单，并拒绝 Windows 不允许的结尾点/空格。
    private static bool TryCleanSegment(string rawSegment, out string safe)
    {
        safe = string.Empty;

        if (!SafeName.TryMakeSafe(rawSegment, out var name))
            return false;

        // SafeName 在 Linux 上的非法字符集较小（'\0' 与 '/'），这里补齐跨平台黑名单与控制字符。
        foreach (var c in name)
        {
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '\u007F')
                return false;
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
            return false;

        if (name.Length > 255)
            return false;

        safe = name;
        return true;
    }
}
