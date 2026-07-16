// NasServer/Services/Storage/ContentTypes.cs

using System;
using System.Collections.Generic;
using Lukas.Std;

namespace NasServer.Services.Storage;

/// <summary>
/// 按文件扩展名推断 MIME 类型。用于内联预览（图片/音视频/文本）时设置正确的 Content-Type，
/// 浏览器据此决定如何渲染。未知类型回落到 application/octet-stream（浏览器会当作下载）。
/// </summary>
public static class ContentTypes
{
    // 只收录适合在浏览器内联预览的常见类型；其余一律按二进制处理。
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图片
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        // 视频
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".ogv"] = "video/ogg",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        // 音频
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".m4a"] = "audio/mp4",
        [".opus"] = "audio/opus",
        // 文本/代码（统一按 UTF-8 纯文本，便于浏览器内联显示）
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "text/xml; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".pdf"] = "application/pdf",
    };

    public const string Default = "application/octet-stream";

    public static string FromPath(string path)
    {
        var ext = Io.Path.GetExtension(path);
        return ext.Length > 0 && Map.TryGetValue(ext, out var mime) ? mime : Default;
    }

    /// <summary>该扩展名是否被允许内联预览（即在已知预览类型表内）。</summary>
    public static bool IsInlinePreviewable(string path) =>
        Io.Path.GetExtension(path) is { Length: > 0 } ext && Map.ContainsKey(ext);
}
