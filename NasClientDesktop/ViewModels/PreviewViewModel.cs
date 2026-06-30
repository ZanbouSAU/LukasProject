// NasClientDesktop/ViewModels/PreviewViewModel.cs
// 在线预览/文本编辑。
//  - 图片：Avalonia 内置 Bitmap 可解码的格式（jpg/png/gif/webp/bmp）直接下载到临时文件并解码显示；
//          svg/ico/avif 等内置不支持的，下载后交系统默认程序打开。
//  - 文本：在线读取到可编辑区，Ctrl+S 覆盖保存。
//  - 音视频：内嵌播放器在 Native AOT + 三平台自包含下不可靠（见技术调研），
//          故下载内联预览流到临时文件后用系统默认播放器打开（服务端支持 Range，播放器可拖动）。

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NasClientDesktop.Models;
using NasClientDesktop.Services;
using Lukas.Std;

namespace NasClientDesktop.ViewModels;

public sealed partial class PreviewViewModel : ViewModelBase
{
    private readonly FileService _files;
    private readonly FileEntry _entry;
    private readonly PreviewKind _kind;
    private readonly Action _onClose;
    private readonly Action _onSaved;
    private readonly CancellationTokenSource _cts = new();

    public string Name => _entry.Name;
    public string Path => _entry.Path;

    public bool IsImage => _kind == PreviewKind.Image;
    public bool IsText => _kind == PreviewKind.Text;
    public bool IsMedia => _kind is PreviewKind.Video or PreviewKind.Audio;

    // ---- 图片 ----
    [ObservableProperty] private Bitmap? _image;

    // ---- 通用加载/错误态 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotLoading))]
    private bool _loading = true;
    public bool NotLoading => !Loading;

    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private string? _statusMessage;

    // ---- 文本 ----
    private string? _original;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Dirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private string _textValue = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _saving;

    [ObservableProperty] private string? _saveError;
    [ObservableProperty] private bool _savedFlag;

    public bool Dirty => _original != null && TextValue != _original;
    public bool CanSave => Dirty && !Saving;
    public string SizeText => Format.Size(System.Text.Encoding.UTF8.GetByteCount(TextValue));

    public PreviewViewModel(FileService files, FileEntry entry, PreviewKind kind, Action onClose, Action onSaved)
    {
        _files = files;
        _entry = entry;
        _kind = kind;
        _onClose = onClose;
        _onSaved = onSaved;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            switch (_kind)
            {
                case PreviewKind.Image:
                    await LoadImageAsync();
                    break;
                case PreviewKind.Text:
                    await LoadTextAsync();
                    break;
                case PreviewKind.Video:
                case PreviewKind.Audio:
                    await LoadMediaAsync();
                    break;
            }
        }
        catch (OperationCanceledException) { /* 关闭时取消 */ }
        catch (ApiException ex)
        {
            await OnUi(() => { LoadError = ex.Message; Loading = false; });
        }
        catch (Exception ex)
        {
            await OnUi(() => { LoadError = "加载失败：" + ex.Message; Loading = false; });
        }
    }

    private async Task LoadImageAsync()
    {
        // svg/ico/avif：Avalonia 内置位图不支持，下载后交系统默认程序打开。
        if (!Format.IsBitmapNative(_entry.Name))
        {
            var path0 = await _files.DownloadPreviewToTempAsync(_entry, ct: _cts.Token).ConfigureAwait(false);
            PlatformLauncher.OpenLocalFile(path0);
            await OnUi(() =>
            {
                Loading = false;
                StatusMessage = "该图片格式已用系统默认程序打开。";
            });
            return;
        }

        var tempPath = await _files.DownloadPreviewToTempAsync(_entry, ct: _cts.Token).ConfigureAwait(false);
        // 经 NasLib 读取临时文件字节，交给 Avalonia 解码（避免 System.IO）。
        var bytes = Io.File.ReadAllBytes(tempPath.AsSpan());
        using var ms = new System.IO.MemoryStream(bytes, writable: false);
        var bmp = new Bitmap(ms);
        await OnUi(() =>
        {
            Image = bmp;
            Loading = false;
        });
    }

    private async Task LoadTextAsync()
    {
        var res = await _files.ReadTextAsync(_entry.Path, _cts.Token).ConfigureAwait(false);
        await OnUi(() =>
        {
            _original = res.Content;
            TextValue = res.Content;
            Loading = false;
        });
    }

    private async Task LoadMediaAsync()
    {
        var tempPath = await _files.DownloadPreviewToTempAsync(_entry, ct: _cts.Token).ConfigureAwait(false);
        PlatformLauncher.OpenLocalFile(tempPath);
        await OnUi(() =>
        {
            Loading = false;
            StatusMessage = "已用系统默认播放器打开（支持拖动进度）。";
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        Saving = true;
        SaveError = null;
        try
        {
            await _files.SaveTextAsync(_entry.Path, TextValue);
            _original = TextValue;
            OnPropertyChanged(nameof(Dirty));
            OnPropertyChanged(nameof(CanSave));
            SavedFlag = true;
            _onSaved();
        }
        catch (ApiException ex)
        {
            SaveError = ex.Message;
        }
        catch (Exception ex)
        {
            SaveError = "保存失败：" + ex.Message;
        }
        finally
        {
            Saving = false;
        }
    }

    partial void OnTextValueChanged(string value)
    {
        if (SavedFlag) SavedFlag = false;
    }

    [RelayCommand]
    private void Close()
    {
        _cts.Cancel();
        Image?.Dispose();
        _onClose();
    }

    private static async Task OnUi(Action action)
        => await Dispatcher.UIThread.InvokeAsync(action);
}
