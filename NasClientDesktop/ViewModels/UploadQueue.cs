// NasClientDesktop/ViewModels/UploadQueue.cs
// 传输队列（上传 + 下载共用，对应前端 lib/uploads.ts 的扩展）：
//  - 并发上限 3，支持取消、上传失败后「覆盖重传」（应对 409 同名冲突）。
//  - 上传：单文件 PUT；zip 走 POST /upload-zip 由服务端解压。
//  - 下载：票据直链 GET，边收边写盘，进度按 Content-Length 计算（目录打包 zip 时长度可能未知）。
//  - 进度回报切回 UI 线程更新 ObservableProperty。

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

public enum UploadStatus { Queued, Running, Done, Error, Cancelled }

/// <summary>传输类型：上传单文件 / 上传 zip 解压 / 下载。</summary>
public enum UploadKind { File, Zip, Download }

public sealed partial class UploadTaskViewModel(
    int id,
    string label,
    UploadKind kind,
    string localPath,
    string destPath,
    long total) : ViewModelBase
{
    public int Id { get; } = id;
    public string Label { get; } = label;
    public UploadKind Kind { get; } = kind;

    /// <summary>上传：本地源文件路径；下载：本地保存目标路径。</summary>
    public string LocalPath { get; } = localPath;

    /// <summary>
    /// 上传：服务端目标相对路径（文件为文件路径，zip 为目标目录）；下载：服务端源相对路径。
    /// </summary>
    public string DestPath { get; } = destPath;

    /// <summary>是否为下载任务（用于 UI 文案与方向分流）。</summary>
    public bool IsDownload => Kind == UploadKind.Download;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(HasPercent))]
    private UploadStatus _status = UploadStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    private long _sent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    [NotifyPropertyChangedFor(nameof(HasPercent))]
    private long _total = total;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetryOverwrite))]
    private bool _conflict;

    public bool Overwrite { get; set; }

    internal CancellationTokenSource? Cts;

    public int Percent => Total > 0 ? (int)(Sent * 100 / Total) : 0;

    /// <summary>总大小已知（能显示百分比）。下载目录打包 zip 时长度可能未知。</summary>
    public bool HasPercent => Total > 0;

    public bool IsActive => Status is UploadStatus.Queued or UploadStatus.Running;
    public bool CanRetryOverwrite => Status == UploadStatus.Error && Conflict;

    public string StatusText => Status switch
    {
        UploadStatus.Running => HasPercent ? $"{Percent}%" : (IsDownload ? "下载中" : "上传中"),
        UploadStatus.Queued => "排队中",
        UploadStatus.Done => "完成",
        UploadStatus.Cancelled => "已取消",
        UploadStatus.Error => "失败",
        _ => "",
    };

    partial void OnStatusChanged(UploadStatus value) => OnPropertyChanged(nameof(StatusText));
    partial void OnSentChanged(long value) => OnPropertyChanged(nameof(StatusText));
}

public sealed partial class UploadQueue : ObservableObject
{
    private const int Concurrency = 3;

    private readonly FileService _files;
    private readonly Action _onUploaded;
    private readonly List<UploadTaskViewModel> _internal = new();
    private int _nextId = 1;
    private int _runningCount;
    private readonly Lock _gate = new();

    /// <summary>UI 可观察的任务集合（仅在 UI 线程修改）。</summary>
    public ObservableCollection<UploadTaskViewModel> Tasks { get; } = new();

    /// <summary>是否有任务（用于任务托盘的显隐；int→bool 不会被 IsVisible 隐式转换，故显式暴露）。</summary>
    public bool HasTasks => Tasks.Count > 0;

    public UploadQueue(FileService files, Action onUploaded)
    {
        _files = files;
        _onUploaded = onUploaded;
        Tasks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTasks));
    }

    /// <summary>把若干本地文件上传到 cwd；保留相对目录结构（rel 含子目录时）。</summary>
    public void AddFiles(string cwd, IEnumerable<(string localPath, string relName, long size)> files)
    {
        foreach (var (localPath, relName, size) in files)
        {
            var dest = Format.JoinPath(cwd, relName);
            var id = _nextId++;
            var vm = new UploadTaskViewModel(id, dest.Length == 0 ? "~" : dest, UploadKind.File, localPath, dest, size);
            _internal.Add(vm);
            Tasks.Add(vm);
        }
        Pump();
    }

    /// <summary>上传一个 zip 并由服务端解压到 cwd。</summary>
    public void AddZip(string cwd, string localZipPath, long size)
    {
        var id = _nextId++;
        var label = (cwd.Length == 0 ? "~" : cwd);
        var vm = new UploadTaskViewModel(id, label, UploadKind.Zip, localZipPath, cwd, size);
        _internal.Add(vm);
        Tasks.Add(vm);
        Pump();
    }

    /// <summary>
    /// 新增一个下载任务：把服务端 <paramref name="remotePath"/> 下载到本地 <paramref name="localTargetPath"/>。
    /// <paramref name="knownSize"/> 为已知大小（文件大小）；目录打包 zip 时传 0（长度运行时由响应头决定）。
    /// </summary>
    public void AddDownload(string label, string remotePath, string localTargetPath, long knownSize)
    {
        var id = _nextId++;
        var vm = new UploadTaskViewModel(id, label, UploadKind.Download, localTargetPath, remotePath, knownSize);
        _internal.Add(vm);
        Tasks.Add(vm);
        Pump();
    }

    /// <summary>调度等待中的任务。始终在 UI 线程执行：既保护 _runningCount/_internal，
    /// 也保证 task.Status 的变更（触发 PropertyChanged）发生在 UI 线程。</summary>
    private void Pump()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Pump);
            return;
        }

        lock (_gate)
        {
            while (_runningCount < Concurrency)
            {
                UploadTaskViewModel? next = null;
                foreach (var t in _internal)
                    if (t.Status == UploadStatus.Queued) { next = t; break; }
                if (next is null) return;

                _runningCount++;
                next.Status = UploadStatus.Running;
                _ = RunAsync(next);
            }
        }
    }

    private async Task RunAsync(UploadTaskViewModel task)
    {
        var cts = new CancellationTokenSource();
        task.Cts = cts;

        try
        {
            switch (task.Kind)
            {
                case UploadKind.Zip:
                    await _files.UploadZipAsync(task.LocalPath, task.DestPath, task.Overwrite, OnProgress, cts.Token).ConfigureAwait(false);
                    break;
                case UploadKind.Download:
                    await _files.DownloadToFileAsync(task.DestPath, task.LocalPath, OnProgress, cts.Token).ConfigureAwait(false);
                    break;
                default:
                    await _files.UploadFileAsync(task.LocalPath, task.DestPath, task.Overwrite, OnProgress, cts.Token).ConfigureAwait(false);
                    break;
            }

            Dispatcher.UIThread.Post(() =>
            {
                task.Status = UploadStatus.Done;
                if (task.Total > 0) task.Sent = task.Total;
            });

            // 仅上传需要刷新当前目录；下载不改变服务端目录。
            if (!task.IsDownload) _onUploaded();
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => task.Status = UploadStatus.Cancelled);
        }
        catch (ApiException ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (task.Status == UploadStatus.Cancelled) return;
                task.Status = UploadStatus.Error;
                task.Error = ex.Message;
                task.Conflict = ex.IsConflict; // 下载不会 409，IsConflict 恒 false，不影响
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (task.Status == UploadStatus.Cancelled) return;
                task.Status = UploadStatus.Error;
                task.Error = (task.IsDownload ? "下载失败：" : "上传失败：") + ex.Message;
            });
        }
        finally
        {
            lock (_gate) _runningCount--;
            Pump();
        }

        return;

        void OnProgress(long sent, long total)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (task.Status == UploadStatus.Running)
                {
                    task.Sent = sent;
                    if (total > 0) task.Total = total; // 下载时 total 由响应头决定，未知则保持 0
                }
            });
        }
    }

    [RelayCommand]
    private static void Cancel(UploadTaskViewModel task)
    {
        if (task.Status == UploadStatus.Running)
        {
            task.Status = UploadStatus.Cancelled;
            task.Cts?.Cancel();
        }
        else if (task.Status == UploadStatus.Queued)
        {
            task.Status = UploadStatus.Cancelled;
        }
    }

    [RelayCommand]
    private void RetryOverwrite(UploadTaskViewModel task)
    {
        if (task.Status != UploadStatus.Error) return;
        task.Status = UploadStatus.Queued;
        task.Sent = 0;
        task.Error = null;
        task.Conflict = false;
        task.Overwrite = true;
        Pump();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        for (var i = _internal.Count - 1; i >= 0; i--)
        {
            var t = _internal[i];
            if (t.Status is UploadStatus.Done or UploadStatus.Cancelled)
            {
                _internal.RemoveAt(i);
                Tasks.Remove(t);
            }
        }
    }
}
