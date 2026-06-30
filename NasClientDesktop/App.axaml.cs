// NasClientDesktop/App.axaml.cs

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NasClientDesktop.Services;
using NasClientDesktop.ViewModels;
using NasClientDesktop.Views;

namespace NasClientDesktop;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动时尽力清理上次的预览缓存。
            TempFile.TryCleanup();

            var services = new AppServices();

            // 先构造 VM 但不自动恢复会话，待注入对话能力后再显式启动，
            // 保证恢复流程若切到浏览器页时 Dialogs 已就绪。
            var vm = new MainWindowViewModel(services, startupRestore: false);
            var window = new MainWindow { DataContext = vm };

            // View 拥有 TopLevel，能提供 StorageProvider；注入对话能力给 VM。
            vm.Dialogs = new StorageDialogService(window);

            desktop.MainWindow = window;

            // 现在再启动会话恢复（静默 refresh）。
            vm.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
