/*
 * 为支持 Str 中的 CreateFile、DeleteFile、CreateDirectory、GetFileAttributesEx.cs，需引入并采用 .NET Runtime 源码
 * 见 https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/System/IO/PathInternal.Windows.cs
 * 本文件完全遵循 .NET Runtime 许可
 */

// Lukas/PathInternal.Windows.cs

using System;
using System.Runtime.CompilerServices;
using Lukas.Str;

namespace Lukas.Std;

/// <summary>
/// Windows 路径处理辅助：判断路径形态（设备路径、UNC、扩展前缀等），并在需要时为长路径补上
/// <c>\\?\</c> 扩展前缀以突破 MAX_PATH(260) 限制。逻辑沿用 .NET 运行时的路径规范化约定。
/// 这些方法仅在 Windows 路径语义下有意义。
/// </summary>
internal static class PathInternal
{
    private const char DirectorySeparatorChar = '\\';
    private const char AltDirectorySeparatorChar = '/';
    private const char VolumeSeparatorChar = ':';
    private const char PathSeparator = ';';

    private const string DirectorySeparatorCharAsString = "\\";

    private const string NtPathPrefix = @"\??\";
    private const string ExtendedPathPrefix = @"\\?\";
    private const string UncPathPrefix = @"\\";
    private const string UncExtendedPrefixToInsert = @"?\UNC\";
    private const string UncExtendedPathPrefix = @"\\?\UNC\";
    private const string UncNtPathPrefix = @"\??\UNC\";
    private const string DevicePathPrefix = @"\\.\";
    private const string ParentDirectoryPrefix = @"..\";
    private const string DirectorySeparators = @"\/";
    private static ReadOnlySpan<byte> Utf8DirectorySeparators => @"\/"u8;

    private const int MaxShortPath = 260;
    private const int MaxShortDirectoryPath = 248;
    private const int DevicePrefixLength = 4;
    private const int UncPrefixLength = 2;
    private const int UncExtendedPrefixLength = 8;

    internal static bool IsValidDriveChar(char value)
    {
        return (uint)((value | 0x20) - 'a') <= 'z' - 'a';
    }

    internal static bool EndsWithPeriodOrSpace(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
            return false;

        var c = path[^1];
        return c is ' ' or '.';
    }

    /// <summary>仅在路径可能触碰 MAX_PATH 限制（过长或以空格/句点结尾）时才补扩展前缀，否则原样返回。</summary>
    internal static ReadOnlySpan<char> EnsureExtendedPrefixIfNeeded(ReadOnlySpan<char> path)
    {
        if (path.Length >= MaxShortPath || EndsWithPeriodOrSpace(path))
        {
            return EnsureExtendedPrefix(path);
        }

        return path;
    }

    /// <summary>
    /// 为完全限定的路径补上 <c>\\?\</c>（UNC 路径补 <c>\\?\UNC\</c>）扩展前缀；
    /// 相对路径或已是设备路径的则不动。
    /// </summary>
    internal static ReadOnlySpan<char> EnsureExtendedPrefix(ReadOnlySpan<char> path)
    {
        if (IsPartiallyQualified(path) || IsDevice(path))
            return path;

        if (path.StartsWith(UncPathPrefix, StringComparison.OrdinalIgnoreCase))
            return string.Concat(path[..2], UncExtendedPrefixToInsert.AsSpan(), path[2..]);

        return string.Concat(ExtendedPathPrefix.AsSpan(), path);
    }

    internal static bool IsDevice(ReadOnlySpan<char> path)
    {
        return IsExtended(path)
               ||
               (
                   path.Length >= DevicePrefixLength
                   && IsDirectorySeparator(path[0])
                   && IsDirectorySeparator(path[1])
                   && (path[2] == '.' || path[2] == '?')
                   && IsDirectorySeparator(path[3])
               );
    }

    internal static bool IsDeviceUnc(ReadOnlySpan<char> path)
    {
        return path.Length >= UncExtendedPrefixLength
               && IsDevice(path)
               && IsDirectorySeparator(path[7])
               && path[4] == 'U'
               && path[5] == 'N'
               && path[6] == 'C';
    }

    internal static bool IsExtended(ReadOnlySpan<char> path)
    {
        return path.Length >= DevicePrefixLength
               && path[0] == '\\'
               && (path[1] == '\\' || path[1] == '?')
               && path[2] == '?'
               && path[3] == '\\';
    }

    internal static int GetRootLength(ReadOnlySpan<char> path)
    {
        var pathLength = path.Length;
        var i = 0;

        var deviceSyntax = IsDevice(path);
        var deviceUnc = deviceSyntax && IsDeviceUnc(path);

        if ((!deviceSyntax || deviceUnc) && pathLength > 0 && IsDirectorySeparator(path[0]))
        {
            if (deviceUnc || (pathLength > 1 && IsDirectorySeparator(path[1])))
            {
                i = deviceUnc ? UncExtendedPrefixLength : UncPrefixLength;

                var n = 2;
                while (i < pathLength && (!IsDirectorySeparator(path[i]) || --n > 0))
                    i++;
            }
            else
            {
                i = 1;
            }
        }
        else if (deviceSyntax)
        {
            i = DevicePrefixLength;
            while (i < pathLength && !IsDirectorySeparator(path[i]))
                i++;

            if (i < pathLength && i > DevicePrefixLength && IsDirectorySeparator(path[i]))
                i++;
        }
        else if (pathLength >= 2
                 && path[1] == VolumeSeparatorChar
                 && IsValidDriveChar(path[0]))
        {
            i = 2;

            if (pathLength > 2 && IsDirectorySeparator(path[2]))
                i++;
        }

        return i;
    }

    /// <summary>路径是否「不完全限定」（即依赖当前目录或当前盘符才能解析）。</summary>
    internal static bool IsPartiallyQualified(ReadOnlySpan<char> path)
    {
        if (path.Length < 2)
        {
            return true;
        }

        if (IsDirectorySeparator(path[0]))
        {
            return !(path[1] == '?' || IsDirectorySeparator(path[1]));
        }

        return !(path.Length >= 3
                 && path[1] == VolumeSeparatorChar
                 && IsDirectorySeparator(path[2])
                 && IsValidDriveChar(path[0]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsDirectorySeparator(char c)
    {
        return c is DirectorySeparatorChar or AltDirectorySeparatorChar;
    }

    /// <summary>把路径中的分隔符统一为 <c>\</c> 并折叠连续重复的分隔符；已规范的路径原样返回。</summary>
    internal static string? NormalizeDirectorySeparators(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        char current;

        var normalized = true;

        for (var i = 0; i < path.Length; i++)
        {
            current = path[i];
            if (IsDirectorySeparator(current)
                && (current != DirectorySeparatorChar
                    || (i > 0 && i + 1 < path.Length && IsDirectorySeparator(path[i + 1]))))
            {
                normalized = false;
                break;
            }
        }

        if (normalized)
            return path;

        var builder = new ValueStringBuilder(stackalloc char[MaxShortPath]);

        var start = 0;
        if (IsDirectorySeparator(path[start]))
        {
            start++;
            builder.Append(DirectorySeparatorChar);
        }

        for (var i = start; i < path.Length; i++)
        {
            current = path[i];

            if (IsDirectorySeparator(current))
            {
                if (i + 1 < path.Length && IsDirectorySeparator(path[i + 1]))
                {
                    continue;
                }

                current = DirectorySeparatorChar;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    internal static bool IsEffectivelyEmpty(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
            return true;

        foreach (var c in path)
        {
            if (c != ' ')
                return false;
        }
        return true;
    }
}
