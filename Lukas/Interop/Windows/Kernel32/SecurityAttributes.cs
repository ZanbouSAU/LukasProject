// Lukas/Interop/Windows/Kernel32/SecurityAttributes.cs

namespace Lukas.Interop.Windows.Kernel32;

// SECURITY_ATTRIBUTES：句柄的安全描述符与可继承性，按需构造。

internal static unsafe partial class Kernel32
{
    internal struct SecurityAttributes
    {
        internal uint NLength;
        internal void* LpSecurityDescriptor;
        internal Interop.Bool BInheritHandle;

        internal static SecurityAttributes Create() =>
            new()
            {
                NLength = (uint)sizeof(SecurityAttributes),
            };

        internal static SecurityAttributes Create(void* securityDescriptor) =>
            new()
            {
                NLength = (uint)sizeof(SecurityAttributes),
                LpSecurityDescriptor = securityDescriptor,
            };

        internal static SecurityAttributes Create(bool inheritable) =>
            new()
            {
                NLength = (uint)sizeof(SecurityAttributes),
                BInheritHandle = inheritable ? Interop.Bool.True : Interop.Bool.False
            };
    }
}
