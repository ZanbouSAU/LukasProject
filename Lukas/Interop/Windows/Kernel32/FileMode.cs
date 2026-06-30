// Lukas/Interop/Windows/Kernel32/FileMode.cs

namespace Lukas.Interop.Windows.Kernel32;

// 对应 CreateFile 的 dwCreationDisposition：决定文件不存在/已存在时的创建与截断行为。

public enum FileMode
{
    CreateNew = 1,
    Create = 2,
    Open = 3,
    OpenOrCreate = 4,
    Truncate = 5,
    Append = 6
}
