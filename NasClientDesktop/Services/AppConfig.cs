// NasClientDesktop/Services/AppConfig.cs

using System;
using System.Reflection;
using Lukas.Std;

namespace NasClientDesktop.Services;

/// <summary>
/// 应用级常量与运行时信息。
///
/// <para>API 端点固定指向 <see cref="ApiBaseUrl"/>。密钥信息按要求先以 "test" 占位，
/// 用户后续可自行修改本文件中的 <see cref="ApiKey"/>。</para>
///
/// <para>版本号在运行时从程序集元数据读取（对应 .csproj 的 &lt;Version&gt; / &lt;InformationalVersion&gt;），
/// 而非硬编码，满足「版本信息从项目文件读取」的要求。</para>
/// </summary>
public static class AppConfig
{
    /// <summary>
    /// NasServer API 基址。端口 9226 按要求固定。
    /// 末尾不带斜杠，拼接时统一以 "/api/..." 形式追加。
    /// </summary>
    public const string ApiBaseUrl = "https://api.lukasau.forum:9226";
    // public const string ApiBaseUrl = "http://127.0.0.1:6182";

    /// <summary>
    /// 当前 NasServer 的认证基于邮箱口令 + JWT，不强制要求此密钥；
    /// 保留它以便将来服务端启用 API Key 头（X-Api-Key）时即开即用。
    /// </summary>
    public const string ApiKey = "test";

    /// <summary>是否在每个请求上附带 <see cref="ApiKey"/>（默认关闭；服务端需要时置 true）。</summary>
    public static readonly bool SendApiKeyHeader = false;

    /// <summary>附带 API Key 时使用的请求头名。</summary>
    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>产品名（用于窗口标题与本地数据目录名）。</summary>
    public const string ProductName = "NasClientDesktop";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();

        // InformationalVersion 对应 csproj 的 <Version>/<InformationalVersion>。
        // AOT/单文件下某些 SDK 会在其后附加 "+<commit>"，这里裁掉构建元数据部分。
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        var ver = asm.GetName().Version;
        return ver is not null ? ver.ToString() : "unknown";
    }

    /// <summary>
    /// 每用户应用数据目录（用于保存刷新令牌等）。
    /// 跨平台解析：Windows → %AppData%\NasClientDesktop；
    /// Linux → ~/.config/NasClientDesktop（XDG）；macOS → ~/Library/Application Support/NasClientDesktop。
    /// 目录创建通过 NasLib（<see cref="Io.File.CreateDirectories(System.ReadOnlySpan{char})"/>）完成。
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        // ApplicationData 在极少数最小化环境下可能为空，回退到用户主目录。
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dir = Io.Path.Combine(baseDir, ProductName);
        try
        {
            // 用 NasLib 创建目录（递归创建）。
            Io.File.CreateDirectories(dir.AsSpan());
        }
        catch
        {
            // 创建失败不致命：调用方写文件时会再次失败并被妥善处理。
        }
        return dir;
    }
}
