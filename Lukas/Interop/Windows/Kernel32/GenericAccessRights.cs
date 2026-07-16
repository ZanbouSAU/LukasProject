// Lukas/Interop/Windows/Kernel32/GenericAccessRights.cs

namespace Lukas.Interop.Windows.Kernel32;

// CreateFile 的 dwDesiredAccess：通用访问权限（读/写/执行/全部），以及更细的文件级数据权限位。

public static class GenericAccessRights
{
    internal const int GenericRead = unchecked((int)0x80000000);
    internal const int GenericWrite = 0x40000000;
    internal const int GenericExecute = 0x20000000;
    internal const int GenericAll = 0x10000000;
    
    internal const int FileReadData = 0x0001;
    internal const int FileWriteData = 0x0002;
    internal const int FileAppendData = 0x0004;
}
