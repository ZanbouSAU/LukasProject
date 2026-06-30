// Lukas/Str/Utf8AsyncStringBuilder.cs

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace Lukas.Str;

/// <summary>
/// <see cref="Utf8AsyncStringBuilder"/> 的插值字符串处理器，让它能直接参与 <c>$"..."</c> 语法。
/// 字面量与被插值的值都原地编码进目标构建器，中途不产生临时 <see cref="string"/>。
///
/// 与 <see cref="Utf8StringBuilder"/> 的处理器不同，这里目标是一个引用类型（堆对象），
/// 因此直接持有其引用即可，无需 <c>unsafe</c> 指针。
/// </summary>
[InterpolatedStringHandler]
public readonly ref struct Utf8AsyncInterpolatedStringHandler
{
    private const int MinGrowChunk = 64;

    private readonly Utf8AsyncStringBuilder _sb;

    public Utf8AsyncInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        Utf8AsyncStringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        _sb = sb;
        _sb.EnsureFreeSpace(literalLength * 3 + formattedCount * 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(string value) => _sb.Append(value.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(scoped ReadOnlySpan<char> value) => _sb.Append(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(scoped ReadOnlySpan<byte> utf8) => _sb.Append(utf8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(scoped ReadOnlySpan<char> chars) => _sb.Append(chars);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(string? value)
    {
        if (value is not null) _sb.Append(value.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value) where T : IUtf8SpanFormattable
    {
        int written;
        while (!value.TryFormat(_sb.FreeSpan, out written, default, provider: null))
            _sb.Grow(Math.Max(MinGrowChunk, _sb.Capacity));
        _sb.Advance(written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, scoped ReadOnlySpan<char> format)
        where T : IUtf8SpanFormattable
    {
        int written;
        while (!value.TryFormat(_sb.FreeSpan, out written, format, provider: null))
            _sb.Grow(Math.Max(MinGrowChunk, _sb.Capacity));
        _sb.Advance(written);
    }

    public void AppendFormatted(object? value)
    {
        var s = value?.ToString();
        if (s is not null) _sb.Append(s.AsSpan());
    }
}

/// <summary>
/// 以 UTF-8 字节累积文本的高性能构建器，是 <see cref="Utf8StringBuilder"/> 的「可跨 <c>await</c>」版本。
///
/// 与作为 <c>ref struct</c> 的 <see cref="Utf8StringBuilder"/> 不同，本类型是托管堆对象，
/// 因此可以作为字段、在 <c>async</c> 方法里跨 <c>await</c> 存活，从而提供异步落盘能力
/// （<see cref="WriteToAsync"/> / <see cref="FlushToAsync"/> / <see cref="DrainToAsync"/>）。
///
/// 底层存储统一使用 <see cref="ArrayPool{T}"/> 租借的 <see cref="byte"/> 数组，扩容时翻倍并归还旧数组，
/// 让常见拼接走低分配路径。写入 <see cref="char"/>/<see cref="string"/> 时即时转成 UTF-8。
///
/// 线程不安全：与其它构建器一致，假定单线程（单写者）使用；请勿在并发线程间共享同一实例。
/// 用完需 <see cref="Dispose"/>（或 <c>await using</c>）以归还池数组。
/// </summary>
public sealed class Utf8AsyncStringBuilder : IDisposable, IAsyncDisposable
{
    private const int MinGrowChunk = 64;
    private const int DefaultInitialCapacity = 4096;

    // 数组可达到的最大长度（与运行时一致，避免请求过大长度抛错）。
    private const int MaxCapacity = 0x7FFFFFC7;

    // 当前缓冲区，始终来自 ArrayPool 租借（释放后置为空数组）。
    private byte[] _buffer;

    // 已写入的字节数（逻辑长度），始终以此为准，不依赖 _buffer.Length。
    private int _length;

    // 归还池数组时是否清零（处理敏感文本时可开启，代价是一次清零）。
    private readonly bool _clearOnReturn;

    private bool _disposed;

    /// <summary>以指定初始容量起步；底层数组从共享池租借。</summary>
    /// <param name="initialCapacity">初始容量下限（实际可能更大）。</param>
    /// <param name="clearBufferOnReturn">归还/释放时是否清零缓冲，默认 <c>false</c>。</param>
    public Utf8AsyncStringBuilder(int initialCapacity = DefaultInitialCapacity, bool clearBufferOnReturn = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);

        var cap = Math.Max(initialCapacity, MinGrowChunk);
        _buffer = ArrayPool<byte>.Shared.Rent(cap);
        _length = 0;
        _clearOnReturn = clearBufferOnReturn;
        _disposed = false;
    }

    /// <summary>已写入的字节数。</summary>
    public int Length => _length;

    /// <summary>当前缓冲区容量（实际数组长度，可能大于初始请求）。</summary>
    public int Capacity => _buffer.Length;

    /// <summary>已写入内容的只读视图；在下一次扩容/释放前有效。</summary>
    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ThrowIfDisposed();
            return _buffer.AsSpan(0, _length);
        }
    }

    /// <summary>已写入内容的只读内存视图；在下一次扩容/释放前有效，供异步写出直接使用。</summary>
    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            ThrowIfDisposed();
            return _buffer.AsMemory(0, _length);
        }
    }

    /// <summary>缓冲区中尚未写入的剩余区间，供 <c>TryFormat</c> 等接口直接写入；在下一次扩容前有效。</summary>
    public Span<byte> FreeSpan
    {
        get
        {
            ThrowIfDisposed();
            return _buffer.AsSpan(_length);
        }
    }

    // ---------------- 追加：字节 ----------------

    /// <summary>追加一段已经是 UTF-8 的字节，不做任何转码。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(scoped ReadOnlySpan<byte> utf8)
    {
        ThrowIfDisposed();
        if (utf8.Length > _buffer.Length - _length)
            Grow(utf8.Length);
        utf8.CopyTo(_buffer.AsSpan(_length));
        _length += utf8.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(byte b)
    {
        ThrowIfDisposed();
        if (_length == _buffer.Length) Grow(1);
        _buffer[_length++] = b;
    }

    // ---------------- 追加：字符串 / 字符 ----------------

    public void Append(string? s)
    {
        if (s is null) return;
        Append(s.AsSpan());
    }

    /// <summary>追加一段 UTF-16 字符并即时转成 UTF-8；非法码元会替换为 U+FFFD。</summary>
    public void Append(scoped ReadOnlySpan<char> chars)
    {
        ThrowIfDisposed();
        if (chars.IsEmpty)
            return;

        var remaining = chars;
        while (true)
        {
            // 一轮转不完（目标空间不够）就扩容后继续，直到全部写入。
            var status = Utf8.FromUtf16(
                remaining,
                _buffer.AsSpan(_length),
                out var charsRead,
                out var bytesWritten,
                replaceInvalidSequences: true,
                isFinalBlock: true);

            _length += bytesWritten;
            remaining = remaining[charsRead..];

            if (status == OperationStatus.Done)
                return;

            if (status == OperationStatus.DestinationTooSmall)
            {
                var need = Encoding.UTF8.GetMaxByteCount(remaining.Length);
                Grow(Math.Max(MinGrowChunk, need));
                continue;
            }

            ThrowInvalidUtf16();
            return;
        }
    }

    /// <summary>追加单个字符。按码点大小手写 1~3 字节的 UTF-8；落在代理区的字符走通用转码路径。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        if (c < 0x80)
        {
            Append((byte)c);
            return;
        }

        ThrowIfDisposed();

        if (c < 0x800)
        {
            if (_buffer.Length - _length < 2)
                Grow(2);
            _buffer[_length] = (byte)(0xC0 | (c >> 6));
            _buffer[_length + 1] = (byte)(0x80 | (c & 0x3F));
            _length += 2;
            return;
        }

        if (c is >= '\uD800' and <= '\uDFFF')
        {
            var surrogate = new ReadOnlySpan<char>(in c);
            Append(surrogate);
            return;
        }

        if (_buffer.Length - _length < 3)
            Grow(3);
        _buffer[_length] = (byte)(0xE0 | (c >> 12));
        _buffer[_length + 1] = (byte)(0x80 | ((c >> 6) & 0x3F));
        _buffer[_length + 2] = (byte)(0x80 | (c & 0x3F));
        _length += 3;
    }

    // ---------------- 追加：格式化值 / 行 ----------------

    public void AppendFormatted<T>(
        T value,
        scoped ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
        where T : IUtf8SpanFormattable
    {
        ThrowIfDisposed();
        int written;
        while (!value.TryFormat(_buffer.AsSpan(_length), out written, format, provider))
            Grow(Math.Max(MinGrowChunk, _buffer.Length));
        _length += written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLine() => Append((byte)'\n');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLine(scoped ReadOnlySpan<byte> utf8)
    {
        Append(utf8);
        Append((byte)'\n');
    }

    // ---------------- 容量管理 ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureFreeSpace(int additionalBytes)
    {
        ThrowIfDisposed();
        if (additionalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalBytes));
        if ((uint)additionalBytes > (uint)(_buffer.Length - _length))
            Grow(additionalBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int totalCapacity)
    {
        ThrowIfDisposed();
        if (totalCapacity > _buffer.Length)
            Grow(totalCapacity - _length);
    }

    /// <summary>在外部已直接写入 <see cref="FreeSpan"/> 后，手动把写指针前移 <paramref name="count"/> 字节。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count > _buffer.Length - _length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _length += count;
    }

    /// <summary>
    /// 扩容到至少能再容纳 <paramref name="additional"/> 字节。新容量在「翻倍」与「实际所需」间取大者，
    /// 始终从 <see cref="ArrayPool{T}"/> 租借并归还旧数组。
    /// </summary>
    public void Grow(int additional)
    {
        ThrowIfDisposed();
        if (additional < 0)
            throw new ArgumentOutOfRangeException(nameof(additional));

        var required = checked(_length + additional);

        var doubled = _buffer.Length * 2L;
        var target = Math.Max(doubled, required);
        if (target < MinGrowChunk) target = MinGrowChunk;

        if (target > MaxCapacity)
        {
            if (required > MaxCapacity)
                throw new OutOfMemoryException("Utf8AsyncStringBuilder capacity exceeded.");
            target = MaxCapacity;
        }

        var newCapacity = (int)target;
        var newArr = ArrayPool<byte>.Shared.Rent(newCapacity);

        if (_length > 0)
            _buffer.AsSpan(0, _length).CopyTo(newArr);

        ReturnBuffer();
        _buffer = newArr;
    }

    // ---------------- 取出内容 ----------------

    /// <summary>把已写入内容复制成一个新的 <see cref="byte"/> 数组。</summary>
    public byte[] ToArray()
    {
        ThrowIfDisposed();
        return _buffer.AsSpan(0, _length).ToArray();
    }

    public bool TryCopyTo(scoped Span<byte> destination, out int bytesWritten)
    {
        ThrowIfDisposed();
        if (_length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }
        _buffer.AsSpan(0, _length).CopyTo(destination);
        bytesWritten = _length;
        return true;
    }

    /// <summary>把已写入的 UTF-8 内容解码为 <see cref="string"/>。</summary>
    public override string ToString()
    {
        ThrowIfDisposed();
        return Encoding.UTF8.GetString(_buffer.AsSpan(0, _length));
    }

    /// <summary>把已写入内容同步写到 <paramref name="writer"/>，不产生中间拷贝。</summary>
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(_buffer.AsSpan(0, _length));
    }

    // ---------------- 异步写出 ----------------

    /// <summary>异步把已写入内容写到 <paramref name="stream"/>；不清空内容。</summary>
    public async ValueTask WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        if (_length == 0)
            return;
        await stream.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>异步把已写入内容写到 <paramref name="stream"/>，成功后清空（保留缓冲以复用）。</summary>
    public async ValueTask FlushToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        Clear();
    }

    /// <summary>
    /// 异步把已写入内容交给自定义异步落地回调 <paramref name="sink"/>（例如写入某个 fd / 套接字 / 引擎），
    /// 成功后清空。<paramref name="sink"/> 收到的是当前内容的只读内存视图，调用期间内容不会被改动。
    /// </summary>
    public async ValueTask DrainToAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sink);
        if (_length > 0)
            await sink(_buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
        Clear();
    }

    // ---------------- 清空 / 释放 ----------------

    /// <summary>清空内容（写指针归零），保留已分配的缓冲区以便复用。</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        if (_clearOnReturn && _length > 0)
            _buffer.AsSpan(0, _length).Clear();
        _length = 0;
    }

    /// <summary>归还池数组。可重复调用。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReturnBuffer();
        _length = 0;
    }

    /// <summary>与 <see cref="Dispose"/> 等价，便于 <c>await using</c>；本类型释放本身无异步工作。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ---------------- 内部 ----------------

    // 归还当前缓冲到共享池，并置为空数组以防误用已回收的数组。
    private void ReturnBuffer()
    {
        var toReturn = _buffer;
        _buffer = [];
        if (toReturn.Length != 0)
            ArrayPool<byte>.Shared.Return(toReturn, clearArray: _clearOnReturn);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) ThrowObjectDisposed();
    }

    [DoesNotReturn]
    private static void ThrowObjectDisposed() =>
        throw new ObjectDisposedException(nameof(Utf8AsyncStringBuilder));

    [DoesNotReturn]
    private static void ThrowInvalidUtf16() =>
        throw new InvalidOperationException("Invalid UTF-16 sequence in input.");
}

/// <summary>为 <see cref="Utf8AsyncStringBuilder"/> 提供插值拼接入口的扩展方法。</summary>
public static class Utf8AsyncStringBuilderExtensions
{
    /// <summary>
    /// 以插值语法向构建器追加内容，例如 <c>sb.AppendInterpolated($"{name}={value}")</c>。
    /// 实际写入由 <see cref="Utf8AsyncInterpolatedStringHandler"/> 在编译期展开完成，这里无需额外逻辑。
    /// </summary>
    public static void AppendInterpolated(
        this Utf8AsyncStringBuilder sb,
        [InterpolatedStringHandlerArgument("sb")] scoped Utf8AsyncInterpolatedStringHandler handler)
    {
        _ = handler;
    }
}
