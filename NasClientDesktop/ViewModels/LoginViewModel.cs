// NasClientDesktop/ViewModels/LoginViewModel.cs
// 登录/注册。

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NasClientDesktop.Services;

namespace NasClientDesktop.ViewModels;

public sealed partial class LoginViewModel(AuthService auth, Action<string> onLoggedIn) : ViewModelBase
{
    // 传出已登录邮箱

    // 模式：false=登录，true=注册
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogin))]
    [NotifyPropertyChangedFor(nameof(ModeTitle))]
    [NotifyPropertyChangedFor(nameof(SubmitText))]
    private bool _isRegister;

    public bool IsLogin => !IsRegister;
    public string ModeTitle => IsRegister ? "创建账号" : "登录";
    public string SubmitText => IsRegister ? "创建账号" : "登录";

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirm = "";
    [ObservableProperty] private string _fullName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _busy;

    public bool NotBusy => !Busy;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _notice;

    [RelayCommand]
    private void SwitchToLogin()
    {
        IsRegister = false;
        Error = null;
        Notice = null;
    }

    [RelayCommand]
    private void SwitchToRegister()
    {
        IsRegister = true;
        Error = null;
        Notice = null;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        Error = null;
        Notice = null;

        var email = Email.Trim();
        if (email.Length == 0)
        {
            Error = "请输入邮箱";
            return;
        }
        if (Password.Length < 8)
        {
            Error = "密码至少 8 位";
            return;
        }
        if (IsRegister && Password != Confirm)
        {
            Error = "两次输入的密码不一致";
            return;
        }

        Busy = true;
        try
        {
            if (IsRegister)
            {
                var name = FullName.Trim();
                await auth.RegisterAsync(email, Password, Confirm, name.Length > 0 ? name : null);
                Notice = "注册成功，请登录";
                IsRegister = false;
                Password = "";
                Confirm = "";
            }
            else
            {
                var auth1 = await auth.LoginAsync(email, Password);
                onLoggedIn(auth1.Email);
            }
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = "网络错误，请稍后重试";
        }
        finally
        {
            Busy = false;
        }
    }
}
