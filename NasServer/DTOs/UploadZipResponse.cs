// NasServer/DTOs/UploadZipResponse.cs

namespace NasServer.DTOs;

/// <summary>zip 目录上传结果：解压出的文件数、目录数与总字节数。</summary>
public record UploadZipResponse(string Path, int Files, int Directories, long TotalBytes);
