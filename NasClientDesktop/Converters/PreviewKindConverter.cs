// NasClientDesktop/Converters/PreviewKindConverter.cs

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using NasClientDesktop.Services;

namespace NasClientDesktop.Converters;

/// <summary>把 PreviewKind 转成动作文案（"查看/编辑" 或 "预览"）。当前未直接用于绑定，保留以备扩展。</summary>
public sealed class PreviewKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PreviewKind kind)
            return kind == PreviewKind.Text ? "查看/编辑" : "预览";
        return "预览";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
