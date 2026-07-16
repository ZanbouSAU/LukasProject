// NasClientDesktop/ViewModels/FileRowViewModel.cs
// 目录列表中的一行，包裹 FileEntry 并提供展示用派生属性。

using CommunityToolkit.Mvvm.ComponentModel;
using NasClientDesktop.Models;
using NasClientDesktop.Services;

namespace NasClientDesktop.ViewModels;

public sealed partial class FileRowViewModel(FileEntry entry) : ViewModelBase
{
    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsDirectory => Entry.IsDirectory;
    public PreviewKind PreviewKind { get; } = Format.PreviewKindOf(entry.Name);

    public bool IsPreviewable => !IsDirectory && PreviewKind != PreviewKind.None;
    public bool IsText => PreviewKind == PreviewKind.Text;

    public string DisplayName => IsDirectory ? Name + "/" : Name;
    public string SizeText => IsDirectory ? "—" : Format.Size(Entry.Size);
    public string TimeText => Format.Time(Entry.ModifiedAtUtc);

    /// <summary>预览按钮文案：文本为「查看/编辑」，媒体为「预览」。</summary>
    public string PreviewActionText => PreviewKind == PreviewKind.Text ? "查看/编辑" : "预览";

    [ObservableProperty] private bool _downloading;
}
