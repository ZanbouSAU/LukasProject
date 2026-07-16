// NasServer/Configuration/StorageSettings.cs

using System;

namespace NasServer.Configuration;

/// <summary>
/// 云存储相关配置。所有项均可经 appsettings 的 "Storage" 节或环境变量
/// （如 <c>Storage__RootPath</c>）覆盖。
/// </summary>
public class StorageSettings
{
    /// <summary>用户数据根目录。</summary>
    public string RootPath { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\Program Files\NasServer\users"
        : "/var/opt/cloudstorage/users";

    /// <summary>单次上传（含 zip 目录上传解压后的总量）允许的最大字节数，默认 4 GiB。</summary>
    public long MaxUploadBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>zip 目录上传允许的最大条目数，防止 zip 炸弹式的海量小文件。</summary>
    public int MaxZipEntries { get; set; } = 10_000;

    /// <summary>相对路径整体允许的最大字符数。</summary>
    public int MaxRelativePathChars { get; set; } = 2048;

    /// <summary>文本在线阅读/编辑允许的最大文件字节数，默认 5 MiB。超过的文件不在线打开（应下载）。</summary>
    public long MaxTextEditBytes { get; set; } = 5L * 1024 * 1024;
}
