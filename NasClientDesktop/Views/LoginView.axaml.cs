// NasClientDesktop/Views/LoginView.axaml.cs

using Avalonia.Controls;
using Avalonia.Input;
using NasClientDesktop.ViewModels;

namespace NasClientDesktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    // Enter 键提交
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter && DataContext is LoginViewModel vm && vm.SubmitCommand.CanExecute(null))
        {
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
