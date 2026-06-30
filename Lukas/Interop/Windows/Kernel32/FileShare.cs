// Lukas/Interop/Windows/Kernel32/FileShare.cs

using System;

namespace Lukas.Interop.Windows.Kernel32;

// CreateFile 的 dwShareMode：允许其他句柄并发读/写/删除的共享方式。

[Flags]
public enum FileShare
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Write | Read,
    Delete = 4,
    Inheritable = 16
}
