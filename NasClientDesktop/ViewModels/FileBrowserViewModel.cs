// NasClientDesktop/ViewModels/FileBrowserViewModel.cs
// 主界面（对应前端 FileBrowser.tsx）。签名元素：shell 提示符样式的路径栏（user@nas:~/path $）。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NasClientDesktop.Services;

namespace NasClientDesktop.ViewModels;

public sealed partial class FileBrowserViewModel : ViewModelBase
{
    private readonly AppServices _svc;
    private readonly Action _onSessionExpired;

    public IDialogService? Dialogs { get; set; } // 由 View 注入

    public UploadQueue Uploads { get; }

    // 当前目录（空串=根）
    [ObservableProperty]
    private string _cwd = "";

    [ObservableProperty] private string _email;
    public string UserName
    {
        get
        {
            var at = Email.IndexOf('@');
            return at > 0 ? Email[..at] : (Email.Length > 0 ? Email : "guest");
        }
    }

    /// <summary>shell 提示符前缀：<c>user@nas:</c>。</summary>
    public string Prompt => $"{UserName}@nas:";

    public ObservableCollection<PathSegmentViewModel> Segments { get; } = new();
    public ObservableCollection<FileRowViewModel> Entries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotLoading))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _loading;
    public bool NotLoading => !Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? _loadError;

    public bool ShowEmpty => !Loading && LoadError == null && Entries.Count == 0;

    [ObservableProperty] private string? _toast;
    private CancellationTokenSource? _toastCts;

    // 预览模态
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private PreviewViewModel? _preview;
    public bool HasPreview => Preview != null;

    private CancellationTokenSource? _listCts;

    public FileBrowserViewModel(AppServices svc, string email, Action onSessionExpired)
    {
        _svc = svc;
        _onSessionExpired = onSessionExpired;
        _email = email;
        Uploads = new UploadQueue(_svc.Files, () => Dispatcher.UIThread.Post(RefreshCwd));
        RebuildSegments();
        _ = RefreshAsync(_cwd);
    }

    partial void OnCwdChanged(string value)
    {
        RebuildSegments();
        OnPropertyChanged(nameof(MkdirPrompt));
        _ = RefreshAsync(value);
    }

    partial void OnEmailChanged(string value)
    {
        OnPropertyChanged(nameof(UserName));
        OnPropertyChanged(nameof(Prompt));
    }

    private void RebuildSegments()
    {
        Segments.Clear();
        Segments.Add(new PathSegmentViewModel("~", "")); // 根
        var segs = Format.SplitPath(Cwd);
        for (var i = 0; i < segs.Count; i++)
        {
            var target = string.Join('/', segs.GetRange(0, i + 1));
            Segments.Add(new PathSegmentViewModel(segs[i], target));
        }
    }

    // ---------------------------------------------------------------- 列目录

    private async Task RefreshAsync(string path)
    {
        var previous = _listCts;
        var cts = new CancellationTokenSource();
        _listCts = cts;
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        Loading = true;
        LoadError = null;
        try
        {
            var res = await _svc.Files.ListDirAsync(path, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_listCts != cts) return; // 已被更晚的请求取代
                Entries.Clear();
                foreach (var e in res.Entries)
                    Entries.Add(new FileRowViewModel(e));
                Loading = false;
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
        catch (OperationCanceledException) { /* 被取代 */ }
        catch (ApiException ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_listCts != cts) return;
                LoadError = ex.Status == 401 ? "会话已过期，请重新登录" : ex.Message;
                Loading = false;
            });
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_listCts != cts) return;
                LoadError = "加载失败，请检查网络";
                Loading = false;
            });
        }
    }

    private void RefreshCwd() => _ = RefreshAsync(Cwd);

    [RelayCommand]
    private void NavigateTo(string target) => Cwd = target;

    [RelayCommand]
    private void OpenEntry(FileRowViewModel row)
    {
        if (row.IsDirectory) Cwd = row.Path;
        else if (row.IsPreviewable) OpenPreview(row);
    }

    [RelayCommand]
    private void Retry() => RefreshCwd();

    // ---------------------------------------------------------------- 预览

    private void OpenPreview(FileRowViewModel row)
    {
        if (!row.IsPreviewable) return;
        Preview = new PreviewViewModel(
            _svc.Files, row.Entry, row.PreviewKind,
            onClose: () => Preview = null,
            onSaved: RefreshCwd);
    }

    [RelayCommand]
    private void PreviewRow(FileRowViewModel row) => OpenPreview(row);

    // ---------------------------------------------------------------- 顶栏：登出

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _svc.Auth.LogoutAsync();
        _onSessionExpired();
    }

    [RelayCommand]
    private async Task LogoutAllAsync()
    {
        try
        {
            await _svc.Auth.LogoutAllAsync();
            _onSessionExpired();
        }
        catch
        {
            ShowToast("吊销失败，请检查网络后重试");
        }
    }

    // ---------------------------------------------------------------- 新建目录

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MkdirNotBusy))]
    private bool _mkdirOpen;

    [ObservableProperty] private string _mkdirName = "";
    [ObservableProperty] private string? _mkdirError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MkdirNotBusy))]
    private bool _mkdirBusy;
    public bool MkdirNotBusy => !MkdirBusy;
    public string MkdirPrompt => $"在 ~/{Cwd} 下新建目录";

    [RelayCommand]
    private void OpenMkdir()
    {
        MkdirName = "";
        MkdirError = null;
        MkdirOpen = true;
    }

    [RelayCommand]
    private void CancelMkdir() => MkdirOpen = false;

    [RelayCommand]
    private async Task SubmitMkdirAsync()
    {
        var invalid = Format.ValidateNameSegment(MkdirName);
        if (invalid != null) { MkdirError = invalid; return; }

        MkdirBusy = true;
        MkdirError = null;
        try
        {
            await _svc.Files.MkDirAsync(Format.JoinPath(Cwd, MkdirName.Trim()));
            MkdirOpen = false;
            MkdirName = "";
            ShowToast("目录已创建");
            RefreshCwd();
        }
        catch (ApiException ex) { MkdirError = ex.Message; }
        catch (Exception ex) { MkdirError = "创建失败：" + ex.Message; }
        finally { MkdirBusy = false; }
    }

    // ---------------------------------------------------------------- 新建文件

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFileNotBusy))]
    private bool _newFileOpen;

    [ObservableProperty] private string _newFileName = "";
    [ObservableProperty] private string? _newFileError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFileNotBusy))]
    private bool _newFileBusy;
    public bool NewFileNotBusy => !NewFileBusy;
    public string NewFilePrompt => $"在 ~/{Cwd} 下新建文件";

    [RelayCommand]
    private void OpenNewFile()
    {
        NewFileName = "";
        NewFileError = null;
        NewFileOpen = true;
    }

    [RelayCommand]
    private void CancelNewFile() => NewFileOpen = false;

    [RelayCommand]
    private async Task SubmitNewFileAsync()
    {
        var invalid = Format.ValidateNameSegment(NewFileName);
        if (invalid != null) { NewFileError = invalid; return; }

        NewFileBusy = true;
        NewFileError = null;
        try
        {
            await _svc.Files.NewFileAsync(Format.JoinPath(Cwd, NewFileName.Trim()));
            NewFileOpen = false;
            NewFileName = "";
            ShowToast("文件已创建");
            RefreshCwd();
        }
        catch (ApiException ex) { NewFileError = ex.Message; }
        catch (Exception ex) { NewFileError = "创建失败：" + ex.Message; }
        finally { NewFileBusy = false; }
    }

    // ---------------------------------------------------------------- 重命名

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameTarget))]
    [NotifyPropertyChangedFor(nameof(RenamePrompt))]
    private partial FileRowViewModel? RenameTarget { get; set; }

    public bool HasRenameTarget => RenameTarget != null;
    public string RenamePrompt => RenameTarget == null ? "" : $"重命名 {RenameTarget.Name}";

    [ObservableProperty] private string _renameName = "";
    [ObservableProperty] private string? _renameError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameNotBusy))]
    private bool _renameBusy;
    public bool RenameNotBusy => !RenameBusy;

    [RelayCommand]
    private void OpenRename(FileRowViewModel row)
    {
        RenameTarget = row;
        RenameName = row.Name;
        RenameError = null;
    }

    [RelayCommand]
    private void CancelRename() => RenameTarget = null;

    [RelayCommand]
    private async Task SubmitRenameAsync()
    {
        var target = RenameTarget;
        if (target == null) return;

        var invalid = Format.ValidateNameSegment(RenameName);
        if (invalid != null) { RenameError = invalid; return; }

        var parts = Format.SplitPath(target.Path);
        parts.RemoveAt(parts.Count - 1);
        var parent = string.Join('/', parts);
        var dest = Format.JoinPath(parent, RenameName.Trim());
        if (dest == target.Path) { RenameTarget = null; return; }

        RenameBusy = true;
        RenameError = null;
        try
        {
            if (!await MoveWithOverwriteAsync(target.Path, dest)) return;
            RenameTarget = null;
            RenameName = "";
            ShowToast("已重命名");
            RefreshCwd();
        }
        catch (ApiException ex) { RenameError = ex.Message; }
        catch (Exception ex) { RenameError = "重命名失败：" + ex.Message; }
        finally { RenameBusy = false; }
    }

    // ---------------------------------------------------------------- 移动 / 复制（目录选择器）

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferActive))]
    [NotifyPropertyChangedFor(nameof(TransferTitle))]
    [NotifyPropertyChangedFor(nameof(TransferConfirmText))]
    private partial FileRowViewModel? TransferRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferTitle))]
    [NotifyPropertyChangedFor(nameof(TransferConfirmText))]
    private partial bool TransferIsCopy { get; set; }

    public bool TransferActive => TransferRow != null;
    public string TransferTitle => TransferRow == null
        ? ""
        : (TransferIsCopy ? $"复制「{TransferRow.Name}」到…" : $"移动「{TransferRow.Name}」到…");
    public string TransferConfirmText => TransferIsCopy ? "复制到此目录" : "移动到此目录";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerDisplayPath))]
    [NotifyPropertyChangedFor(nameof(PickerCanGoUp))]
    private string _pickerCwd = "";

    public ObservableCollection<FileRowViewModel> PickerDirs { get; } = new();
    public string PickerDisplayPath => PickerCwd.Length == 0 ? "~" : "~/" + PickerCwd;
    public bool PickerCanGoUp => PickerCwd.Length > 0;

    [ObservableProperty] private string? _pickerError;
    [ObservableProperty] private bool _pickerLoading;

    [RelayCommand]
    private void OpenMove(FileRowViewModel row) => StartTransfer(row, isCopy: false);

    [RelayCommand]
    private void OpenCopy(FileRowViewModel row) => StartTransfer(row, isCopy: true);

    private void StartTransfer(FileRowViewModel row, bool isCopy)
    {
        TransferRow = row;
        TransferIsCopy = isCopy;
        PickerError = null;
        _ = LoadPickerAsync(Cwd);
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        TransferRow = null;
        PickerDirs.Clear();
    }

    [RelayCommand]
    private void PickerEnter(FileRowViewModel dir) => _ = LoadPickerAsync(dir.Path);

    [RelayCommand]
    private void PickerGoUp()
    {
        var parts = Format.SplitPath(PickerCwd);
        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        _ = LoadPickerAsync(string.Join('/', parts));
    }

    private async Task LoadPickerAsync(string path)
    {
        PickerLoading = true;
        PickerError = null;
        try
        {
            var res = await _svc.Files.ListDirAsync(path);
            PickerCwd = res.Path;
            PickerDirs.Clear();
            foreach (var e in res.Entries)
                if (e.IsDirectory)
                    PickerDirs.Add(new FileRowViewModel(e));
        }
        catch (ApiException ex) { PickerError = ex.Message; }
        catch (Exception ex) { PickerError = "加载目录失败：" + ex.Message; }
        finally { PickerLoading = false; }
    }

    [RelayCommand]
    private async Task ConfirmTransferAsync()
    {
        var row = TransferRow;
        if (row == null) return;
        var isCopy = TransferIsCopy;
        var dest = Format.JoinPath(PickerCwd, row.Name);

        TransferRow = null;
        PickerDirs.Clear();

        if (dest == row.Path)
        {
            ShowToast("源与目标目录相同");
            return;
        }

        try
        {
            if (isCopy)
            {
                if (await CopyWithOverwriteAsync(row.Path, dest, row.IsDirectory))
                    ShowToast("已复制");
            }
            else
            {
                if (await MoveWithOverwriteAsync(row.Path, dest))
                    ShowToast("已移动");
            }
            RefreshCwd();
        }
        catch (ApiException ex) { ShowToast(ex.Message); }
        catch (Exception ex) { ShowToast((isCopy ? "复制失败：" : "移动失败：") + ex.Message); }
    }

    private async Task<bool> MoveWithOverwriteAsync(string source, string dest)
    {
        try
        {
            await _svc.Files.MoveAsync(source, dest, false);
            return true;
        }
        catch (ApiException ex) when (ex.Status == 409)
        {
            if (Dialogs == null) throw;
            if (!await Dialogs.ConfirmAsync("目标已存在", $"~/{dest} 已存在，是否覆盖？")) return false;
            await _svc.Files.MoveAsync(source, dest, true);
            return true;
        }
    }

    private async Task<bool> CopyWithOverwriteAsync(string source, string dest, bool isDir)
    {
        try
        {
            await _svc.Files.CopyAsync(source, dest, false);
            return true;
        }
        catch (ApiException ex) when (ex.Status == 409)
        {
            if (Dialogs == null) throw;
            var msg = isDir
                ? $"目标目录下存在同名文件（~/{dest}），是否覆盖已存在的文件？"
                : $"~/{dest} 已存在，是否覆盖？";
            if (!await Dialogs.ConfirmAsync("目标已存在", msg)) return false;
            await _svc.Files.CopyAsync(source, dest, true);
            return true;
        }
    }

    // ---------------------------------------------------------------- 删除

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeleteTarget))]
    [NotifyPropertyChangedFor(nameof(DeleteIsDirectory))]
    [NotifyPropertyChangedFor(nameof(DeletePromptText))]
    private partial FileRowViewModel? DeleteTarget { get; set; }

    public bool HasDeleteTarget => DeleteTarget != null;
    public bool DeleteIsDirectory => DeleteTarget?.IsDirectory ?? false;

    [ObservableProperty]
    public partial bool DeleteRecursive { get; set; }

    [ObservableProperty]
    public partial string? DeleteError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteNotBusy))]
    private partial bool DeleteBusy { get; set; }

    public bool DeleteNotBusy => !DeleteBusy;

    public string DeletePromptText
    {
        get
        {
            if (DeleteTarget == null) return "";
            var kind = DeleteTarget.IsDirectory ? "目录" : "文件";
            var slash = DeleteTarget.IsDirectory ? "/" : "";
            return $"确认删除{kind} {DeleteTarget.Name}{slash} ？此操作不可恢复。";
        }
    }

    [RelayCommand]
    private void OpenDelete(FileRowViewModel row)
    {
        DeleteTarget = row;
        DeleteRecursive = false;
        DeleteError = null;
    }

    [RelayCommand]
    private void CancelDelete() => DeleteTarget = null;

    [RelayCommand]
    private async Task SubmitDeleteAsync()
    {
        var target = DeleteTarget;
        if (target == null) return;
        DeleteBusy = true;
        DeleteError = null;
        try
        {
            var res = await _svc.Files.DeleteEntryAsync(target.Path, target.IsDirectory && DeleteRecursive);
            DeleteTarget = null;
            ShowToast(res.Message);
            RefreshCwd();
        }
        catch (ApiException ex) { DeleteError = ex.Message; }
        catch (Exception ex) { DeleteError = "删除失败：" + ex.Message; }
        finally { DeleteBusy = false; }
    }

    // ---------------------------------------------------------------- 下载

    [RelayCommand]
    private async Task DownloadAsync(FileRowViewModel row)
    {
        if (Dialogs == null) return;
        try
        {
            string? localTarget;
            long knownSize;
            if (row.IsDirectory)
            {
                var dir = await Dialogs.PickSaveFolderAsync();
                if (dir == null) return;
                localTarget = Format.JoinPathLocal(dir, row.Name + ".zip");
                knownSize = 0; // 目录打包 zip，大小未知，进度按响应头决定
            }
            else
            {
                localTarget = await Dialogs.PickSavePathAsync(row.Name);
                if (localTarget == null) return;
                knownSize = row.Entry.Size; // 文件大小已知，可显示百分比
            }

            // 交给传输队列：与上传共用任务框，显示 0~100% 进度、可取消。
            Uploads.AddDownload(row.Name, row.Path, localTarget, knownSize);
        }
        catch (Exception ex) { ShowToast("下载失败：" + ex.Message); }
    }

    // ---------------------------------------------------------------- 上传触发

    [RelayCommand]
    private async Task UploadFilesAsync()
    {
        if (Dialogs == null) return;
        var picked = await Dialogs.PickFilesAsync(allowMultiple: true);
        if (picked.Count == 0) return;
        EnqueueUploads(picked);
    }

    [RelayCommand]
    private async Task UploadFolderAsync()
    {
        if (Dialogs == null) return;
        var picked = await Dialogs.PickFolderAsync();
        if (picked.Count == 0) return;
        EnqueueUploads(picked);
    }

    [RelayCommand]
    private async Task UploadZipAsync()
    {
        if (Dialogs == null) return;
        var zip = await Dialogs.PickZipAsync();
        if (zip == null) return;
        Uploads.AddZip(Cwd, zip.LocalPath, zip.Size);
    }

    /// <summary>拖放上传：把本地文件路径直接入队。</summary>
    public void DropLocalFiles(IEnumerable<PickedFile> files) => EnqueueUploads(files);

    private void EnqueueUploads(IEnumerable<PickedFile> files)
    {
        var list = new List<(string, string, long)>();
        foreach (var f in files)
            list.Add((f.LocalPath, f.RelName, f.Size));
        Uploads.AddFiles(Cwd, list);
    }

    // ---------------------------------------------------------------- Toast

    private void ShowToast(string message)
    {
        Toast = message;
        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = Task.Delay(3500, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.UIThread.Post(() => { if (_toastCts == cts) Toast = null; });
        }, TaskScheduler.Default);
    }
}
