// NasClientDesktop/ViewModels/MainWindowViewModel.cs
// 顶层「门卫」：
//  - 启动时若本地有刷新令牌，先静默 refresh 恢复会话；
//  - 已登录 → 显示 FileBrowser；未登录 → 显示 Login；
//  - 会话彻底失效（HttpService.SessionExpired）→ 回到 Login。

using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NasClientDesktop.Services;

namespace NasClientDesktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _svc;

    public string Title => $"{AppConfig.ProductName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLogin))]
    [NotifyPropertyChangedFor(nameof(ShowBrowser))]
    [NotifyPropertyChangedFor(nameof(ShowSplash))]
    [NotifyPropertyChangedFor(nameof(LoginVm))]
    [NotifyPropertyChangedFor(nameof(BrowserVm))]
    private ViewModelBase? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSplash))]
    [NotifyPropertyChangedFor(nameof(ShowLogin))]
    [NotifyPropertyChangedFor(nameof(ShowBrowser))]
    private bool _ready;

    public bool ShowSplash => !Ready;
    public bool ShowLogin => Ready && Current is LoginViewModel;
    public bool ShowBrowser => Ready && Current is FileBrowserViewModel;

    /// <summary>类型化访问，供子视图 DataContext 绑定（避免多态 DataContext 与编译绑定不匹配）。</summary>
    public LoginViewModel? LoginVm => Current as LoginViewModel;
    public FileBrowserViewModel? BrowserVm => Current as FileBrowserViewModel;

    /// <summary>供 View 注入对话能力（文件选择等）。切换到浏览器时回灌。</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>设计期无参构造：仅供 XAML 预览，不发起网络（直接进登录页占位）。</summary>
    public MainWindowViewModel() : this(new AppServices(), startupRestore: false)
    {
        // 设计期：显示登录页占位，便于预览。
        ShowLoginPage();
        Ready = true;
    }

    public MainWindowViewModel(AppServices svc, bool startupRestore = true)
    {
        _svc = svc;
        _svc.Http.SessionExpired += OnSessionExpired;

        // startupRestore=true 时立即启动；=false 时等待外部调用 Start()
        // （让宿主有机会先注入 Dialogs，使恢复期切到浏览器页时对话能力已就绪）。
        if (startupRestore)
            Start();
    }

    private bool _started;

    /// <summary>启动会话恢复流程。可安全重复调用（仅首次生效）。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = RestoreSessionAsync();
    }

    private void OnSessionExpired()
        => Dispatcher.UIThread.Post(ShowLoginPage);

    private async Task RestoreSessionAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_svc.Tokens.Refresh))
            {
                var ok = await _svc.Http.TryRefreshAsync().ConfigureAwait(false);
                if (ok)
                {
                    var email = _svc.Tokens.Email ?? "";
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ShowBrowserPage(email);
                        Ready = true;
                    });
                    return;
                }
            }
        }
        catch
        {
            // 恢复失败回落到登录页。
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowLoginPage();
            Ready = true;
        });
    }

    private void ShowLoginPage()
    {
        Current = new LoginViewModel(_svc.Auth, OnLoggedIn);
    }

    private void ShowBrowserPage(string email)
    {
        var browser = new FileBrowserViewModel(_svc, email, OnSessionExpired) { Dialogs = Dialogs };
        Current = browser;
    }

    private void OnLoggedIn(string email) => ShowBrowserPage(email);
}
