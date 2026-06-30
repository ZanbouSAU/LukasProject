// NasClientDesktop/Services/NasIo.cs
// NasLib（Lukas.Io）与 .NET Stream 世界的桥接。
// 所有本地文件 IO 都从这里经 NasLib 完成；HttpClient/HttpContent 需要 Stream，故在此适配。

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lukas.Interop.Unix.System.Native;
using Lukas.Std;

namespace NasClientDesktop.Services;

/// <summary>
/// 把一个用 NasLib 打开的只读文件包装成前向只读 <see cref="Stream"/>，
/// 供 <see cref="ProgressStreamContent"/> 流式上传。仅支持顺序读取（CanSeek=false）。
/// </summary>
public sealed class NasFileReadStream : Stream
{
    private readonly Io.File _file;
    private long _position;
    private bool _disposed;

    public NasFileReadStream(string path)
    {
        Length = Io.File.GetFileLength(path.AsSpan());
        _file = new Io.File();
        _file.Open(path.AsSpan(), Flags.Read);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) return 0;
        var n = _file.Read(buffer.AsSpan(offset, count));
        if (n > 0) _position += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return 0;
        var n = _file.Read(buffer);
        if (n > 0) _position += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing) _file.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>用 NasLib 把一个输入 <see cref="Stream"/>（如 HTTP 响应体）流式写到本地文件。</summary>
public static class NasFileWriter
{
    private const int BufferSize = 81920; // 80 KiB

    /// <summary>
    /// 从 <paramref name="source"/> 读取并经 NasLib 写入 <paramref name="targetPath"/>（Create 语义：存在即清空）。
    /// 先确保父目录存在。可回报进度（total 未知时传 -1）。
    /// </summary>
    public static async Task WriteFromStreamAsync(
        string targetPath,
        Stream source,
        long total,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        // 确保父目录存在（经 NasLib）。
        var parent = Io.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            try { Io.File.CreateDirectories(parent.AsSpan()); } catch { /* 已存在或无父目录 */ }
        }

        using var file = new Io.File();
        file.Open(targetPath.AsSpan(), Flags.Create);

        var buffer = new byte[BufferSize];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            file.Write(buffer.AsSpan(0, read));
            written += read;
            onProgress?.Invoke(written, total);
        }
    }
}

/// <summary>临时文件路径分配（用于预览下载）。临时文件位于应用数据目录下的 cache 子目录。</summary>
public static class TempFile
{
    private static readonly string CacheDir = ResolveCacheDir();

    private static string ResolveCacheDir()
    {
        var dir = Io.Path.Combine(AppConfig.DataDirectory, "cache");
        try { Io.File.CreateDirectories(dir.AsSpan()); } catch { /* 忽略 */ }
        return dir;
    }

    /// <summary>为给定原始文件名分配一个唯一的临时路径，保留扩展名以便系统按类型打开。</summary>
    public static string ForName(string originalName)
    {
        var ext = Io.Path.GetExtension(originalName); // 含点，可能为空
        var stamp = DateTime.UtcNow.Ticks.ToString("x") + "_" + Environment.ProcessId.ToString("x");
        var name = "preview_" + stamp + ext;
        return Io.Path.Combine(CacheDir, name);
    }

    /// <summary>尽力清理过期缓存（启动时调用）。失败忽略。</summary>
    public static void TryCleanup()
    {
        try
        {
            if (Io.Directory.Exists(CacheDir.AsSpan()))
                Io.Directory.Delete(CacheDir.AsSpan(), recursive: true);
            Io.File.CreateDirectories(CacheDir.AsSpan());
        }
        catch
        {
            // 忽略
        }
    }
}
