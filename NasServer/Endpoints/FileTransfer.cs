// NasServer/Endpoints/FileTransfer.cs

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using NasServer.Services.Storage;
using Lukas.AsyncEngine;
using Lukas.Interop;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace NasServer.Endpoints;

/// <summary>
/// 下载/预览的 HTTP 传输层：把 <see cref="DownloadDescriptor"/>（业务层给的纯元数据）真正写进
/// HTTP 响应。所有与 ASP.NET 绑定的细节——HTTP Range、Content-Disposition、Content-Type、
/// zip 流式打包、同步 I/O 放开——都集中在这里，与 <c>FileService</c> 的业务逻辑彻底分离。
/// </summary>
internal static class FileTransfer
{
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>按描述符把文件（支持 Range）或目录（打包为 zip）流式写入响应。</summary>
    public static Task SendAsync(
        HttpContext ctx, IAsyncIoEngine engine, in DownloadDescriptor descriptor, bool inline, CancellationToken ct)
        => descriptor.Kind == DownloadTargetKind.File
            ? StreamFileAsync(ctx, engine, descriptor.FullPath, descriptor.DownloadName, descriptor.Length, inline, ct)
            : StreamDirectoryAsZipAsync(ctx.Response, engine, descriptor.FullPath, descriptor.DownloadName, ct);

    /// <summary>
    /// 把单个文件流式写入响应。支持 HTTP Range（音视频拖动/断点续传）；
    /// inline=true 时设置真实 MIME 与 Content-Disposition: inline，让浏览器内联渲染。
    /// </summary>
    private static async Task StreamFileAsync(
        HttpContext ctx, IAsyncIoEngine engine, string fullPath, string fileName, long length, bool inline, CancellationToken ct)
    {
        var response = ctx.Response;

        response.ContentType = inline ? ContentTypes.FromPath(fullPath) : ContentTypes.Default;
        SetDispositionName(response, fileName, inline);
        response.Headers.AcceptRanges = "bytes";

        long start = 0;
        var end = length - 1;
        var isRange = false;

        var rangeHeader = ctx.Request.Headers.Range.ToString();
        switch (length)
        {
            case > 0 when rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                          && TryParseSingleRange(rangeHeader, length, out start, out end):
                isRange = true;
                break;
            case > 0 when rangeHeader.Length > 0 && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase):
                // 语法是 Range 但越界/不可满足：按 RFC 7233 返回 416。
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{length}";
                return;
        }

        var count = end - start + 1;

        if (isRange)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }
        response.ContentLength = count;

        if (count <= 0)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var file = await PositionedFile.OpenAsync(engine, fullPath, Flags.Read, ct: ct);
        try
        {
            var offset = start;
            var remaining = count;
            while (remaining > 0)
            {
                var want = (int)Math.Min(CopyBufferSize, remaining);
                var n = await file.ReadFullAsync(buffer.AsMemory(0, want), offset, ct);
                if (n <= 0)
                    break;
                await response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                offset += n;
                remaining -= n;
            }
        }
        finally
        {
            await file.DisposeAsync();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>解析单段字节范围。成功时 start/end 为闭区间且落在 [0, length)。</summary>
    private static bool TryParseSingleRange(string rangeHeader, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;

        var spec = rangeHeader.AsSpan("bytes=".Length).Trim();
        if (spec.IndexOf(',') >= 0)
            return false; // 不支持多段。

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;

        var startSpan = spec[..dash].Trim();
        var endSpan = spec[(dash + 1)..].Trim();

        if (startSpan.Length == 0)
        {
            // 后缀范围 "-N"：最后 N 字节。
            if (!long.TryParse(endSpan, out var suffix) || suffix <= 0)
                return false;
            start = Math.Max(0, length - suffix);
            end = length - 1;
            return true;
        }

        if (!long.TryParse(startSpan, out start) || start < 0 || start >= length)
            return false;

        if (endSpan.Length == 0)
        {
            end = length - 1;
        }
        else
        {
            if (!long.TryParse(endSpan, out end) || end < start)
                return false;
            if (end >= length)
                end = length - 1;
        }
        return true;
    }

    /// <summary>把目录递归打包为 zip 直接写入响应（流式，不落临时包）。符号链接一律跳过。</summary>
    private static async Task StreamDirectoryAsZipAsync(
        HttpResponse response, IAsyncIoEngine engine, string rootFull, string downloadName, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/zip";
        SetDispositionName(response, downloadName + ".zip", inline: false);

        // ZipArchive 在 Dispose（写中央目录、刷新 DeflateStream）时执行同步写，
        // 而 Kestrel 默认禁止对响应体做同步 I/O。仅对本次 zip 下载放开同步 I/O。
        var bodyControl = response.HttpContext.Features.Get<IHttpBodyControlFeature>();
        bodyControl?.AllowSynchronousIO = true;

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var zip = new ZipArchive(response.Body, ZipArchiveMode.Create, leaveOpen: true);

            var pending = new Stack<(DirectoryInfo Dir, string Rel)>();
            pending.Push((new DirectoryInfo(rootFull), string.Empty));

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, rel) = pending.Pop();

                var hasChildren = false;
                foreach (var info in dir.EnumerateFileSystemInfos())
                {
                    if (info.LinkTarget is not null)
                        continue; // 不跟随符号链接，防止打包逃出用户目录的数据。

                    hasChildren = true;
                    var childRel = rel.Length == 0 ? info.Name : $"{rel}/{info.Name}";

                    if ((info.Attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(((DirectoryInfo)info, childRel));
                        continue;
                    }

                    var entry = zip.CreateEntry(childRel, CompressionLevel.Fastest);
                    entry.LastWriteTime = info.LastWriteTimeUtc;
                    await using var entryStream = await entry.OpenAsync(ct);

                    var length = ((FileInfo)info).Length;
                    var file = await PositionedFile.OpenAsync(engine, info.FullName, Flags.Read, ct: ct);
                    try
                    {
                        long offset = 0;
                        while (offset < length)
                        {
                            var want = (int)Math.Min(CopyBufferSize, length - offset);
                            var n = await file.ReadFullAsync(buffer.AsMemory(0, want), offset, ct);
                            if (n <= 0)
                                break;
                            await entryStream.WriteAsync(buffer.AsMemory(0, n), ct);
                            offset += n;
                        }
                    }
                    finally
                    {
                        await file.DisposeAsync();
                    }
                }

                // 保留空目录：写一个以 '/' 结尾的零字节条目。
                if (!hasChildren && rel.Length > 0)
                    zip.CreateEntry(rel + "/");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SetDispositionName(HttpResponse response, string fileName, bool inline)
    {
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName); // 自动按 RFC 5987 处理非 ASCII 文件名。
        response.Headers.ContentDisposition = disposition.ToString();
    }
}
