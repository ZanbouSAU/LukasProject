// Lukas/Io.cs

using System;
using Lukas.Interop.Unix.System.Native;

namespace Lukas.Std;

/// <summary>
/// 库的统一 I/O 门面。它把标准输入输出（<see cref="Stdin"/>/<see cref="Stdout"/>/<see cref="Stderr"/>）、
/// 同步文件 <see cref="File"/>、异步文件 <see cref="FileAsync"/> 以及底层 PAL 等汇聚到一处，
/// 整个类型按 <c>partial</c> 拆分到多个文件实现。
///
/// 进程退出时会自动冲刷标准输出缓冲，避免缓存内容丢失。
/// </summary>
public static partial class Io
{
    static Io()
    {
        // 注册进程退出回调，确保退出前把 stdout 缓冲写出去。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushOut();
    }
    
    /// <summary>向标准输出写入一段字节，不追加换行。</summary>
    /// <param name="value"></param>
    public static void Print(ReadOnlySpan<byte> value)
        => Stdout.Write(value);
    
    /// <summary>向标准输出写入一段字节并换行。</summary>
    public static void Println(ReadOnlySpan<byte> value)
        => Stdout.Write(value, true);
    
    /// <summary>向标准输出写入一段字符并换行。</summary>
    public static void Println(ReadOnlySpan<char> value)
        => Stdout.Write(value, true);
    
    /// <summary>格式化并写入一个可格式化为 UTF-8 的值并换行；<see langword="null"/> 时只输出空行。</summary>
    public static void Println<T>(T? value) where T : IUtf8SpanFormattable
    {
        if (value is null)
        {
            Println();
            return;
        }
        
        Stdout.Write(value, true);
    }

    public static void Println(object? value)
    {
        if (value is null)
        {
            Println();
            return;
        }
        
        Stdout.Write(value, true);
    }
    
    public static void Println(string? value)
    {
        if (value is null)
        {
            Println();
            return;
        }
        
        Stdout.Write(value, true);
    }
    
    /// <summary>仅输出一个换行。</summary>
    public static void Println()
        => Stdout.WriteLine();
    
    /// <summary>新建（或清空）一个文件后立即关闭，相当于「创建空文件」。</summary>
    public static void Create(string path)
    {
        File file = new();
        file.Open(path, Flags.Create);
        file.Dispose();
    }
        
    /// <summary>确保文件存在：存在则保留，不存在则创建，随后立即关闭。</summary>
    public static void OpenOrCreate(string path)
    {
        File file = new();
        file.Open(path, Flags.OpenOrCreate);
        file.Dispose();
    }
        
    /// <summary>把内容作为一行写入文件（存在则覆盖创建）。</summary>
    public static void WriteLineAllBytes(string path, string contents)
    {
        File file = new();
        file.Open(path, Flags.OpenOrCreate);
        file.Write(contents, isLine: true);
        file.Dispose();
    }
        
    /// <summary>以追加方式把内容作为一行写入文件末尾。</summary>
    public static void WriteLineAppendAllBytes(string path, string contents)
    {
        File file = new();
        file.Open(path);
        file.Write(contents, isLine: true);
        file.Dispose();
    }

    /// <summary>冲刷标准输出缓冲。</summary>
    public static void FlushOut()
        => Stdout.Flush();
}
