// NasClientDesktop/Services/TokenStore.cs
// 令牌保管：
//  - access token 只放内存（进程内）；
//  - refresh token 与 email 持久化到每用户应用数据目录，重启后可静默续期，替代浏览器 localStorage。
// 文件读写一律走 NasLib（Lukas.Io），不使用 System.IO.File。

using System;
using System.Threading;
using Lukas.Interop;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace NasClientDesktop.Services;

/// <summary>令牌与会话身份的保管处。线程安全由调用方（HttpService）的串行刷新保证，此处仅做简单同步。</summary>
public sealed class TokenStore
{
    private readonly Lock _gate = new();
    private string? _accessToken;

    private readonly string _refreshPath = Io.Path.Combine(AppConfig.DataDirectory, "refresh.token");
    private readonly string _emailPath = Io.Path.Combine(AppConfig.DataDirectory, "email.txt");

    /// <summary>当前内存中的 access token（可能为 null）。</summary>
    public string? Access
    {
        get { lock (_gate) return _accessToken; }
    }

    /// <summary>磁盘上持有的 refresh token（无则 null）。</summary>
    public string? Refresh => ReadIfExists(_refreshPath);

    /// <summary>磁盘上记录的已登录邮箱（无则 null）。</summary>
    public string? Email => ReadIfExists(_emailPath);

    /// <summary>保存一组新令牌：access 入内存，refresh/email 落盘。</summary>
    public void Save(string accessToken, string refreshToken, string email)
    {
        lock (_gate)
        {
            _accessToken = accessToken;
        }
        WriteAtomically(_refreshPath, refreshToken);
        WriteAtomically(_emailPath, email);
    }

    /// <summary>仅更新 access token（刷新成功但不改变身份时也可用 Save，这里保留以备用）。</summary>
    public void UpdateAccess(string accessToken)
    {
        lock (_gate) _accessToken = accessToken;
    }

    /// <summary>清空会话：内存清空，磁盘上的 refresh/email 删除。</summary>
    public void Clear()
    {
        lock (_gate) _accessToken = null;
        DeleteIfExists(_refreshPath);
        DeleteIfExists(_emailPath);
    }

    // ---------------------------------------------------------------- 文件助手（全部经 NasLib）

    private static string? ReadIfExists(string path)
    {
        try
        {
            if (!Io.File.Exists(path.AsSpan())) return null;
            var text = Io.File.ReadAllText(path.AsSpan());
            text = text.Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        try
        {
            // Create 语义：存在则清空后写入。NasLib 的 WriteLineAllBytes 会以 OpenOrCreate 打开并写一行；
            // 这里直接用 File + Create，避免追加换行影响后续 Trim 之外的精确比较。
            using var file = new Io.File();
            file.Open(path.AsSpan(), Flags.Create);
            file.Write(content.AsSpan());
        }
        catch
        {
            // 落盘失败不致命：本次会话仍可用，仅重启后无法静默续期。
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (Io.File.Exists(path.AsSpan()))
                Io.File.DeleteFile(path.AsSpan());
        }
        catch
        {
            // 删除失败忽略。
        }
    }
}
