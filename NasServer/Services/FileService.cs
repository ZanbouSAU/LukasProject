// NasServer/Services/FileService.cs

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NasServer.Configuration;
using NasServer.DTOs;
using NasServer.Services.Interface;
using NasServer.Services.Storage;
using Lukas.AsyncEngine;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace NasServer.Services;

/// <summary>
/// <see cref="IFileService"/> 的实现：承载原先散落在 FileEndpoints 里的全部存储业务逻辑。
/// 写入沿用安全套路——经 <see cref="PositionedFile"/> 定位写入临时文件，成功后原子改名；
/// 同名目标用 <see cref="PathLockPool"/> 串行化。所有路径经 <see cref="StoragePaths"/> 强校验。
/// </summary>
public sealed class FileService(
    StoragePaths paths,
    PathLockPool locks,
    IAsyncIoEngine engine,
    IOptions<StorageSettings> settings) : IFileService
{
    /// <summary>上传/拷贝的流式缓冲大小。</summary>
    private const int CopyBufferSize = 1024 * 1024;

    private const string UploadTempPrefix = ".nas-upload-";

    /// <summary>严格 UTF-8 解码器：遇到非法字节抛 <see cref="DecoderFallbackException"/>，用于甄别二进制文件。</summary>
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private StorageSettings Settings => settings.Value;

    // ---------------------------------------------------------------- 列目录

    public FileListResponse List(Guid userId, string? relativePath)
    {
#if DEBUG
        Log.Info($"【FileService.List】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}");
#endif

        if (!paths.TryResolve(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.List】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.List】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        if (normalized.Length == 0)
            paths.EnsureUserRoot(userId);

        if (Io.File.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.List】目标是一个文件，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "目标不是目录");
        }
        if (!Io.Directory.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.List】目录不存在，抛出 NotFound");
#endif
            throw new FileServiceException(FileErrorKind.NotFound, "目录不存在");
        }

        var entries = new List<FileEntry>();
        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            if (info.LinkTarget is not null)
                continue; // 符号链接一律不展示，防止借链接探测/逃出用户目录。

            var isDir = (info.Attributes & FileAttributes.Directory) != 0;
            var rel = normalized.Length == 0 ? info.Name : $"{normalized}/{info.Name}";
            entries.Add(new FileEntry(
                Name: info.Name,
                Path: rel,
                IsDirectory: isDir,
                Size: isDir ? 0 : ((FileInfo)info).Length,
                ModifiedAtUtc: info.LastWriteTimeUtc));
        }

        entries.Sort(static (a, b) => a.IsDirectory == b.IsDirectory
            ? string.CompareOrdinal(a.Name, b.Name)
            : a.IsDirectory ? -1 : 1);

#if DEBUG
        Log.Info($"【FileService.List】成功列出 {entries.Count} 个条目");
#endif

        return new FileListResponse(normalized, entries);
    }

    // ---------------------------------------------------------------- 创建目录

    public string CreateDirectory(Guid userId, string? relativePath)
    {
#if DEBUG
        Log.Info($"【FileService.CreateDirectory】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}");
#endif

        if (!paths.TryResolveNonRoot(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.CreateDirectory】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.CreateDirectory】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        if (Io.File.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.CreateDirectory】同名文件已存在，抛出 Conflict");
#endif
            throw new FileServiceException(FileErrorKind.Conflict, "同名文件已存在");
        }

        paths.EnsureUserRoot(userId);
        Io.File.CreateDirectories(full);

#if DEBUG
        Log.Info($"【FileService.CreateDirectory】目录创建成功，Normalized={normalized}");
#endif

        return normalized;
    }

    // ---------------------------------------------------------------- 上传文件

    public async Task<UploadResponse> UploadAsync(
        Guid userId, string? relativePath, Stream body, long? declaredLength, bool overwrite, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileService.UploadAsync】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}，DeclaredLength={declaredLength?.ToString() ?? "(null)"}，Overwrite={overwrite}");
#endif

        if (!paths.TryResolveNonRoot(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.UploadAsync】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.UploadAsync】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        var cap = Settings.MaxUploadBytes;
        if (declaredLength is { } declared && declared > cap)
        {
#if DEBUG
            Log.Info($"【FileService.UploadAsync】声明大小 {declared} 超过上限 {cap}，抛出 PayloadTooLarge");
#endif
            throw new FileServiceException(FileErrorKind.PayloadTooLarge, "文件超过允许的最大大小");
        }

        if (Io.Directory.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.UploadAsync】目标路径是一个目录，抛出 Conflict");
#endif
            throw new FileServiceException(FileErrorKind.Conflict, "目标路径是一个目录");
        }

        paths.EnsureUserRoot(userId);
        var parent = Io.Path.GetDirectoryName(full);
        Io.File.CreateDirectories(parent);

#if DEBUG
        Log.Info($"【FileService.UploadAsync】准备获取路径锁，FullPath={full}");
#endif
        using var pathLock = await locks.AcquireAsync(full, ct);

        if (!overwrite && Io.File.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.UploadAsync】文件已存在且 overwrite=false，抛出 Conflict");
#endif
            throw new FileServiceException(FileErrorKind.Conflict, "文件已存在（可加 overwrite=true 覆盖）");
        }

        var tempPath = Io.Path.Combine(parent, $"{UploadTempPrefix}{Guid.NewGuid():N}.part");
#if DEBUG
        Log.Info($"【FileService.UploadAsync】临时文件路径：{tempPath}");
#endif

        long total;
        try
        {
            total = await WriteStreamToFileAsync(body, tempPath, cap, ct);
#if DEBUG
            Log.Info($"【FileService.UploadAsync】写入完成，总字节数={total}");
#endif
        }
        catch (PayloadTooLargeException)
        {
#if DEBUG
            Log.Info("【FileService.UploadAsync】写入时超出大小限制，清理临时文件并抛出 PayloadTooLarge");
#endif
            TryDelete(tempPath);
            throw new FileServiceException(FileErrorKind.PayloadTooLarge, "文件超过允许的最大大小");
        }
        catch
        {
#if DEBUG
            Log.Info("【FileService.UploadAsync】写入异常，清理临时文件并重新抛出");
#endif
            TryDelete(tempPath);
            throw;
        }

        // 写入完整后才原子改名为最终文件，绝不暴露半截文件。
#if DEBUG
        Log.Info($"【FileService.UploadAsync】原子移动临时文件到目标：{tempPath} -> {full}");
#endif
        Io.File.Move(tempPath, full, overwrite: true);

#if DEBUG
        Log.Info($"【FileService.UploadAsync】上传成功，Normalized={normalized}，Size={total}");
#endif

        return new UploadResponse(normalized, total);
    }

    // ---------------------------------------------------------------- 上传 zip 解压

    public async Task<UploadZipResponse> UploadZipAsync(
        Guid userId, string? relativePath, Stream body, long? declaredLength, bool overwrite, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileService.UploadZipAsync】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}，DeclaredLength={declaredLength?.ToString() ?? "(null)"}，Overwrite={overwrite}");
#endif

        if (!paths.TryResolve(userId, relativePath, out var targetFull, out var targetRel))
        {
#if DEBUG
            Log.Info("【FileService.UploadZipAsync】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.UploadZipAsync】路径解析成功，TargetFull={targetFull}，TargetRel={targetRel}");
#endif

        if (Io.File.Exists(targetFull))
        {
#if DEBUG
            Log.Info("【FileService.UploadZipAsync】目标是一个文件，抛出 Conflict");
#endif
            throw new FileServiceException(FileErrorKind.Conflict, "目标路径是一个文件");
        }

        var cap = Settings.MaxUploadBytes;
        if (declaredLength is { } declared && declared > cap)
        {
#if DEBUG
            Log.Info($"【FileService.UploadZipAsync】声明大小 {declared} 超过上限 {cap}，抛出 PayloadTooLarge");
#endif
            throw new FileServiceException(FileErrorKind.PayloadTooLarge, "压缩包超过允许的最大大小");
        }

        paths.EnsureUserRoot(userId);
        Io.File.CreateDirectories(targetFull);

        var zipTemp = Io.Path.Combine(targetFull, $"{UploadTempPrefix}{Guid.NewGuid():N}.zip.tmp");
#if DEBUG
        Log.Info($"【FileService.UploadZipAsync】ZIP临时文件路径：{zipTemp}");
#endif

        try
        {
            try
            {
                await WriteStreamToFileAsync(body, zipTemp, cap, ct);
#if DEBUG
                Log.Info("【FileService.UploadZipAsync】ZIP文件写入完成");
#endif
            }
            catch (PayloadTooLargeException)
            {
#if DEBUG
                Log.Info("【FileService.UploadZipAsync】ZIP文件写入时超出大小限制，抛出 PayloadTooLarge");
#endif
                throw new FileServiceException(FileErrorKind.PayloadTooLarge, "压缩包超过允许的最大大小");
            }

            int files = 0, directories = 0;
            long totalBytes = 0;

            await using var zipStream = new FileStream(
                zipTemp, FileMode.Open, FileAccess.Read, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
            await using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count > Settings.MaxZipEntries)
            {
#if DEBUG
                Log.Info($"【FileService.UploadZipAsync】ZIP条目数 {archive.Entries.Count} 超过上限 {Settings.MaxZipEntries}，抛出 BadRequest");
#endif
                throw new FileServiceException(FileErrorKind.BadRequest, "压缩包条目数超过上限");
            }

#if DEBUG
            Log.Info($"【FileService.UploadZipAsync】开始解压，ZIP条目数={archive.Entries.Count}");
#endif

            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var entryRel = entry.FullName.Replace('\\', '/').Trim('/');
                    if (entryRel.Length == 0)
                        continue;

                    var combinedRel = targetRel.Length == 0 ? entryRel : $"{targetRel}/{entryRel}";

                    // 逐条目走与普通路径完全相同的强校验，天然免疫 zip-slip（"../"、绝对路径、盘符等）。
                    if (!paths.TryResolveNonRoot(userId, combinedRel, out var entryFull, out _))
                    {
#if DEBUG
                        Log.Info($"【FileService.UploadZipAsync】条目路径非法：{entry.FullName}，抛出 BadRequest");
#endif
                        throw new FileServiceException(FileErrorKind.BadRequest, $"压缩包内含非法路径：{entry.FullName}");
                    }

                    var isDirectoryEntry = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    if (isDirectoryEntry)
                    {
#if DEBUG
                        Log.Info($"【FileService.UploadZipAsync】创建目录条目：{entry.FullName}");
#endif
                        Io.File.CreateDirectories(entryFull);
                        directories++;
                        continue;
                    }

#if DEBUG
                    Log.Info($"【FileService.UploadZipAsync】处理文件条目：{entry.FullName}，目标路径={entryFull}");
#endif

                    if (Io.Directory.Exists(entryFull))
                    {
#if DEBUG
                        Log.Info($"【FileService.UploadZipAsync】条目与已有目录同名：{entry.FullName}，抛出 Conflict");
#endif
                        throw new FileServiceException(FileErrorKind.Conflict, $"条目与已有目录同名：{entry.FullName}");
                    }
                    if (!overwrite && Io.File.Exists(entryFull))
                    {
#if DEBUG
                        Log.Info($"【FileService.UploadZipAsync】文件已存在且 overwrite=false：{entry.FullName}，抛出 Conflict");
#endif
                        throw new FileServiceException(FileErrorKind.Conflict,
                            $"文件已存在（可加 overwrite=true 覆盖）：{entry.FullName}");
                    }

                    Io.File.CreateDirectories(Io.Path.GetDirectoryName(entryFull));

#if DEBUG
                    Log.Info($"【FileService.UploadZipAsync】获取条目路径锁：{entryFull}");
#endif
                    using var entryLock = await locks.AcquireAsync(entryFull, ct);

                    // 不信任 zip 头里声明的长度，边解压边累计真实字节数防 zip 炸弹。
                    await using var source = await entry.OpenAsync(ct);
                    var part = await PositionedFile.OpenAsync(engine, entryFull + ".part", Flags.Create, ct: ct);
                    try
                    {
                        long offset = 0;
                        int n;
                        while ((n = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct)) > 0)
                        {
                            totalBytes += n;
                            if (totalBytes > cap)
                            {
#if DEBUG
                                Log.Info($"【FileService.UploadZipAsync】解压后总大小 {totalBytes} 超过上限 {cap}，抛出 PayloadTooLarge");
#endif
                                throw new FileServiceException(FileErrorKind.PayloadTooLarge, "解压后总大小超过允许上限");
                            }
                            await part.WriteAllAsync(buffer.AsMemory(0, n), offset, ct);
                            offset += n;
                        }
                        await part.CloseAsync(ct);
                        Io.File.Move(entryFull + ".part", entryFull, overwrite: true);
                        files++;
#if DEBUG
                        Log.Info($"【FileService.UploadZipAsync】文件条目解压完成：{entry.FullName}，大小={offset}");
#endif
                    }
                    finally
                    {
                        await part.DisposeAsync();
                        TryDelete(entryFull + ".part");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

#if DEBUG
            Log.Info($"【FileService.UploadZipAsync】解压完成，文件数={files}，目录数={directories}，总字节数={totalBytes}");
#endif

            return new UploadZipResponse(targetRel, files, directories, totalBytes);
        }
        finally
        {
            TryDelete(zipTemp);
#if DEBUG
            Log.Info($"【FileService.UploadZipAsync】清理ZIP临时文件：{zipTemp}");
#endif
        }
    }

    // ---------------------------------------------------------------- 删除

    public async Task<(string Normalized, bool WasDirectory)> DeleteAsync(
        Guid userId, string? relativePath, bool recursive, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileService.DeleteAsync】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}，Recursive={recursive}");
#endif

        if (!paths.TryResolveNonRoot(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.DeleteAsync】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径（不允许删除根目录）");
        }

#if DEBUG
        Log.Info($"【FileService.DeleteAsync】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        if (Io.File.Exists(full))
        {
#if DEBUG
            Log.Info($"【FileService.DeleteAsync】删除文件：{full}");
#endif
            using var pathLock = await locks.AcquireAsync(full, ct);
            Io.File.DeleteFile(full);
#if DEBUG
            Log.Info("【FileService.DeleteAsync】文件删除成功");
#endif
            return (normalized, false);
        }

        if (Io.Directory.Exists(full))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
            {
#if DEBUG
                Log.Info("【FileService.DeleteAsync】目录非空且 recursive=false，抛出 Conflict");
#endif
                throw new FileServiceException(FileErrorKind.Conflict, "目录非空（可加 recursive=true 递归删除）");
            }

#if DEBUG
            Log.Info($"【FileService.DeleteAsync】删除目录：{full}，Recursive={recursive}");
#endif
            Io.Directory.Delete(full, recursive);
#if DEBUG
            Log.Info("【FileService.DeleteAsync】目录删除成功");
#endif
            return (normalized, true);
        }

#if DEBUG
        Log.Info($"【FileService.DeleteAsync】文件或目录不存在，抛出 NotFound");
#endif
        throw new FileServiceException(FileErrorKind.NotFound, "文件或目录不存在");
    }

    // ---------------------------------------------------------------- 文本读

    public async Task<TextContentResponse> ReadTextAsync(Guid userId, string? relativePath, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileService.ReadTextAsync】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}");
#endif

        if (!paths.TryResolveNonRoot(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.ReadTextAsync】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.ReadTextAsync】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        if (Io.Directory.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.ReadTextAsync】目标是一个目录，抛出 Conflict");
#endif
            throw new FileServiceException(FileErrorKind.Conflict, "目标是一个目录");
        }
        if (!Io.File.Exists(full) || new FileInfo(full).LinkTarget is not null)
        {
#if DEBUG
            Log.Info("【FileService.ReadTextAsync】文件不存在，抛出 NotFound");
#endif
            throw new FileServiceException(FileErrorKind.NotFound, "文件不存在");
        }

        var length = Io.File.GetFileLength(full);
#if DEBUG
        Log.Info($"【FileService.ReadTextAsync】文件大小={length}，上限={Settings.MaxTextEditBytes}");
#endif

        if (length > Settings.MaxTextEditBytes)
        {
#if DEBUG
            Log.Info("【FileService.ReadTextAsync】文件过大，抛出 PayloadTooLarge");
#endif
            throw new FileServiceException(FileErrorKind.PayloadTooLarge, "文件过大，无法在线打开（请下载后查看）");
        }

        string content;
        if (length == 0)
        {
            content = string.Empty;
#if DEBUG
            Log.Info("【FileService.ReadTextAsync】文件为空");
#endif
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)length);
            var file = await PositionedFile.OpenAsync(engine, full, Flags.Read, ct: ct);
            try
            {
                var read = await file.ReadFullAsync(buffer.AsMemory(0, (int)length), 0, ct);
#if DEBUG
                Log.Info($"【FileService.ReadTextAsync】读取字节数={read}");
#endif
                // 严格 UTF-8 解码：含非法字节（多半是二进制文件）则拒绝在线打开。
                try
                {
                    content = StrictUtf8.GetString(buffer.AsSpan(0, read));
#if DEBUG
                    Log.Info($"【FileService.ReadTextAsync】UTF-8解码成功，内容长度={content.Length}");
#endif
                }
                catch (DecoderFallbackException)
                {
#if DEBUG
                    Log.Info($"【FileService.ReadTextAsync】不是有效的UTF-8文本文件，抛出 UnsupportedMedia");
#endif
                    throw new FileServiceException(FileErrorKind.UnsupportedMedia, "不是有效的 UTF-8 文本文件");
                }
            }
            finally
            {
                await file.DisposeAsync();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return new TextContentResponse(normalized, content, length);
    }

    // ---------------------------------------------------------------- 文本写

    public async Task<UploadResponse> SaveTextAsync(Guid userId, string relativePath, string content, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileService.SaveTextAsync】进入方法，UserId={userId}，RelativePath={relativePath}，ContentLength={content.Length}");
#endif

        if (!paths.TryResolveNonRoot(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.SaveTextAsync】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.SaveTextAsync】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        // 用 Utf8.FromUtf16 直接转码，避免 Encoding.UTF8.GetBytes 的额外分配；非法 UTF-16 视为坏请求。
        var buffer = new byte[Encoding.UTF8.GetMaxByteCount(content.Length)];
        if (Utf8.FromUtf16(content, buffer, out _, out var bytesWritten) != OperationStatus.Done)
        {
#if DEBUG
            Log.Info("【FileService.SaveTextAsync】非法UTF-16序列，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "内容包含非法的 UTF-16 序列");
        }
        var bytes = buffer.AsMemory(0, bytesWritten);

#if DEBUG
        Log.Info($"【FileService.SaveTextAsync】UTF-8编码后字节数={bytes.Length}，上限={Settings.MaxTextEditBytes}");
#endif

        if (bytes.Length > Settings.MaxTextEditBytes)
        {
#if DEBUG
            Log.Info("【FileService.SaveTextAsync】内容过大，抛出 PayloadTooLarge");
#endif
            throw new FileServiceException(FileErrorKind.PayloadTooLarge, "内容过大");
        }

        if (Io.Directory.Exists(full))
        {
#if DEBUG
            Log.Info("【FileService.SaveTextAsync】目标路径是一个目录，抛出 Conflict");
#endif
            throw new FileServiceException(FileErrorKind.Conflict, "目标路径是一个目录");
        }

        paths.EnsureUserRoot(userId);
        var parent = Io.Path.GetDirectoryName(full);
        Io.File.CreateDirectories(parent);

#if DEBUG
        Log.Info($"【FileService.SaveTextAsync】获取路径锁：{full}");
#endif
        using var pathLock = await locks.AcquireAsync(full, ct);

        var tempPath = Io.Path.Combine(parent, $"{UploadTempPrefix}{Guid.NewGuid():N}.part");
#if DEBUG
        Log.Info($"【FileService.SaveTextAsync】临时文件路径：{tempPath}");
#endif

        try
        {
            var file = await PositionedFile.OpenAsync(engine, tempPath, Flags.Create, ct: ct);
            try
            {
                if (bytes.Length > 0)
                    await file.WriteAllAsync(bytes, 0, ct);
                await file.CloseAsync(ct);
#if DEBUG
                Log.Info("【FileService.SaveTextAsync】写入完成");
#endif
            }
            finally
            {
                await file.DisposeAsync();
            }
            Io.File.Move(tempPath, full, overwrite: true);
#if DEBUG
            Log.Info($"【FileService.SaveTextAsync】原子移动成功：{tempPath} -> {full}");
#endif
        }
        catch
        {
#if DEBUG
            Log.Info("【FileService.SaveTextAsync】异常，清理临时文件并重新抛出");
#endif
            TryDelete(tempPath);
            throw;
        }

#if DEBUG
        Log.Info($"【FileService.SaveTextAsync】保存成功，Normalized={normalized}，Size={bytes.Length}");
#endif

        return new UploadResponse(normalized, bytes.Length);
    }

    // ---------------------------------------------------------------- 新建空文件

    public async Task<string> CreateFileAsync(Guid userId, string? relativePath, CancellationToken ct)
    {
        if (!paths.TryResolveNonRoot(userId, relativePath, out var full, out var normalized))
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");

        if (Io.Directory.Exists(full))
            throw new FileServiceException(FileErrorKind.Conflict, "同名目录已存在");

        paths.EnsureUserRoot(userId);
        var parent = Io.Path.GetDirectoryName(full);
        Io.File.CreateDirectories(parent);

        using var pathLock = await locks.AcquireAsync(full, ct);

        if (Io.File.Exists(full))
            throw new FileServiceException(FileErrorKind.Conflict, "文件已存在");

        // 原子创建空文件：写临时空文件再改名，避免与并发上传竞争产生半截文件。
        var tempPath = Io.Path.Combine(parent, $"{UploadTempPrefix}{Guid.NewGuid():N}.part");
        try
        {
            var file = await PositionedFile.OpenAsync(engine, tempPath, Flags.Create, ct: ct);
            await file.CloseAsync(ct);
            await file.DisposeAsync();
            Io.File.Move(tempPath, full, overwrite: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return normalized;
    }

    // ---------------------------------------------------------------- 移动 / 重命名

    public async Task<(string Source, string Dest, bool IsDirectory)> MoveAsync(
        Guid userId, string? source, string? dest, bool overwrite, CancellationToken ct)
    {
        if (!paths.TryResolveNonRoot(userId, source, out var srcFull, out var srcNorm))
            throw new FileServiceException(FileErrorKind.BadRequest, "非法的源路径（不允许移动根目录）");
        if (!paths.TryResolveNonRoot(userId, dest, out var dstFull, out var dstNorm))
            throw new FileServiceException(FileErrorKind.BadRequest, "非法的目标路径");

        var srcIsDir = Io.Directory.Exists(srcFull);
        var srcIsFile = Io.File.Exists(srcFull);
        if (!srcIsDir && !srcIsFile)
            throw new FileServiceException(FileErrorKind.NotFound, "源文件或目录不存在");

        if (srcNorm == dstNorm)
            throw new FileServiceException(FileErrorKind.BadRequest, "源与目标相同");

        // 不允许把目录移动到自身或其子目录内（否则 rename 会失败或制造环）。
        if (srcIsDir && (dstNorm == srcNorm || dstNorm.StartsWith(srcNorm + "/", StringComparison.Ordinal)))
            throw new FileServiceException(FileErrorKind.BadRequest, "不能把目录移动到自身或其子目录内");

        paths.EnsureUserRoot(userId);
        var parent = Io.Path.GetDirectoryName(dstFull);
        Io.File.CreateDirectories(parent);

        // 锁定源与目标（按字典序固定加锁顺序，避免与反向操作死锁）。
        string first, second;
        if (string.CompareOrdinal(srcFull, dstFull) <= 0) { first = srcFull; second = dstFull; }
        else { first = dstFull; second = srcFull; }
        using var lock1 = await locks.AcquireAsync(first, ct);
        using var lock2 = await locks.AcquireAsync(second, ct);

        var dstIsDir = Io.Directory.Exists(dstFull);
        var dstIsFile = Io.File.Exists(dstFull);
        if (dstIsDir || dstIsFile)
        {
            if (!overwrite)
                throw new FileServiceException(FileErrorKind.Conflict, "目标已存在（可覆盖）");
            // 覆盖：仅允许「文件覆盖文件」。涉及目录的覆盖语义复杂且危险，一律拒绝。
            if (srcIsDir || dstIsDir)
                throw new FileServiceException(FileErrorKind.Conflict, "目标已存在且涉及目录，无法覆盖");
            Io.File.DeleteFile(dstFull);
        }

        // 同一用户根下，rename 对文件与目录都原子有效。
        Io.File.Move(srcFull, dstFull, overwrite: false);

        return (srcNorm, dstNorm, srcIsDir);
    }

    // ---------------------------------------------------------------- 复制

    public async Task<(string Source, string Dest, bool IsDirectory)> CopyAsync(
        Guid userId, string? source, string? dest, bool overwrite, CancellationToken ct)
    {
        if (!paths.TryResolveNonRoot(userId, source, out var srcFull, out var srcNorm))
            throw new FileServiceException(FileErrorKind.BadRequest, "非法的源路径");
        if (!paths.TryResolveNonRoot(userId, dest, out var dstFull, out var dstNorm))
            throw new FileServiceException(FileErrorKind.BadRequest, "非法的目标路径");

        var srcIsDir = Io.Directory.Exists(srcFull);
        var srcIsFile = Io.File.Exists(srcFull);
        if (!srcIsDir && !srcIsFile)
            throw new FileServiceException(FileErrorKind.NotFound, "源文件或目录不存在");

        if (srcNorm == dstNorm)
            throw new FileServiceException(FileErrorKind.BadRequest, "源与目标相同");

        // 不允许把目录复制进自身或其子目录（否则递归会无限展开）。
        if (srcIsDir && (dstNorm == srcNorm || dstNorm.StartsWith(srcNorm + "/", StringComparison.Ordinal)))
            throw new FileServiceException(FileErrorKind.BadRequest, "不能把目录复制到自身或其子目录内");

        paths.EnsureUserRoot(userId);
        var parent = Io.Path.GetDirectoryName(dstFull);
        Io.File.CreateDirectories(parent);

        if (srcIsDir)
        {
            // 目录复制：递归。目标目录若已存在则合并写入；逐文件遵循 overwrite 规则。
            await CopyDirectoryAsync(userId, srcFull, dstFull, overwrite, ct);
            return (srcNorm, dstNorm, true);
        }

        // 文件复制
        using var pathLock = await locks.AcquireAsync(dstFull, ct);
        if (Io.Directory.Exists(dstFull))
            throw new FileServiceException(FileErrorKind.Conflict, "目标是一个已存在的目录");
        if (Io.File.Exists(dstFull))
        {
            if (!overwrite)
                throw new FileServiceException(FileErrorKind.Conflict, "目标文件已存在（可覆盖）");
            Io.File.DeleteFile(dstFull);
        }
        await CopyFileAsync(srcFull, dstFull, ct);
        return (srcNorm, dstNorm, false);
    }

    /// <summary>递归复制目录。逐文件按 <paramref name="overwrite"/> 处理冲突，目录不存在则创建。</summary>
    private async Task CopyDirectoryAsync(Guid userId, string srcDir, string dstDir, bool overwrite, CancellationToken ct)
    {
        Io.File.CreateDirectories(dstDir);

        foreach (var info in new DirectoryInfo(srcDir).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (info.LinkTarget is not null)
                continue; // 跳过符号链接，防止借链接逃出用户目录。

            var childDst = Io.Path.Combine(dstDir, info.Name);
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                await CopyDirectoryAsync(userId, info.FullName, childDst, overwrite, ct);
            }
            else
            {
                using var entryLock = await locks.AcquireAsync(childDst, ct);
                if (Io.File.Exists(childDst))
                {
                    if (!overwrite)
                        throw new FileServiceException(FileErrorKind.Conflict, $"目标已存在：{info.Name}（可覆盖）");
                    Io.File.DeleteFile(childDst);
                }
                await CopyFileAsync(info.FullName, childDst, ct);
            }
        }
    }

    /// <summary>把单个文件复制到目标：写临时文件 + 原子改名，避免暴露半截文件。</summary>
    private async Task CopyFileAsync(string srcFull, string dstFull, CancellationToken ct)
    {
        var parent = Io.Path.GetDirectoryName(dstFull);
        Io.File.CreateDirectories(parent);
        var tempPath = Io.Path.Combine(parent, $"{UploadTempPrefix}{Guid.NewGuid():N}.part");

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var src = await PositionedFile.OpenAsync(engine, srcFull, Flags.Read, ct: ct);
        PositionedFile? dst = null;
        try
        {
            dst = await PositionedFile.OpenAsync(engine, tempPath, Flags.Create, ct: ct);
            long offset = 0;
            while (true)
            {
                var n = await src.ReadFullAsync(buffer.AsMemory(0, CopyBufferSize), offset, ct);
                if (n <= 0) break;
                await dst.WriteAllAsync(buffer.AsMemory(0, n), offset, ct);
                offset += n;
                if (n < CopyBufferSize) break; // 读不满一缓冲即已到文件尾
            }
            await dst.CloseAsync(ct);
            await dst.DisposeAsync();
            dst = null;
            await src.DisposeAsync();

            Io.File.Move(tempPath, dstFull, overwrite: true);
        }
        catch
        {
            if (dst is not null) await dst.DisposeAsync();
            await src.DisposeAsync();
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------------------------------------------------------- 下载解析（仅元数据）

    public DownloadDescriptor ResolveDownload(Guid userId, string? relativePath, bool requireFile)
    {
#if DEBUG
        Log.Info($"【FileService.ResolveDownload】进入方法，UserId={userId}，RelativePath={relativePath ?? "(null)"}，RequireFile={requireFile}");
#endif

        if (!paths.TryResolve(userId, relativePath, out var full, out var normalized))
        {
#if DEBUG
            Log.Info("【FileService.ResolveDownload】路径解析失败，抛出 BadRequest");
#endif
            throw new FileServiceException(FileErrorKind.BadRequest, "非法路径");
        }

#if DEBUG
        Log.Info($"【FileService.ResolveDownload】路径解析成功，FullPath={full}，Normalized={normalized}");
#endif

        if (Io.File.Exists(full))
        {
            if (new FileInfo(full).LinkTarget is not null)
            {
#if DEBUG
                Log.Info("【FileService.ResolveDownload】文件是符号链接，抛出 NotFound");
#endif
                throw new FileServiceException(FileErrorKind.NotFound, "文件不存在");
            }
            var fileName = Io.File.GetFileName(full);
            var fileSize = Io.File.GetFileLength(full);
#if DEBUG
            Log.Info($"【FileService.ResolveDownload】解析为文件，Name={fileName}，Size={fileSize}");
#endif
            return new DownloadDescriptor(
                DownloadTargetKind.File,
                full,
                fileName,
                fileSize);
        }

        if (!requireFile && Io.Directory.Exists(full))
        {
            var downloadName = normalized.Length == 0 ? "root" : Io.File.GetFileName(full);
#if DEBUG
            Log.Info($"【FileService.ResolveDownload】解析为目录，Name={downloadName}");
#endif
            return new DownloadDescriptor(DownloadTargetKind.Directory, full, downloadName, 0);
        }

#if DEBUG
        Log.Info("【FileService.ResolveDownload】文件或目录不存在，抛出 NotFound");
#endif
        throw new FileServiceException(
            FileErrorKind.NotFound, requireFile ? "文件不存在" : "文件或目录不存在");
    }

    // ---------------------------------------------------------------- 内部助手

    /// <summary>把流写入 <paramref name="destPath"/>（经异步引擎定位写入），超过 <paramref name="cap"/> 抛 <see cref="PayloadTooLargeException"/>。</summary>
    private async Task<long> WriteStreamToFileAsync(Stream source, string destPath, long cap, CancellationToken ct)
    {
#if DEBUG
        Log.Info($"【FileService.WriteStreamToFileAsync】进入方法，DestPath={destPath}，Cap={cap}");
#endif

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var file = await PositionedFile.OpenAsync(engine, destPath, Flags.Create, ct: ct);
        try
        {
            long offset = 0;
            int n;
            while ((n = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct)) > 0)
            {
                offset += n;
                if (offset > cap)
                {
#if DEBUG
                    Log.Info($"【FileService.WriteStreamToFileAsync】写入偏移 {offset} 超过上限 {cap}，抛出 PayloadTooLargeException");
#endif
                    throw new PayloadTooLargeException();
                }
                await file.WriteAllAsync(buffer.AsMemory(0, n), offset - n, ct);
            }
            await file.CloseAsync(ct);
#if DEBUG
            Log.Info($"【FileService.WriteStreamToFileAsync】写入完成，总字节数={offset}");
#endif
            return offset;
        }
        finally
        {
            await file.DisposeAsync();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Io.File.Exists(path))
            {
#if DEBUG
                Log.Info($"【FileService.TryDelete】清理临时文件：{path}");
#endif
                Io.File.DeleteFile(path);
            }
        }
        catch
        {
            // 清理临时文件失败不影响主流程。
        }
    }

    /// <summary>解压字节数超限的内部信号，转换为对外的 PayloadTooLarge 错误。</summary>
    private sealed class PayloadTooLargeException : Exception;
}
