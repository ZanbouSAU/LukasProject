// NasClientDesktop/Program.cs

using System;
using Avalonia;

namespace NasClientDesktop;

internal static class Program
{
    // Native AOT 下不要在 Main 之前触碰任何 Avalonia 类型。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
