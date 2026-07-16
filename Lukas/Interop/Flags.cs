// Lukas/Interop/Flags.cs

namespace Lukas.Interop;

/// <summary>文件打开/创建语义，含义对齐常见的文件打开模式。</summary>
public enum Flags
{
    /// <summary>新建文件，若已存在则失败。</summary>
    CreateNew = 0x0000,
    /// <summary>新建文件，若已存在则清空。</summary>
    Create = 0x0001,
    /// <summary>打开已有文件，不存在则失败。</summary>
    Open = 0x0002,
    /// <summary>存在则打开，不存在则创建。</summary>
    OpenOrCreate = 0x0003,
    /// <summary>打开并清空已有文件。</summary>
    Truncate = 0x0004,
    /// <summary>追加写入，必要时创建。</summary>
    Append = 0x0005,
    /// <summary>只读打开。</summary>
    Read = 0x0006
}
