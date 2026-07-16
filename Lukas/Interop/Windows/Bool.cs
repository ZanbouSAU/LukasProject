// Lukas/Interop/Windows/Bool.cs

namespace Lukas.Interop.Windows;

// Win32 BOOL 的托管表示（非 0 即真）。

internal static class Interop
{
    internal enum Bool
    {
        False = 0,
        True = 1
    }
}
