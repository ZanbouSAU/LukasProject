// Lukas/SafeName.cs

using System.IO;
using System.Linq;

namespace Lukas.Std;

/// <summary>
/// 把外部传入的文件名清洗成可安全落盘的纯文件名，主要用于防范路径穿越。
/// 典型场景：文件服务器收到对端给的名字，不能直接拿来拼路径。
/// </summary>
public static class SafeName
{
    /// <summary>
    /// 尝试将 <paramref name="rawName"/> 规整为安全文件名：
    /// 去掉所有目录部分、首尾空白，拒绝空名、<c>"."</c>/<c>".."</c>、含非法字符或盘符冒号的名字。
    /// 成功时 <paramref name="safe"/> 为清洗后的名字并返回 <see langword="true"/>。
    /// </summary>
    public static bool TryMakeSafe(string? rawName, out string safe)
    {
        safe = string.Empty;
        if (string.IsNullOrWhiteSpace(rawName))
            return false;
        
        var name = rawName.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
            name = name[(lastSlash + 1)..];

        name = name.Trim();

        if (name.Length == 0 || name == "." || name == "..")
            return false;
        
        if (name.IndexOfAny(Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToArray()) >= 0)
            return false;
        
        if (name.Contains(':'))
            return false;

        safe = name;
        return true;
    }
}
