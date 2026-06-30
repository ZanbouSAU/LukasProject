// NasClientDesktop/Services/FileService.cs
// 文件接口。路径一律是用户目录内的相对路径（'/' 分隔，空串=根目录）。
//
// 与浏览器版的差异：
//  - 上传请求体直接来自磁盘文件流（NasLib 打开），低内存、可回报进度；
//  - 下载/预览：服务端走「一次性票据 + 直链」。桌面端用票据直链发起 GET，
//    把响应体流式写到目标文件（下载）或临时文件（预览后交给系统默认程序打开）。
//  - 所有本地文件落盘/读取一律经 NasLib（Lukas.Io），不使用 System.IO.File。

using System;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using NasClientDesktop.Models;
using NasClientDesktop.Serialization;
using Lukas.Std;

namespace NasClientDesktop.Services;

public sealed class FileService(HttpService http)
{
    private static string Enc(string s) => Uri.EscapeDataString(s);

    // ---------------------------------------------------------------- 列目录 / 目录操作

    public async Task<FileListResponse> ListDirAsync(string path, CancellationToken ct = default)
    {
        var res = await http.SendJsonAsync(
            HttpMethod.Get, $"/api/files/list?path={Enc(path)}",
            AppJsonContext.Default.FileListResponse, auth: true, contentFactory: null, ct: ct);
        // 形状校验：Entries 为 null 说明后端版本不匹配（旧版 PascalCase 等）。
        // 源生成反序列化下 Entries 不会为 null（除非服务端真的没返回该字段），保险起见兜底。
        return res;
    }

    public Task<MessageResponse> MkDirAsync(string path)
    {
        return http.SendJsonAsync(HttpMethod.Post, "/api/files/mkdir",
            AppJsonContext.Default.MessageResponse, auth: true, contentFactory: Body);

        HttpContent Body() => HttpService.JsonContent(new MkDirRequest(path), AppJsonContext.Default.MkDirRequest);
    }

    public Task<MessageResponse> DeleteEntryAsync(string path, bool recursive)
    {
        var qs = $"path={Enc(path)}&recursive={(recursive ? "true" : "false")}";
        return http.SendJsonAsync(HttpMethod.Delete, $"/api/files/delete?{qs}",
            AppJsonContext.Default.MessageResponse, auth: true);
    }

    /// <summary>在指定相对路径新建一个空文件（父目录自动创建）。</summary>
    public Task<MessageResponse> NewFileAsync(string path)
    {
        return http.SendJsonAsync(HttpMethod.Post, "/api/files/new-file",
            AppJsonContext.Default.MessageResponse, auth: true, contentFactory: Body);

        HttpContent Body() => HttpService.JsonContent(new NewFileRequest(path), AppJsonContext.Default.NewFileRequest);
    }

    /// <summary>移动 / 重命名：dest 为完整目标相对路径。overwrite=true 允许覆盖已存在目标文件。</summary>
    public Task<MessageResponse> MoveAsync(string source, string dest, bool overwrite)
    {
        return http.SendJsonAsync(HttpMethod.Post, "/api/files/move",
            AppJsonContext.Default.MessageResponse, auth: true, contentFactory: Body);

        HttpContent Body() => HttpService.JsonContent(new MoveRequest(source, dest, overwrite), AppJsonContext.Default.MoveRequest);
    }

    /// <summary>复制：dest 为完整目标相对路径（文件或目录，目录递归）。overwrite=true 允许覆盖已存在目标文件。</summary>
    public Task<MessageResponse> CopyAsync(string source, string dest, bool overwrite)
    {
        return http.SendJsonAsync(HttpMethod.Post, "/api/files/copy",
            AppJsonContext.Default.MessageResponse, auth: true, contentFactory: Body);

        HttpContent Body() => HttpService.JsonContent(new CopyRequest(source, dest, overwrite), AppJsonContext.Default.CopyRequest);
    }

    // ---------------------------------------------------------------- 上传（单文件，带进度）

    /// <summary>
    /// 上传本地文件到服务端 <paramref name="destRelPath"/>（用户目录内相对路径）。
    /// 带 401 自动刷新重试一次。进度通过 <paramref name="onProgress"/> 回报。
    /// </summary>
    public async Task UploadFileAsync(string localFilePath,
        string destRelPath,
        bool overwrite,
        Action<long, long> onProgress,
        CancellationToken ct = default)
    {
        var qs = $"path={Enc(destRelPath)}&overwrite={(overwrite ? "true" : "false")}";

        try
        {
            await Attempt().ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            if (await http.TryRefreshAsync().ConfigureAwait(false))
            {
                await Attempt().ConfigureAwait(false);
                return;
            }

            throw;
        }
        return;

        async Task Attempt()
        {
            // 通过 NasLib 打开本地文件读流，包装为 HttpContent 流式上传。
            var length = Io.File.GetFileLength(localFilePath.AsSpan());
            var source = new NasFileReadStream(localFilePath);
            var content = new ProgressStreamContent(source, length, "application/octet-stream", onProgress, ct);

            using var req = http.NewRequest(HttpMethod.Put, $"/api/files/upload?{qs}", auth: true);
            req.Content = content; // req 释放时会一并释放 content 与底层文件句柄
            using var res = await http.Raw.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            await HandleUploadResponse(res, AppJsonContext.Default.UploadResponse).ConfigureAwait(false);
        }
    }

    /// <summary>上传本地 zip 并由服务端解压到 <paramref name="destDirRelPath"/>。</summary>
    public async Task UploadZipAsync(string localZipPath,
        string destDirRelPath,
        bool overwrite,
        Action<long, long> onProgress,
        CancellationToken ct = default)
    {
        var qs = $"path={Enc(destDirRelPath)}&overwrite={(overwrite ? "true" : "false")}";

        try
        {
            await Attempt().ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            if (await http.TryRefreshAsync().ConfigureAwait(false))
            {
                await Attempt().ConfigureAwait(false);
                return;
            }

            throw;
        }
        return;

        async Task Attempt()
        {
            var length = Io.File.GetFileLength(localZipPath.AsSpan());
            var source = new NasFileReadStream(localZipPath);
            var content = new ProgressStreamContent(source, length, "application/octet-stream", onProgress, ct);

            using var req = http.NewRequest(HttpMethod.Post, $"/api/files/upload-zip?{qs}", auth: true);
            req.Content = content;
            using var res = await http.Raw.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            await HandleUploadResponse(res, AppJsonContext.Default.UploadZipResponse).ConfigureAwait(false);
        }
    }

    private static async Task HandleUploadResponse<T>(HttpResponseMessage res, JsonTypeInfo<T> typeInfo)
    {
        if (!res.IsSuccessStatusCode)
        {
            var status = (int)res.StatusCode;
            var message = $"请求失败（HTTP {status}）";
            try
            {
                await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var body = await System.Text.Json.JsonSerializer.DeserializeAsync(
                    stream, AppJsonContext.Default.ErrorResponse).ConfigureAwait(false);
                if (body is not null && !string.IsNullOrEmpty(body.Message)) message = body.Message;
            }
            catch { /* 非 JSON 体 */ }
            throw new ApiException(status, message);
        }
        await using var ok = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var value = await System.Text.Json.JsonSerializer.DeserializeAsync(ok, typeInfo).ConfigureAwait(false);
        if (value is null) throw new ApiException(0, "响应解析失败");
    }

    // ---------------------------------------------------------------- 下载票据

    /// <summary>换取下载/内联预览票据。inline=true 用于图片/音视频/文本在线查看。</summary>
    private async Task<string> GetDownloadTicketAsync(string path, bool inline)
    {
        var qs = $"path={Enc(path)}&inline={(inline ? "true" : "false")}";
        var res = await http.SendJsonAsync(HttpMethod.Post, $"/api/files/download-ticket?{qs}",
            AppJsonContext.Default.DownloadTicketResponse, auth: true);
        return res.Ticket;
    }

    /// <summary>由票据构造可直接 GET 的直链（相对 BaseAddress）。</summary>
    private static string DownloadByTicketPath(string ticket) =>
        $"/api/files/download-by-ticket?ticket={Uri.EscapeDataString(ticket)}";

    /// <summary>
    /// 下载文件或目录到本地 <paramref name="localTargetPath"/>（目录由服务端打包为 zip）。
    /// 通过 NasLib 把响应体流式写盘，可回报进度。
    /// </summary>
    public async Task DownloadToFileAsync(
        string remotePath,
        string localTargetPath,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        var ticket = await GetDownloadTicketAsync(remotePath, inline: false).ConfigureAwait(false);
        using var req = http.NewRequest(HttpMethod.Get, DownloadByTicketPath(ticket), auth: false);
        using var res = await http.Raw.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new ApiException((int)res.StatusCode, "下载失败");

        var total = res.Content.Headers.ContentLength ?? -1L;
        await using var http1 = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await NasFileWriter.WriteFromStreamAsync(localTargetPath, http1, total, onProgress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取「内联预览」票据并下载到临时文件，返回临时文件路径（供系统默认程序打开音视频，
    /// 或交给 Avalonia 解码图片）。文件名尽量保留原扩展名。
    /// </summary>
    public async Task<string> DownloadPreviewToTempAsync(
        FileEntry entry,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        var ticket = await GetDownloadTicketAsync(entry.Path, inline: true).ConfigureAwait(false);
        using var req = http.NewRequest(HttpMethod.Get, DownloadByTicketPath(ticket), auth: false);
        using var res = await http.Raw.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new ApiException((int)res.StatusCode, "预览加载失败");

        var total = res.Content.Headers.ContentLength ?? -1L;
        var tempPath = TempFile.ForName(entry.Name);
        await using var http1 = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await NasFileWriter.WriteFromStreamAsync(tempPath, http1, total, onProgress, ct).ConfigureAwait(false);
        return tempPath;
    }

    // ---------------------------------------------------------------- 文本在线读取 / 保存

    public Task<TextContentResponse> ReadTextAsync(string path, CancellationToken ct = default)
        => http.SendJsonAsync(HttpMethod.Get, $"/api/files/text?path={Enc(path)}",
            AppJsonContext.Default.TextContentResponse, auth: true, contentFactory: null, ct: ct);

    public Task<UploadResponse> SaveTextAsync(string path, string content)
    {
        return http.SendJsonAsync(HttpMethod.Post, "/api/files/text",
            AppJsonContext.Default.UploadResponse, auth: true, contentFactory: Body);

        HttpContent Body() => HttpService.JsonContent(new SaveTextRequest(path, content), AppJsonContext.Default.SaveTextRequest);
    }
}
