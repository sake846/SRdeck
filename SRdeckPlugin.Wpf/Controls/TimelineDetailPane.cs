using System.Windows;
using System.Windows.Controls;

namespace SRdeckPlugin.Wpf.Controls;

/// <summary>Hosts the plugin-specific detail template for a selected timeline event.</summary>
public partial class TimelineDetailPane : UserControl
{
    public static readonly DependencyProperty DetailContentProperty = DependencyProperty.Register(
        nameof(DetailContent), typeof(object), typeof(TimelineDetailPane));

    public static readonly DependencyProperty DetailContentTemplateProperty = DependencyProperty.Register(
        nameof(DetailContentTemplate), typeof(DataTemplate), typeof(TimelineDetailPane));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(TimelineDetailPane),
        new FrameworkPropertyMetadata("時系列の項目を選択すると詳細情報が表示されます"));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public object? DetailContent
    {
        get => GetValue(DetailContentProperty);
        set => SetValue(DetailContentProperty, value);
    }

    public DataTemplate? DetailContentTemplate
    {
        get => (DataTemplate?)GetValue(DetailContentTemplateProperty);
        set => SetValue(DetailContentTemplateProperty, value);
    }

    public TimelineDetailPane() => InitializeComponent();
}
