// Lukas/Str/Utf8StringBuilder.cs

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace Lukas.Str;

/// <summary>
/// <see cref="Utf8StringBuilder"/> 的插值字符串处理器，让它能直接参与 <c>$"..."</c> 语法。
/// 字面量和被插值的值都会原地编码进目标构建器，中途不产生临时 <see cref="string"/>。
/// </summary>
[InterpolatedStringHandler]
public readonly unsafe ref struct Utf8InterpolatedStringHandler
{
    // 每个待格式化值预留的初步空间；不够时再按需扩容。
    private const int MinGrowChunk = 64;
    
    private readonly Utf8StringBuilder* _sb;

    public Utf8InterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        scoped ref Utf8StringBuilder sb)
    {
        _sb = (Utf8StringBuilder*)Unsafe.AsPointer(ref sb);
        _sb->EnsureFreeSpace(literalLength * 3 + formattedCount * 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(string value) => _sb->Append(value.AsSpan());
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(scoped ReadOnlySpan<char> value) => _sb->Append(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(scoped ReadOnlySpan<byte> utf8) => _sb->Append(utf8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(scoped ReadOnlySpan<char> chars) => _sb->Append(chars);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(string? value)
    {
        if (value is not null)
            _sb->Append(value.AsSpan());
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value) where T : IUtf8SpanFormattable
    {
        int written;
        while (!value.TryFormat(_sb->FreeSpan, out written, default, provider: null))
        {
            _sb->Grow(Math.Max(MinGrowChunk, _sb->Capacity));
        }
        _sb->Advance(written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, scoped ReadOnlySpan<char> format)
        where T : IUtf8SpanFormattable
    {
        int written;
        while (!value.TryFormat(_sb->FreeSpan, out written, format, provider: null))
        {
            _sb->Grow(Math.Max(MinGrowChunk, _sb->Capacity));
        }
        _sb->Advance(written);
    }
    
    public void AppendFormatted(object? value)
    {
        var s = value?.ToString();
        if (s != null)
            _sb->Append(s.AsSpan());
    }
}

/// <summary>
/// 直接以 UTF-8 字节累积文本的高性能构建器，是 <see cref="ValueStringBuilder"/> 的字节版。
///
/// 它分三级管理底层存储：起步时用构造函数申请的原生内存（或调用方给的池数组），
/// 扩容后若容量不超过 1 MiB 就改用 <see cref="ArrayPool{T}"/>，再大则回落到原生堆。
/// 这样既避免了大对象堆（LOH）压力，又能让常见的小串走零 GC 路径。
///
/// 写入 <see cref="char"/>/<see cref="string"/> 时会即时转成 UTF-8。作为 <c>ref struct</c>
/// 只能在栈上使用，用完务必 <see cref="Dispose"/> 以释放原生内存或归还池数组。
/// </summary>
public ref struct Utf8StringBuilder : IDisposable
{
    // 扩容时小于等于该阈值用池数组，超过则用原生内存。
    private const int StackThreshold = 4 * 1024;
    private const int PoolThreshold  = 1 * 1024 * 1024;
    
    private const int MinGrowChunk = 64;
    
    // 当前有效缓冲区（可能指向原生内存或池数组）。
    private Span<byte> _buffer;
    
    // 仅当底层是池数组时非空，释放时据此归还。
    private byte[]? _pooled;
    
    // 仅当底层是原生内存时非空，释放时据此 free。
    private unsafe byte* _native;
    private int _nativeCapacity;

    /// <summary>用原生内存起步。<paramref name="initializer"/> 可指定初始容量，默认 4 KiB。</summary>
    public Utf8StringBuilder(Utf8StringBuilderInitializer initializer = default)
    {
        const int defaultInitialCapacity = 4096;

        unsafe
        {
            var size = initializer.StackSize == 0 ? defaultInitialCapacity : initializer.StackSize;
            var ptr = (byte*)NativeMemory.Alloc((nuint)size);
            _native = ptr;
            _nativeCapacity = size;
            _buffer = new Span<byte>(ptr, size);
        }
        _pooled = null;
        Length = 0;
        IsDisposed = false;
    }
    
    /// <summary>用 <see cref="ArrayPool{T}"/> 数组起步的工厂方法，适合生命周期短、用完即弃的拼接。</summary>
    public static Utf8StringBuilder CreatePooled(int initialCapacity = StackThreshold)
    {
        var cap = Math.Max(initialCapacity, MinGrowChunk);
        var pooled = ArrayPool<byte>.Shared.Rent(cap);

        var sb = default(Utf8StringBuilder);
        sb._pooled = pooled;
        sb._buffer = pooled.AsSpan();
        unsafe
        {
            sb._native = null;
        }
        sb._nativeCapacity = 0;
        return sb;
    }

    private int Length { get; set; }

    public readonly int Capacity => _buffer.Length;

    private bool IsDisposed { get; set; }
    
    /// <summary>已写入内容的只读视图。</summary>
    public readonly ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ThrowIfDisposed();
            return _buffer[..Length];
        }
    }
    
    /// <summary>缓冲区中尚未写入的剩余区间，供 <c>TryFormat</c> 之类的接口直接写入。</summary>
    public Span<byte> FreeSpan
    {
        get
        {
            ThrowIfDisposed();
            return _buffer[Length..];
        }
    }
    
    /// <summary>追加一段已经是 UTF-8 的字节，不做任何转码。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(scoped ReadOnlySpan<byte> utf8)
    {
        ThrowIfDisposed();
        if (utf8.Length > _buffer.Length - Length)
            Grow(utf8.Length);
        utf8.CopyTo(_buffer[Length..]);
        Length += utf8.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(byte b)
    {
        ThrowIfDisposed();
        if (Length == _buffer.Length)
            Grow(1);
        _buffer[Length++] = b;
    }
    
    public void Append(string? s)
    {
        if (s is null)
            return;
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
                _buffer[Length..],
                out var charsRead,
                out var bytesWritten,
                replaceInvalidSequences: true,
                isFinalBlock: true);
            
            Length += bytesWritten;
            remaining = remaining[charsRead..];

            switch (status)
            {
                case OperationStatus.Done:
                    return;
                case OperationStatus.DestinationTooSmall:
                {
                    var need = Encoding.UTF8.GetMaxByteCount(remaining.Length);
                    Grow(Math.Max(MinGrowChunk, need));
                    continue;
                }
                case OperationStatus.NeedMoreData:
                case OperationStatus.InvalidData:
                default:
                    ThrowInvalidUtf16();
                    return;
            }
        }
    }
    
    public void AppendFormatted<T>(
        T value,
        scoped ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
        where T : IUtf8SpanFormattable
    {
        ThrowIfDisposed();
        int written;
        while (!value.TryFormat(_buffer[Length..], out written, format, provider))
        {
            Grow(Math.Max(MinGrowChunk, _buffer.Length));
        }
        Length += written;
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
            if (_buffer.Length - Length < 2)
                Grow(2);
            _buffer[Length] = (byte)(0xC0 | (c >> 6));
            _buffer[Length + 1] = (byte)(0x80 | (c & 0x3F));
            Length += 2;
            return;
        }
        
        if (c is >= '\uD800' and <= '\uDFFF')
        {
            var surrogate = new ReadOnlySpan<char>(in c);
            Append(surrogate);
            return;
        }
        
        if (_buffer.Length - Length < 3)
            Grow(3);
        _buffer[Length] = (byte)(0xE0 | (c >> 12));
        _buffer[Length + 1] = (byte)(0x80 | ((c >> 6) & 0x3F));
        _buffer[Length + 2] = (byte)(0x80 | (c & 0x3F));
        Length += 3;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLine() => Append((byte)'\n');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLine(scoped ReadOnlySpan<byte> utf8)
    {
        Append(utf8);
        Append((byte)'\n');
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureFreeSpace(int additionalBytes)
    {
        ThrowIfDisposed();
        if ((uint)additionalBytes > (uint)(_buffer.Length - Length))
            Grow(additionalBytes);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int totalCapacity)
    {
        ThrowIfDisposed();
        if (totalCapacity > _buffer.Length)
            Grow(totalCapacity - Length);
    }

    /// <summary>在外部已直接写入 <see cref="FreeSpan"/> 后，手动把写指针前移 <paramref name="count"/> 字节。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        Debug.Assert(count >= 0);
        Debug.Assert(Length + count <= _buffer.Length);
        Length += count;
    }
    
    /// <summary>
    /// 扩容到至少能再容纳 <paramref name="additional"/> 字节。新容量在「翻倍」和「实际所需」间取大者，
    /// 并据 <see cref="PoolThreshold"/> 决定落到池数组还是原生内存。
    /// </summary>
    public void Grow(int additional)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additional);

        var required = checked(Length + additional);
        
        var doubled = _buffer.Length * 2L;
        var target = Math.Max(doubled, required);
        if (target < MinGrowChunk)
            target = MinGrowChunk;
        
        const int maxCapacity = 0x7FFFFFC7;
        if (target > maxCapacity)
        {
            if (required > maxCapacity)
                throw new OutOfMemoryException("Utf8StringBuilder capacity exceeded.");
            target = maxCapacity;
        }

        var newCapacity = (int)target;

        if (newCapacity <= PoolThreshold)
        {
            GrowToPool(newCapacity);
        }
        else
        {
            GrowToNative(newCapacity);
        }
    }

    private void GrowToPool(int newCapacity)
    {
        var newArr = ArrayPool<byte>.Shared.Rent(newCapacity);
        if (Length > 0)
        {
            _buffer[..Length].CopyTo(newArr);
        }
        ReleaseOldBuffer();
        _pooled = newArr;
        _buffer = newArr;
    }

    private void GrowToNative(int newCapacity)
    {
        unsafe
        {
            var newPtr = (byte*)NativeMemory.Alloc((nuint)newCapacity);
            if (Length > 0)
            {
                ref var srcRef = ref MemoryMarshal.GetReference(_buffer);
                fixed (byte* src = &srcRef)
                {
                    Buffer.MemoryCopy(src, newPtr, newCapacity, Length);
                }
            }
            ReleaseOldBuffer();
            _native = newPtr;
            _nativeCapacity = newCapacity;
            _buffer = new Span<byte>(newPtr, newCapacity);
        }
    }

    private void ReleaseOldBuffer()
    {
        if (_pooled is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooled);
            _pooled = null;
        }
        unsafe
        {
            if (_native is not null)
            {
                NativeMemory.Free(_native);
                _native = null;
                _nativeCapacity = 0;
            }
        }
    }
    
    /// <summary>把已写入内容复制成一个新的 <see cref="byte"/> 数组。</summary>
    public readonly byte[] ToArray()
    {
        ThrowIfDisposed();
        return _buffer[..Length].ToArray();
    }
    
    public readonly bool TryCopyTo(scoped Span<byte> destination, out int bytesWritten)
    {
        ThrowIfDisposed();
        if (Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }
        _buffer[..Length].CopyTo(destination);
        bytesWritten = Length;
        return true;
    }
    
    /// <summary>把已写入的 UTF-8 内容解码为 <see cref="string"/>。</summary>
    public readonly override string ToString()
    {
        ThrowIfDisposed();
        return Encoding.UTF8.GetString(_buffer[..Length]);
    }
    
    /// <summary>把已写入内容写到 <paramref name="writer"/>，不产生中间拷贝。</summary>
    public readonly void WriteTo(IBufferWriter<byte> writer)
    {
        ThrowIfDisposed();
        writer.Write(_buffer[..Length]);
    }
    
    /// <summary>清空内容（写指针归零），但保留已分配的缓冲区以便复用。</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        Length = 0;
    }

    /// <summary>释放原生内存或归还池数组。可重复调用。</summary>
    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        ReleaseOldBuffer();
        _buffer = default;
        Length = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void ThrowIfDisposed()
    {
        if (IsDisposed)
            ThrowObjectDisposed();
    }

    [DoesNotReturn]
    private static void ThrowObjectDisposed() =>
        throw new ObjectDisposedException(nameof(Utf8StringBuilder));

    [DoesNotReturn]
    private static void ThrowInvalidUtf16() =>
        throw new InvalidOperationException("Invalid UTF-16 sequence in input.");
}

/// <summary>承载 <see cref="Utf8StringBuilder"/> 的初始容量设置，可由一个 <see cref="int"/> 隐式转换得到。</summary>
public readonly struct Utf8StringBuilderInitializer(int stackSize)
{
    public readonly int StackSize = stackSize;

    public static implicit operator Utf8StringBuilderInitializer(int size) 
        => new(size);
}

/// <summary>为 <see cref="Utf8StringBuilder"/> 提供插值拼接入口的扩展方法。</summary>
public static class Utf8StringBuilderExtensions
{
    /// <summary>
    /// 以插值语法向构建器追加内容，例如 <c>sb.AppendInterpolated($"{name}={value}")</c>。
    /// 实际写入由 <see cref="Utf8InterpolatedStringHandler"/> 在编译期展开完成，这里无需额外逻辑。
    /// </summary>
    public static void AppendInterpolated(
        this ref Utf8StringBuilder sb,
        [InterpolatedStringHandlerArgument("sb")] scoped Utf8InterpolatedStringHandler handler)
    {
        _ = handler;
    }
}
