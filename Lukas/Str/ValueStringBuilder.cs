/*
 * 为支持 Str 中的 PathInternal，需引入并采用 .NET Runtime 源码
 * 见 https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/System/Text/ValueStringBuilder.cs
 * 本文件完全遵循 .NET Runtime 许可
 */

// Lukas/Str/ValueStringBuilder.cs

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lukas.Str;

/// <summary>
/// 面向高性能场景的字符串拼接器。它把内容直接写进调用方给的缓冲区（通常是
/// 栈上的 <c>stackalloc</c> 数组），空间不够时才从 <see cref="ArrayPool{T}"/>
/// 租用更大的数组，因此在常见情况下完全不产生堆分配。
///
/// 这是个 <c>ref struct</c>，只能在栈上使用，不能装箱、不能放进字段或异步状态机。
/// 用完后要么调用 <see cref="ToString"/>（内部会顺带归还租用的数组），要么显式
/// <see cref="Dispose"/>，否则租来的数组不会回到池里。
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public ref struct ValueStringBuilder
{
    // 仅当向池租过数组时才非空；释放时据此判断要不要归还。
    private char[]? _arrayToReturnToPool;
    private Span<char> _chars;
    private int _pos;

    /// <summary>用调用方提供的缓冲区初始化，起步阶段不涉及任何分配。</summary>
    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _arrayToReturnToPool = null;
        _chars = initialBuffer;
        _pos = 0;
    }

    /// <summary>按指定容量从数组池租一块缓冲区起步，适合事先能估出大致长度的场景。</summary>
    public ValueStringBuilder(int initialCapacity)
    {
        _arrayToReturnToPool = ArrayPool<char>.Shared.Rent(initialCapacity);
        _chars = _arrayToReturnToPool;
        _pos = 0;
    }

    /// <summary>当前已写入的字符数。赋值只移动写指针，不会清空或填充缓冲区。</summary>
    public int Length
    {
        get => _pos;
        set
        {
            Debug.Assert(value >= 0);
            Debug.Assert(value <= _chars.Length);
            _pos = value;
        }
    }

    public int Capacity => _chars.Length;

    /// <summary>确保总容量至少为 <paramref name="capacity"/>，不足则扩容。</summary>
    public void EnsureCapacity(int capacity)
    {
        Debug.Assert(capacity >= 0);
        
        if ((uint)capacity > (uint)_chars.Length)
            Grow(capacity - _pos);
    }
    
    /// <summary>
    /// 在当前内容末尾补一个 <c>'\0'</c>，但不计入 <see cref="Length"/>。
    /// 这样配合 <see cref="GetPinnableReference"/> 取指针后，就能当作 C 风格字符串传给原生接口。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NullTerminate()
    {
        EnsureCapacity(_pos + 1);
        _chars[_pos] = '\0';
    }

    /// <summary>暴露缓冲区首元素的引用，使本类型可直接用于 <c>fixed</c> 语句固定取指针。</summary>
    public ref char GetPinnableReference()
    {
        return ref MemoryMarshal.GetReference(_chars);
    }

    public ref char this[int index]
    {
        get
        {
            Debug.Assert(index < _pos);
            return ref _chars[index];
        }
    }
    
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => AsSpan().ToString();

    /// <summary>
    /// 物化为 <see cref="string"/>，<b>并在返回前自动 <see cref="Dispose"/></b>，归还租用的数组。
    /// 因此调用之后本实例就不应再使用。
    /// </summary>
    public override string ToString()
    {
        var s = _chars[.._pos].ToString();
        Dispose();
        return s;
    }

    /// <summary>整块底层缓冲区（含尚未写入的尾部空间），一般只在需要直接写入时用到。</summary>
    public Span<char> RawChars => _chars;

    /// <summary>当前已写入内容的只读视图，不触发分配。</summary>
    public ReadOnlySpan<char> AsSpan() => _chars[.._pos];
    public ReadOnlySpan<char> AsSpan(int start) => _chars.Slice(start, _pos - start);
    public ReadOnlySpan<char> AsSpan(int start, int length) => _chars.Slice(start, length);

    /// <summary>在 <paramref name="index"/> 处插入 <paramref name="count"/> 个字符 <paramref name="value"/>，原有内容整体后移。</summary>
    public void Insert(int index, char value, int count)
    {
        if (_pos > _chars.Length - count)
        {
            Grow(count);
        }

        var remaining = _pos - index;
        _chars.Slice(index, remaining).CopyTo(_chars[(index + count)..]);
        _chars.Slice(index, count).Fill(value);
        _pos += count;
    }

    public void Insert(int index, string? s)
    {
        if (s == null)
        {
            return;
        }

        var count = s.Length;

        if (_pos > (_chars.Length - count))
        {
            Grow(count);
        }

        var remaining = _pos - index;
        _chars.Slice(index, remaining).CopyTo(_chars[(index + count)..]);
        
        s.CopyTo(_chars[index..]);
        _pos += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        var pos = _pos;
        var chars = _chars;
        if ((uint)pos < (uint)chars.Length)
        {
            chars[pos] = c;
            _pos = pos + 1;
        }
        else
        {
            GrowAndAppend(c);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? s)
    {
        if (s == null)
        {
            return;
        }

        var pos = _pos;
        if (s.Length == 1 && (uint)pos < (uint)_chars.Length)
        {
            _chars[pos] = s[0];
            _pos = pos + 1;
        }
        else
        {
            AppendSlow(s);
        }
    }

    private void AppendSlow(string s)
    {
        var pos = _pos;
        if (pos > _chars.Length - s.Length)
        {
            Grow(s.Length);
        }
        
        s.CopyTo(_chars[pos..]);
        _pos += s.Length;
    }

    public void Append(char c, int count)
    {
        if (_pos > _chars.Length - count)
        {
            Grow(count);
        }

        var dst = _chars.Slice(_pos, count);
        for (var i = 0; i < dst.Length; i++)
        {
            dst[i] = c;
        }
        _pos += count;
    }

    public void Append(scoped ReadOnlySpan<char> value)
    {
        var pos = _pos;
        if (pos > _chars.Length - value.Length)
        {
            Grow(value.Length);
        }

        value.CopyTo(_chars[_pos..]);
        _pos += value.Length;
    }

    /// <summary>
    /// 预留 <paramref name="length"/> 个字符的空间并把写指针前移，返回这段可直接写入的区间。
    /// 适合「先拿到目标缓冲、再由外部填充」的场景，省去一次中间拷贝。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<char> AppendSpan(int length)
    {
        var origPos = _pos;
        if (origPos > _chars.Length - length)
        {
            Grow(length);
        }

        _pos = origPos + length;
        return _chars.Slice(origPos, length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndAppend(char c)
    {
        Grow(1);
        Append(c);
    }
    
    // 扩容：新容量取「当前长度 + 所需增量」与「旧容量翻倍」中的较大者，并受数组最大长度约束。
    // 旧内容拷到新数组后，把之前租用的数组（若有）归还池中。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additionalCapacityBeyondPos)
    {
        Debug.Assert(additionalCapacityBeyondPos > 0);
        Debug.Assert(_pos > _chars.Length - additionalCapacityBeyondPos, "Grow called incorrectly, no resize is needed.");

        const uint arrayMaxLength = 0x7FFFFFC7;
        
        var newCapacity = (int)Math.Max(
            (uint)(_pos + additionalCapacityBeyondPos),
            Math.Min((uint)_chars.Length * 2, arrayMaxLength));
        
        var poolArray = ArrayPool<char>.Shared.Rent(newCapacity);

        _chars[.._pos].CopyTo(poolArray);

        var toReturn = _arrayToReturnToPool;
        _chars = _arrayToReturnToPool = poolArray;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }

    /// <summary>把租用的数组归还池中并清空自身。可重复调用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        var toReturn = _arrayToReturnToPool;
        this = default;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }
}
