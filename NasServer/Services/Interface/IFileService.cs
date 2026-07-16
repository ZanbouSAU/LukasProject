// NasServer/Services/Interface/IFileService.cs

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NasServer.DTOs;
using NasServer.Services.Storage;

namespace NasServer.Services.Interface;

/// <summary>
/// 用户云存储的业务逻辑层：路径校验、临时文件+原子改名+路径锁、zip 解压防护、文本读写等，
/// 全部与 HTTP 无关（不依赖 <c>HttpContext</c>）。校验失败抛 <see cref="FileServiceException"/>，
/// 由端点层统一转成 HTTP 响应。下载只解析元数据（<see cref="DownloadDescriptor"/>），
/// 真正的流式写出由端点层负责，以保持传输层（Range/Content-Disposition）与业务层解耦。
/// </summary>
public interface IFileService
{
    /// <summary>列目录。路径为空表示用户根目录（会自动创建）。</summary>
    FileListResponse List(Guid userId, string? relativePath);

    /// <summary>创建多级目录。</summary>
    string CreateDirectory(Guid userId, string? relativePath);

    /// <summary>上传单个文件（原始流）。<paramref name="overwrite"/> 控制是否覆盖同名文件。</summary>
    Task<UploadResponse> UploadAsync(
        Guid userId, string? relativePath, Stream body, long? declaredLength, bool overwrite, CancellationToken ct);

    /// <summary>上传 zip 并解压到目标目录（带 zip-slip / zip 炸弹防护）。</summary>
    Task<UploadZipResponse> UploadZipAsync(
        Guid userId, string? relativePath, Stream body, long? declaredLength, bool overwrite, CancellationToken ct);

    /// <summary>删除文件或目录。<paramref name="recursive"/> 控制非空目录是否递归删除。返回 (相对路径, 是否为目录)。</summary>
    Task<(string Normalized, bool WasDirectory)> DeleteAsync(Guid userId, string? relativePath, bool recursive, CancellationToken ct);

    /// <summary>在线读取 UTF-8 文本（受大小上限约束，非 UTF-8 拒绝）。</summary>
    Task<TextContentResponse> ReadTextAsync(Guid userId, string? relativePath, CancellationToken ct);

    /// <summary>覆盖保存文本（临时文件+原子改名+路径锁）。</summary>
    Task<UploadResponse> SaveTextAsync(Guid userId, string relativePath, string content, CancellationToken ct);

    /// <summary>在指定相对路径新建一个空文件（父目录自动创建）。同名文件/目录已存在则冲突。返回规范相对路径。</summary>
    Task<string> CreateFileAsync(Guid userId, string? relativePath, CancellationToken ct);

    /// <summary>
    /// 移动 / 重命名：把 <paramref name="source"/> 移到完整目标路径 <paramref name="dest"/>。
    /// 文件与目录均可（同一用户根下用 rename，原子）。<paramref name="overwrite"/> 控制是否覆盖已存在目标。
    /// 返回 (源规范路径, 目标规范路径, 目标是否为目录)。
    /// </summary>
    Task<(string Source, string Dest, bool IsDirectory)> MoveAsync(
        Guid userId, string? source, string? dest, bool overwrite, CancellationToken ct);

    /// <summary>
    /// 复制：把 <paramref name="source"/> 复制到完整目标路径 <paramref name="dest"/>（文件或目录，目录递归）。
    /// <paramref name="overwrite"/> 控制是否覆盖已存在的目标文件。返回 (源规范路径, 目标规范路径, 目标是否为目录)。
    /// </summary>
    Task<(string Source, string Dest, bool IsDirectory)> CopyAsync(
        Guid userId, string? source, string? dest, bool overwrite, CancellationToken ct);

    /// <summary>
    /// 校验下载/预览目标并返回纯元数据（不写响应）。
    /// <paramref name="requireFile"/> 为 true 时（内联预览）只接受文件、拒绝目录。
    /// </summary>
    DownloadDescriptor ResolveDownload(Guid userId, string? relativePath, bool requireFile);
}
