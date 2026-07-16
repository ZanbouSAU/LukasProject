// NasClientDesktop/Views/PreviewView.axaml.cs

using Avalonia.Controls;
using Avalonia.Input;
using NasClientDesktop.ViewModels;

namespace NasClientDesktop.Views;

public partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not PreviewViewModel vm) return;

        // Esc 关闭
        if (e.Key == Key.Escape)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl/Cmd+S 保存
        if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            if (vm.SaveCommand.CanExecute(null)) vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }
}