using System.Windows;
using System.Windows.Controls;

namespace SRdeckPlugin.Wpf.Controls;

/// <summary>Hosts a plugin-specific detail template for the selected list item.</summary>
public partial class SelectionDetailPane : UserControl
{
    public static readonly DependencyProperty DetailContentProperty = DependencyProperty.Register(
        nameof(DetailContent), typeof(object), typeof(SelectionDetailPane));

    public static readonly DependencyProperty DetailContentTemplateProperty = DependencyProperty.Register(
        nameof(DetailContentTemplate), typeof(DataTemplate), typeof(SelectionDetailPane));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(SelectionDetailPane),
        new FrameworkPropertyMetadata("一覧から項目を選択すると詳細情報が表示されます"));

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

    public SelectionDetailPane() => InitializeComponent();
}
