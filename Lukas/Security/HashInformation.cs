// Lukas/Security/HashInformation.cs

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Lukas.Security;

[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Local")]
public sealed class HashInformation
{
    internal HashInformation(byte[] settings, byte[] version, int workFactor, byte[] rawHash)
    {
        Settings = settings;
        Version = version;
        WorkFactor = workFactor;
        RawHash = rawHash;
    }

    private byte[] Settings { get; set; }

    private byte[] Version { get; set; }

    private int WorkFactor { get; set; }

    private byte[] RawHash { get; set; }

    public override string ToString() =>
        $"Settings: {Encoding.ASCII.GetString(Settings)}, Version: {Encoding.ASCII.GetString(Version)}, WorkFactor: {WorkFactor}, RawHash: {Encoding.ASCII.GetString(RawHash)}";
}
