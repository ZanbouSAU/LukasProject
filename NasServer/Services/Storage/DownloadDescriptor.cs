// NasServer/Services/Storage/DownloadDescriptor.cs

namespace NasServer.Services.Storage;

/// <summary>下载目标的类型：单文件还是目录（目录由端点层打包为 zip）。</summary>
public enum DownloadTargetKind
{
    File,
    Directory
}

/// <summary>
/// <see cref="Interface.IFileService.ResolveDownload"/> 的结果：把"这是什么、在哪、多大、叫什么"
/// 这些与传输无关的元数据交给端点层，由端点负责真正的流式写出（Range、Content-Disposition 等 HTTP 细节）。
/// 这样 Service 不依赖 ASP.NET，下载的 HTTP 编排全部留在端点层，实现彻底解耦。
/// </summary>
/// <param name="FullPath">已校验、位于用户目录内的绝对路径（文件或目录）。</param>
/// <param name="DownloadName">建议的下载名：文件为其文件名，目录为目录名（端点会追加 .zip）。</param>
/// <param name="Length">文件字节数；目录时为 0（打包大小未知，流式输出）。</param>
public readonly record struct DownloadDescriptor(
    DownloadTargetKind Kind,
    string FullPath,
    string DownloadName,
    long Length
);
