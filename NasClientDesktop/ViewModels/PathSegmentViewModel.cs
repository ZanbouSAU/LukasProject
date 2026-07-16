// NasClientDesktop/ViewModels/PathSegmentViewModel.cs
// 路径提示符中的一段（面包屑），点击导航到该层级。

namespace NasClientDesktop.ViewModels;

public sealed class PathSegmentViewModel(string label, string target)
{
    public string Label { get; } = label;

    /// <summary>点击后导航到的目标相对路径。</summary>
    public string Target { get; } = target;
}
