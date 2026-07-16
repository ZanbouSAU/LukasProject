// NasClientDesktop/Views/FileBrowserView.axaml.cs

// Avalonia 11.3 将 DragEventArgs.Data / DataFormats.Files 标记为过时（建议改用 DataTransfer /
// DataFormat.File）。旧 API 在 11.3 仍可正常工作，这里沿用并局部抑制 CS0618，待统一升级到新拖放 API。
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NasClientDesktop.Services;
using NasClientDesktop.ViewModels;
using Lukas.Std;

namespace NasClientDesktop.Views;

public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // 当承载的浏览器 VM 就绪时，注入对话能力（文件选择/保存）。
        if (DataContext is FileBrowserViewModel { Dialogs: null } vm)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not null)
                vm.Dialogs = new StorageDialogService(top);
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm) return;
        if (!e.Data.Contains(DataFormats.Files)) return;

        var items = e.Data.GetFiles();
        if (items is null) return;

        var picked = new List<PickedFile>();
        foreach (var item in items)
        {
            var local = item.TryGetLocalPath();
            if (string.IsNullOrEmpty(local)) continue;

            // 拖入的可能是文件或文件夹；文件夹则递归展开保留相对路径。
            if (Io.Directory.Exists(local.AsSpan()))
            {
                var rootName = Io.File.GetFileName(local);
                WalkInto(local, rootName, picked);
            }
            else
            {
                long size;
                try { size = Io.File.GetFileLength(local.AsSpan()); } catch { size = 0; }
                picked.Add(new PickedFile(local, item.Name, size));
            }
        }
        if (picked.Count > 0) vm.DropLocalFiles(picked);
    }

    private static void WalkInto(string absDir, string relPrefix, List<PickedFile> result)
    {
        foreach (var (name, isDir) in NasDirectory.Enumerate(absDir))
        {
            if (name is "." or "..") continue;
            var childAbs = Io.Path.Combine(absDir, name);
            var childRel = relPrefix.Length == 0 ? name : relPrefix + "/" + name;
            if (isDir) WalkInto(childAbs, childRel, result);
            else
            {
                long size;
                try { size = Io.File.GetFileLength(childAbs.AsSpan()); } catch { size = 0; }
                result.Add(new PickedFile(childAbs, childRel, size));
            }
        }
    }
}
