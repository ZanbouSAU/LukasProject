// NasClientDesktop/Services/ProgressStreamContent.cs
// 上传进度：HttpClient 本身不暴露上行进度，这里用自定义 HttpContent 在 SerializeToStream 时
// 逐块写出并回报已发送字节数，等价于前端用 XMLHttpRequest.upload.onprogress 的效果。

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NasClientDesktop.Services;

public sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _length;
    private readonly Action<long, long> _onProgress;
    private readonly CancellationToken _ct;
    private const int BufferSize = 81920; // 80 KiB

    public ProgressStreamContent(Stream source, long length, string contentType, Action<long, long> onProgress, CancellationToken ct)
    {
        _source = source;
        _length = length;
        _onProgress = onProgress;
        _ct = ct;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        Headers.ContentLength = length;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[BufferSize];
        long sent = 0;
        int read;
        while ((read = await _source.ReadAsync(buffer.AsMemory(0, BufferSize), _ct).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), _ct).ConfigureAwait(false);
            sent += read;
            _onProgress(sent, _length);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}
