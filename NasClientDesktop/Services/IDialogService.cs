// NasClientDesktop/Services/IDialogService.cs
// 视图提供的对话能力（文件/文件夹选择、保存位置、确认框）。
// VM 通过该接口请求，由 View（拥有 TopLevel.StorageProvider）实现，保持 VM 与 UI 解耦。

using System.Collections.Generic;
using System.Threading.Tasks;

namespace NasClientDesktop.Services;

public sealed record PickedFile(string LocalPath, string RelName, long Size);

public interface IDialogService
{
    /// <summary>选择一个或多个文件。返回空集合表示取消。</summary>
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(bool allowMultiple);

    /// <summary>选择一个文件夹，返回其中所有文件（保留相对路径）。空集合表示取消。</summary>
    Task<IReadOnlyList<PickedFile>> PickFolderAsync();

    /// <summary>选择单个 zip 文件。null 表示取消。</summary>
    Task<PickedFile?> PickZipAsync();

    /// <summary>选择保存位置（用于下载）。返回本地路径，null 表示取消。</summary>
    Task<string?> PickSavePathAsync(string suggestedName);

    /// <summary>选择保存目录（用于目录下载的 zip 落地）。null 表示取消。</summary>
    Task<string?> PickSaveFolderAsync();

    /// <summary>是/否确认框（用于覆盖提示等）。返回 true 表示用户确认。</summary>
    Task<bool> ConfirmAsync(string title, string message);
}
