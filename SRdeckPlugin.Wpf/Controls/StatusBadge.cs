using System.Windows;
using System.Windows.Controls;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Wpf.Controls;

public class StatusBadge : Control
{
    public static readonly DependencyProperty StatusKindProperty =
        DependencyProperty.Register(
            nameof(StatusKind),
            typeof(OverallStatusKind),
            typeof(StatusBadge),
            new FrameworkPropertyMetadata(OverallStatusKind.Idle, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText),
            typeof(string),
            typeof(StatusBadge),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public OverallStatusKind StatusKind
    {
        get => (OverallStatusKind)GetValue(StatusKindProperty);
        set => SetValue(StatusKindProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    static StatusBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(typeof(StatusBadge)));
    }
}
