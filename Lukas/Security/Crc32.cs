// Lukas/Security/Crc32.cs
//
// 性能优化版（保持公共 API 与算法结果与原版完全一致）。
//
// 优化要点（对应清单条目）：
//   #11/#12 Unsafe.Add / MemoryMarshal：软件路径用 ref + Unsafe.Add 去除数组边界检查；
//           按 8 字节一组读取（MemoryMarshal.Read / ReadUnaligned）做 slice-by-8 查表。
//   #13     硬件内建：x86 SSE4.2 的 crc32 指令与 ARM 的 AdvSimd.Crc32 直接计算。
//           注意：CRC-32C(Castagnoli, SSE4.2) 与本算法 CRC-32(IEEE) 多项式不同，
//           因此 x86 不能直接用 Sse42 指令；这里对 ARM 走 AdvSimd.Arm.Crc32（IEEE 0x04C11DB7
//           的反射形式正是本算法），x86 与其它平台走高度优化的 slice-by-16 查表。
//   #15     IsSupported 运行期特性探测，安全回退。
//   #19/#22 [SkipLocalsInit] + AggressiveInlining。
//
// 结果与 zlib/gzip 的 CRC-32 一致。已对全部分支做交叉校验（见 AUDIT/）。

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Lukas.Security;

/// <summary>
/// 标准 CRC-32（IEEE 802.3，多项式 0xEDB88320，输入/输出反射，初值与终值取反）。
/// 与 zlib/gzip 的 CRC-32 一致，用于文件分块的完整性校验。
/// </summary>
/// <remarks>
/// <para>本实现按平台自动选择最快路径：</para>
/// <list type="bullet">
///   <item>ARM (AdvSimd.Crc32)：使用硬件 CRC 指令，单周期吞吐最高。</item>
///   <item>其它平台：slice-by-16 查表 + <see cref="Unsafe"/> 去边界检查的软件实现。</item>
/// </list>
/// </remarks>
public static class Crc32
{
    // 16 张 256 项查表，支撑 slice-by-16：一次推进 16 字节。
    // 列优先布局 Table[k*256 + b]，k 为该字节距离当前窗口尾部的“滞后步数”。
    private static readonly uint[] Table = BuildSliceTable();

    private const int SliceWidth = 16;

    private static uint[] BuildSliceTable()
    {
        const uint poly = 0xEDB88320u;
        var table = new uint[SliceWidth * 256];

        // 第 0 张：标准逐字节表。
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        // 其余各张由前一张推导：table[n][b] = table[0][ table[n-1][b] & 0xFF ] ^ (table[n-1][b] >> 8)。
        for (var b = 0; b < 256; b++)
        {
            var prev = table[b];
            for (var slice = 1; slice < SliceWidth; slice++)
            {
                prev = table[prev & 0xFF] ^ (prev >> 8);
                table[slice * 256 + b] = prev;
            }
        }

        return table;
    }

    /// <summary>计算一段数据的 CRC-32 校验值。</summary>
    [SkipLocalsInit]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        crc = ArmCrc32.IsSupported ? ComputeArm(crc, data) : ComputeSliceBy16(crc, data);

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// 增量计算入口：允许把多段数据接力求 CRC。传入上一次的 <paramref name="crc"/>
    /// （首段用 <c>0u</c>），全部喂完后调用 <see cref="Finalize(uint)"/> 取最终值。
    /// </summary>
    [SkipLocalsInit]
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var state = ~crc; // 解除上一次的终值取反，回到内部状态。
        state = ArmCrc32.IsSupported
            ? ComputeArm(state, data)
            : ComputeSliceBy16(state, data);
        return ~state;
    }

    /// <summary>与 <see cref="Append"/> 配套：此处仅为语义对称，终值取反已在 Append 内完成。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Finalize(uint crc) => crc;

    // ---- ARM 硬件路径 ----
    // AdvSimd.Crc32 系列指令实现的正是 IEEE 反射多项式，与本表算法等价。
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeArm(uint crc, ReadOnlySpan<byte> data)
    {
        ref var p = ref MemoryMarshal.GetReference(data);
        var len = data.Length;
        nuint i = 0;

        // 8 字节为单位推进。
        for (; i + 8 <= (nuint)len; i += 8)
        {
            var v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i));
            crc = ArmCrc32.Arm64.ComputeCrc32(crc, v);
        }

        if (i + 4 <= (nuint)len)
        {
            var v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i));
            crc = ArmCrc32.ComputeCrc32(crc, v);
            i += 4;
        }

        if (i + 2 <= (nuint)len)
        {
            var v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, i));
            crc = ArmCrc32.ComputeCrc32(crc, v);
            i += 2;
        }

        if (i < (nuint)len)
            crc = ArmCrc32.ComputeCrc32(crc, Unsafe.Add(ref p, i));

        return crc;
    }

    // ---- 软件 slice-by-16 路径 ----
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeSliceBy16(uint crc, ReadOnlySpan<byte> data)
    {
        ref var p = ref MemoryMarshal.GetReference(data);
        ref var t = ref MemoryMarshal.GetArrayDataReference(Table);
        var len = data.Length;
        nuint i = 0;

        // 主循环：每次吞 16 字节。
        while (i + SliceWidth <= (nuint)len)
        {
            var lo = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i)) ^ crc;
            var hi = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i + 4));
            var w2 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i + 8));
            var w3 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i + 12));

            crc = T(ref t, 15, (byte)lo)
                ^ T(ref t, 14, (byte)(lo >> 8))
                ^ T(ref t, 13, (byte)(lo >> 16))
                ^ T(ref t, 12, (byte)(lo >> 24))
                ^ T(ref t, 11, (byte)hi)
                ^ T(ref t, 10, (byte)(hi >> 8))
                ^ T(ref t, 9, (byte)(hi >> 16))
                ^ T(ref t, 8, (byte)(hi >> 24))
                ^ T(ref t, 7, (byte)w2)
                ^ T(ref t, 6, (byte)(w2 >> 8))
                ^ T(ref t, 5, (byte)(w2 >> 16))
                ^ T(ref t, 4, (byte)(w2 >> 24))
                ^ T(ref t, 3, (byte)w3)
                ^ T(ref t, 2, (byte)(w3 >> 8))
                ^ T(ref t, 1, (byte)(w3 >> 16))
                ^ T(ref t, 0, (byte)(w3 >> 24));

            i += SliceWidth;
        }

        // 收尾：逐字节，复用第 0 张表。
        for (; i < (nuint)len; i++)
            crc = Unsafe.Add(ref t, (crc ^ Unsafe.Add(ref p, i)) & 0xFF) ^ (crc >> 8);

        return crc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint T(ref uint table, int slice, byte b)
        => Unsafe.Add(ref table, (nuint)(slice * 256) + b);
}
