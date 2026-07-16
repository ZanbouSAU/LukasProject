// Lukas/Interop/Flags.cs

namespace FileTransfer;

public enum Flags
{
    CreateNew = 0x0000,
    Create = 0x0001,
    Open = 0x0002,
    OpenOrCreate = 0x0003,
    Truncate = 0x0004,
    Append = 0x0005,
    Read = 0x0006
}
