// NasClientDesktop/Views/StorageDialogService.cs
// 用 Avalonia 的 StorageProvider 实现文件/文件夹选择与保存位置。
// 文件夹上传时用 NasLib 遍历目录、保留相对路径结构（webkitRelativePath 的桌面等价物）。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NasClientDesktop.Services;
using Lukas.Std;

namespace NasClientDesktop.Views;

public sealed class StorageDialogService(TopLevel top) : IDialogService
{
    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(bool allowMultiple)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = allowMultiple,
        });

        var result = new List<PickedFile>();
        foreach (var f in files)
        {
            var local = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(local)) continue;
            result.Add(new PickedFile(local, f.Name, SafeLength(local)));
        }
        return result;
    }

    public async Task<IReadOnlyList<PickedFile>> PickFolderAsync()
    {
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要上传的文件夹",
            AllowMultiple = false,
        });

        var result = new List<PickedFile>();
        foreach (var folder in folders)
        {
            var root = folder.TryGetLocalPath();
            if (string.IsNullOrEmpty(root)) continue;

            var rootName = Io.File.GetFileName(root); // 顶层文件夹名，作为相对路径前缀
            WalkDirectory(root, rootName, result);
        }
        return result;
    }

    public async Task<PickedFile?> PickZipAsync()
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 zip 文件",
            AllowMultiple = false,
            FileTypeFilter = 
            [
                new FilePickerFileType("Zip 压缩包") { Patterns = ["*.zip"] }
            ],
        });
        if (files.Count == 0) return null;
        var local = files[0].TryGetLocalPath();
        return string.IsNullOrEmpty(local) ? null : new PickedFile(local, files[0].Name, SafeLength(local));
    }

    public async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存到",
            SuggestedFileName = suggestedName,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFolderAsync()
    {
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择保存目录",
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var yes = new Button { Content = "确定", IsDefault = true, MinWidth = 80 };
        var no = new Button { Content = "取消", IsCancel = true, MinWidth = 80 };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 380,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { no, yes },
                    },
                },
            },
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        if (top is Window owner)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();

        return await tcs.Task;
    }

    // ---------------------------------------------------------------- 目录遍历（经 NasLib）

    /// <summary>
    /// 递归遍历本地目录，把每个文件加入结果，相对路径以 <paramref name="relPrefix"/> 开头（'/' 分隔）。
    /// 用 NasLib 的目录枚举。
    /// </summary>
    private static void WalkDirectory(string absDir, string relPrefix, List<PickedFile> result)
    {
        foreach (var (name, isDir) in EnumerateDir(absDir))
        {
            if (name is "." or "..") continue;
            var childAbs = Io.Path.Combine(absDir, name);
            var childRel = relPrefix.Length == 0 ? name : relPrefix + "/" + name;
            if (isDir)
                WalkDirectory(childAbs, childRel, result);
            else
                result.Add(new PickedFile(childAbs, childRel, SafeLength(childAbs)));
        }
    }

    /// <summary>用 NasLib 列出目录项。返回 (名称, 是否目录)。</summary>
    private static IEnumerable<(string name, bool isDir)> EnumerateDir(string absDir)
        => NasDirectory.Enumerate(absDir);

    private static long SafeLength(string path)
    {
        try { return Io.File.GetFileLength(path.AsSpan()); }
        catch { return 0; }
    }
}
