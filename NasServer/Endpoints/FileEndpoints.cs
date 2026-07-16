// NasServer/Endpoints/FileEndpoints.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NasServer.Configuration;
using NasServer.DTOs;
using NasServer.Serialization;
using NasServer.Services;
using NasServer.Services.Interface;
using Lukas.AsyncEngine;
using Lukas.Std;

namespace NasServer.Endpoints;

/// <summary>
/// 用户云存储文件接口（HTTP 编排层）。每个端点只负责：取认证用户、取参数、调 <see cref="IFileService"/>
/// 业务方法、把结果或 <see cref="FileServiceException"/> 转成 HTTP 响应。真正的存储逻辑在 FileService，
/// 下载的流式写出在 <see cref="FileTransfer"/>，本类不含任何文件系统或传输细节。
/// </summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files").RequireAuthorization();

        // GET /api/files/list?path=docs/photos
        group.MapGet("/list", IResult (string? path, HttpContext ctx, IFileService files) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.List】进入列出文件端点，Path={path ?? "(null)"}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.List】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.List】UserId={userId}，Path={path ?? "(null)"}");
#endif
            try
            {
                var result = files.List(userId, path);
#if DEBUG
                Log.Info($"【FileEndpoints.List】列出文件成功，UserId={userId}，EntryCount={result.Entries.Count}");
#endif
                return TypedResults.Json(
                    result,
                    AppJsonSerializerContext.Default.FileListResponse);
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.List】列出文件失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // POST /api/files/mkdir   body: { "path": "docs/2026/photos" }
        group.MapPost("/mkdir", IResult (MkDirRequest request, HttpContext ctx, IFileService files) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.MkDir】进入创建目录端点，Path={request.Path}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.MkDir】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.MkDir】UserId={userId}，Path={request.Path}");
#endif
            try
            {
                var normalized = files.CreateDirectory(userId, request.Path);
#if DEBUG
                Log.Info($"【FileEndpoints.MkDir】创建目录成功，UserId={userId}，Normalized={normalized}");
#endif
                return Message($"目录已创建：{normalized}");
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.MkDir】创建目录失败，UserId={userId}，Path={request.Path}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // POST /api/files/new-file   body: { "path": "docs/notes.txt" }
        // 在指定相对路径新建一个空文件（父目录自动创建）。
        group.MapPost("/new-file", async Task<IResult> (
            NewFileRequest request, HttpContext ctx, IFileService files, CancellationToken ct) =>
        {
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
                return Unauthorized();
            try
            {
                var normalized = await files.CreateFileAsync(userId, request.Path, ct);
                return Message($"文件已创建：{normalized}");
            }
            catch (FileServiceException ex)
            {
                return ToError(ex);
            }
        });

        // POST /api/files/move   body: { "source": "a/x.txt", "dest": "b/x.txt", "overwrite": false }
        // 移动 / 重命名：传完整目标路径，故同一端点既可改名也可移动到其他目录。文件与目录均可。
        group.MapPost("/move", async Task<IResult> (
            MoveRequest request, HttpContext ctx, IFileService files, CancellationToken ct) =>
        {
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
                return Unauthorized();
            try
            {
                var (src, dst, isDir) = await files.MoveAsync(
                    userId, request.Source, request.Dest, request.Overwrite, ct);
                return Message(isDir ? $"目录已移动：{src} → {dst}" : $"文件已移动：{src} → {dst}");
            }
            catch (FileServiceException ex)
            {
                return ToError(ex);
            }
        });

        // POST /api/files/copy   body: { "source": "a/x.txt", "dest": "b/x.txt", "overwrite": false }
        // 复制：传完整目标路径。支持文件与目录（目录递归复制）。
        group.MapPost("/copy", async Task<IResult> (
            CopyRequest request, HttpContext ctx, IFileService files, CancellationToken ct) =>
        {
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
                return Unauthorized();
            try
            {
                var (src, dst, isDir) = await files.CopyAsync(
                    userId, request.Source, request.Dest, request.Overwrite, ct);
                return Message(isDir ? $"目录已复制：{src} → {dst}" : $"文件已复制：{src} → {dst}");
            }
            catch (FileServiceException ex)
            {
                return ToError(ex);
            }
        });

        // PUT /api/files/upload?path=docs/2026/report.pdf&overwrite=false
        group.MapPut("/upload", async Task<IResult> (
            string? path, HttpContext ctx, IFileService files, IOptions<StorageSettings> settings,
            CancellationToken ct, bool overwrite = false) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.Upload】进入上传文件端点，Path={path ?? "(null)"}，Overwrite={overwrite}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.Upload】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.Upload】UserId={userId}，Path={path ?? "(null)"}，Overwrite={overwrite}，ContentLength={ctx.Request.ContentLength?.ToString() ?? "(null)"}");
#endif

            // 放宽 Kestrel 默认 30MB 的请求体上限到配置的存储上限（仅本端点的 HTTP 层提示）；
            // 真正的大小校验在 FileService 内边写边计数完成。
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = settings.Value.MaxUploadBytes;

            try
            {
                var result = await files.UploadAsync(
                    userId, path, ctx.Request.Body, ctx.Request.ContentLength, overwrite, ct);
#if DEBUG
                Log.Info($"【FileEndpoints.Upload】上传文件成功，UserId={userId}，Path={path ?? "(null)"}，Size={result.Size}");
#endif
                return TypedResults.Json(result, AppJsonSerializerContext.Default.UploadResponse);
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.Upload】上传文件失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // POST /api/files/upload-zip?path=docs/2026&overwrite=false
        group.MapPost("/upload-zip", async Task<IResult> (
            string? path, HttpContext ctx, IFileService files, IOptions<StorageSettings> settings,
            CancellationToken ct, bool overwrite = false) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.UploadZip】进入上传压缩包端点，Path={path ?? "(null)"}，Overwrite={overwrite}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.UploadZip】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.UploadZip】UserId={userId}，Path={path ?? "(null)"}，Overwrite={overwrite}，ContentLength={ctx.Request.ContentLength?.ToString() ?? "(null)"}");
#endif

            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = settings.Value.MaxUploadBytes;

            try
            {
                var result = await files.UploadZipAsync(
                    userId, path, ctx.Request.Body, ctx.Request.ContentLength, overwrite, ct);
#if DEBUG
                Log.Info($"【FileEndpoints.UploadZip】上传压缩包成功，UserId={userId}，Path={path ?? "(null)"}，Files={result.Files}，Directories={result.Directories}，TotalBytes={result.TotalBytes}");
#endif
                return TypedResults.Json(result, AppJsonSerializerContext.Default.UploadZipResponse);
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.UploadZip】上传压缩包失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // GET /api/files/download?path=docs/2026/report.pdf
        group.MapGet("/download", async Task<IResult> (
            string? path, HttpContext ctx, IFileService files, IAsyncIoEngine engine, CancellationToken ct) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.Download】进入下载文件端点，Path={path ?? "(null)"}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.Download】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.Download】UserId={userId}，Path={path ?? "(null)"}");
#endif
            return await ServeAsync(ctx, files, engine, userId, path, inline: false, ct);
        });

        // POST /api/files/download-ticket?path=...&inline=false
        // inline=true 签发内联预览票据（图片/音视频/文本在线查看）。
        group.MapPost("/download-ticket", IResult (
            string? path, HttpContext ctx, IFileService files, IJwtTokenService jwt, bool inline = false) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.DownloadTicket】进入申请下载票据签发端点，Path={path ?? "(null)"}，Inline={inline}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.DownloadTicket】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.DownloadTicket】UserId={userId}，Path={path ?? "(null)"}，Inline={inline}");
#endif
            try
            {
                // 仅校验路径合法并存在（预览只对文件签发）；真正下载时还会再校验一次。
                files.ResolveDownload(userId, path, requireFile: inline);
                var ticket = jwt.GenerateDownloadTicket(userId, path ?? string.Empty, inline);
#if DEBUG
                Log.Info($"【FileEndpoints.DownloadTicket】申请下载票据签发成功，UserId={userId}，Path={path ?? "(null)"}");
#endif
                return TypedResults.Json(
                    new DownloadTicketResponse(ticket),
                    AppJsonSerializerContext.Default.DownloadTicketResponse);
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.DownloadTicket】申请下载票据签发失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // GET /api/files/download-by-ticket?ticket=<jwt>  （票据即授权，不在受保护组内）
        app.MapGet("/api/files/download-by-ticket", async Task<IResult> (
            string? ticket, HttpContext ctx, IFileService files, IJwtTokenService jwt,
            IAsyncIoEngine engine, CancellationToken ct) =>
        {
#if DEBUG
            var ticketPreview = ticket?.Length > 0 ? ticket[..Math.Min(8, ticket.Length)] : "(null)";
            Log.Info($"【FileEndpoints.DownloadByTicket】进入获取下载票据签发端点，Ticket={ticketPreview}...");
#endif
            if (string.IsNullOrEmpty(ticket)
                || !jwt.TryValidateDownloadTicket(ticket, out var userId, out var relativePath, out var inline))
            {
#if DEBUG
                Log.Error($"【FileEndpoints.DownloadByTicket】下载票据无效或已过期，Ticket={ticketPreview}...");
#endif
                return Error(StatusCodes.Status401Unauthorized, "下载票据无效或已过期");
            }
#if DEBUG
            Log.Info($"【FileEndpoints.DownloadByTicket】票据验证成功，UserId={userId}，Path={relativePath}，Inline={inline}");
#endif
            return await ServeAsync(ctx, files, engine, userId, relativePath, inline, ct);
        });

        // DELETE /api/files/delete?path=docs/2026&recursive=true
        group.MapDelete("/delete", async Task<IResult> (
            string? path, HttpContext ctx, IFileService files, CancellationToken ct, bool recursive = false) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.Delete】进入删除文件端点，Path={path ?? "(null)"}，Recursive={recursive}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.Delete】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.Delete】UserId={userId}，Path={path ?? "(null)"}，Recursive={recursive}");
#endif
            try
            {
                var (normalized, wasDir) = await files.DeleteAsync(userId, path, recursive, ct);
#if DEBUG
                Log.Info($"【FileEndpoints.Delete】删除成功，UserId={userId}，Normalized={normalized}，WasDirectory={wasDir}");
#endif
                return Message(wasDir ? $"目录已删除：{normalized}" : $"文件已删除：{normalized}");
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.Delete】删除失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // GET /api/files/text?path=notes/todo.md
        group.MapGet("/text", async Task<IResult> (
            string? path, HttpContext ctx, IFileService files, CancellationToken ct) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.ReadText】进入在线读取文件端点，Path={path ?? "(null)"}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.ReadText】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.ReadText】UserId={userId}，Path={path ?? "(null)"}");
#endif
            try
            {
                var result = await files.ReadTextAsync(userId, path, ct);
#if DEBUG
                Log.Info($"【FileEndpoints.ReadText】在线读取文件成功，UserId={userId}，Path={path ?? "(null)"}，Size={result.Size}，ContentLength={result.Content.Length}");
#endif
                return TypedResults.Json(result, AppJsonSerializerContext.Default.TextContentResponse);
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.ReadText】在线读取文件失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });

        // POST /api/files/text  body: { path, content }
        group.MapPost("/text", async Task<IResult> (
            SaveTextRequest request, HttpContext ctx, IFileService files, CancellationToken ct) =>
        {
#if DEBUG
            Log.Info($"【FileEndpoints.SaveText】进入在线覆盖文件端点，Path={request.Path}，ContentLength={request.Content.Length}");
#endif
            if (!CurrentUser.TryGetUserId(ctx, out var userId))
            {
#if DEBUG
                Log.Error("【FileEndpoints.SaveText】未认证，返回 401");
#endif
                return Unauthorized();
            }
#if DEBUG
            Log.Info($"【FileEndpoints.SaveText】UserId={userId}，Path={request.Path}，ContentLength={request.Content.Length}");
#endif
            try
            {
                var result = await files.SaveTextAsync(userId, request.Path, request.Content, ct);
#if DEBUG
                Log.Info($"【FileEndpoints.SaveText】在线覆盖文件成功，UserId={userId}，Path={request.Path}，Size={result.Size}");
#endif
                return TypedResults.Json(result, AppJsonSerializerContext.Default.UploadResponse);
            }
            catch (FileServiceException ex)
            {
#if DEBUG
                Log.Error($"【FileEndpoints.SaveText】在线覆盖文件失败，UserId={userId}，Path={request.Path}，Kind={ex.Kind}，Message={ex.Message}");
#endif
                return ToError(ex);
            }
        });
    }

    // ====== HTTP 编排助手 ======

    /// <summary>解析下载目标（业务层）后，交给 <see cref="FileTransfer"/> 流式写出（传输层）。</summary>
    private static async Task<IResult> ServeAsync(
        HttpContext ctx, IFileService files, IAsyncIoEngine engine, Guid userId, string? path, bool inline, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileEndpoints.ServeAsync】进入下载服务方法，UserId={userId}，Path={path ?? "(null)"}，Inline={inline}");
#endif
        try
        {
            var descriptor = files.ResolveDownload(userId, path, requireFile: false);
#if DEBUG
            Log.Info($"【FileEndpoints.ServeAsync】下载目标解析成功，UserId={userId}，Path={path ?? "(null)"}，Kind={descriptor.Kind}，DownloadName={descriptor.DownloadName}，Length={descriptor.Length}");
#endif
            await FileTransfer.SendAsync(ctx, engine, descriptor, inline, ct);
#if DEBUG
            Log.Info($"【FileEndpoints.ServeAsync】文件传输完成，UserId={userId}，Path={path ?? "(null)"}");
#endif
            return Results.Empty;
        }
        catch (FileServiceException ex)
        {
#if DEBUG
            Log.Error($"【FileEndpoints.ServeAsync】下载服务失败，UserId={userId}，Path={path ?? "(null)"}，Kind={ex.Kind}，Message={ex.Message}");
#endif
            return ToError(ex);
        }
    }

    private static JsonHttpResult<MessageResponse> Message(string message) =>
        TypedResults.Json(new MessageResponse(message), AppJsonSerializerContext.Default.MessageResponse);

    private static JsonHttpResult<ErrorResponse> Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, "未认证");

    private static JsonHttpResult<ErrorResponse> Error(int statusCode, string message) =>
        TypedResults.Json(
            new ErrorResponse(message),
            AppJsonSerializerContext.Default.ErrorResponse,
            statusCode: statusCode);

    /// <summary>把领域异常映射成对应 HTTP 状态码的错误响应（与 AuthEndpoints.ToError 同构）。</summary>
    private static JsonHttpResult<ErrorResponse> ToError(FileServiceException ex)
    {
        var statusCode = ex.Kind switch
        {
            FileErrorKind.NotFound         => StatusCodes.Status404NotFound,
            FileErrorKind.Conflict         => StatusCodes.Status409Conflict,
            FileErrorKind.PayloadTooLarge  => StatusCodes.Status413PayloadTooLarge,
            FileErrorKind.UnsupportedMedia => StatusCodes.Status415UnsupportedMediaType,
            _                              => StatusCodes.Status400BadRequest
        };
        return TypedResults.Json(
            new ErrorResponse(ex.Message),
            AppJsonSerializerContext.Default.ErrorResponse,
            statusCode: statusCode);
    }
}
