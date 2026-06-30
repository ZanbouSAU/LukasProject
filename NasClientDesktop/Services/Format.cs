// NasClientDesktop/Services/Format.cs
// 展示与校验辅助。

using System;
using System.Collections.Generic;

namespace NasClientDesktop.Services;

public enum PreviewKind { None, Image, Video, Audio, Text }

public static class Format
{
    /// <summary>1234567 → "1.2 MB"。</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = "B";
        foreach (var u in units)
        {
            value /= 1024;
            unit = u;
            if (value < 1024) break;
        }
        return value >= 100 ? $"{value:0} {unit}" : $"{value:0.0} {unit}";
    }

    /// <summary>UTC 时间 → 本地 "2026-06-11 09:30"。</summary>
    public static string Time(DateTime utc)
    {
        if (utc == default) return "—";
        var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime()
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>把路径拆成面包屑段。</summary>
    public static List<string> SplitPath(string path)
    {
        var result = new List<string>();
        foreach (var seg in path.Split('/'))
            if (seg.Length > 0) result.Add(seg);
        return result;
    }

    /// <summary>拼接相对路径，去除多余分隔符。</summary>
    public static string JoinPath(params string[] parts)
    {
        var segs = new List<string>();
        foreach (var p in parts)
            foreach (var s in p.Split('/'))
                if (s.Length > 0) segs.Add(s);
        return string.Join('/', segs);
    }

    /// <summary>拼接本地 OS 路径（用平台分隔符，经 NasLib）。用于下载落地路径。</summary>
    public static string JoinPathLocal(string dir, string name) => Lukas.Std.Io.Path.Combine(dir, name);

    /// <summary>客户端预检：目录/文件名里不允许的输入。返回错误信息或 null。</summary>
    public static string? ValidateNameSegment(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "名称不能为空";
        if (trimmed is "." or "..") return "名称不能是 \".\" 或 \"..\"";
        foreach (var ch in trimmed)
        {
            if (ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return "名称含有不允许的字符（\\ / : * ? \" < > |）";
            if (ch < 0x20) return "名称含有控制字符";
        }
        return trimmed.Length > 255 ? "名称过长（最多 255 字符）" : null;
    }

    // ---------------------------------------------------------------- 文件类型识别

    private static readonly HashSet<string> ExtImage = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "avif" };
    private static readonly HashSet<string> ExtVideo = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "webm", "ogv", "mov", "mkv" };
    private static readonly HashSet<string> ExtAudio = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "wav", "ogg", "oga", "flac", "aac", "m4a", "opus" };
    private static readonly HashSet<string> ExtText = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "json", "xml", "csv", "tsv", "yaml", "yml", "ini", "conf",
        "html", "htm", "css", "js", "jsx", "ts", "tsx", "c", "h", "cpp", "hpp", "cs", "java", "py",
        "rb", "go", "rs", "php", "sh", "bash", "sql", "toml", "env", "gitignore", "dockerfile",
    };

    private static string ExtOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : "";
    }

    /// <summary>Avalonia 内置位图可直接解码的图片格式（其余如 svg/ico/avif 走系统默认程序）。</summary>
    private static readonly HashSet<string> ExtBitmapNative = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "bmp" };

    public static bool IsBitmapNative(string name) => ExtBitmapNative.Contains(ExtOf(name));

    public static PreviewKind PreviewKindOf(string name)
    {
        var ext = ExtOf(name);
        if (ExtImage.Contains(ext)) return PreviewKind.Image;
        if (ExtVideo.Contains(ext)) return PreviewKind.Video;
        if (ExtAudio.Contains(ext)) return PreviewKind.Audio;
        return ExtText.Contains(ext) ? PreviewKind.Text : PreviewKind.None;
    }
}
