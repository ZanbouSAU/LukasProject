// NasClientDesktop/Services/PlatformLauncher.cs
// 用系统默认程序打开本地文件或 URL。
// 用于音视频预览的回落方案（embedded 播放器在 Native AOT + 三平台自包含下不可靠，见技术调研）。

using System;
using System.Diagnostics;
using Lukas.Std;

namespace NasClientDesktop.Services;

public static class PlatformLauncher
{
    /// <summary>用系统默认程序打开本地文件路径。失败抛异常由上层提示。</summary>
    public static void OpenLocalFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // UseShellExecute=true 让 shell 选择默认程序。
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { path }, UseShellExecute = false });
        }
        else // Linux 及其它类 Unix
        {
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { path }, UseShellExecute = false });
        }
    }

    /// <summary>用系统默认浏览器打开 URL。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { url }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { url }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Io.Stderr.WriteLine($"[PlatformLauncher] OpenUrl 失败：{ex.Message}");
            Io.Stderr.Flush();
        }
    }
}
