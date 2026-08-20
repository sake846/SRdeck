using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SRdeck.Behaviors;

/// <summary>
/// Applies a consistent, restrained vertical mouse-wheel speed to every
/// scrollable WPF surface in the application.
/// </summary>
public static class ComfortableMouseWheelBehavior
{
    private const double WheelDeltaPerDetent = 120.0;
    private const double PixelsPerDetent = 24.0;
    private static bool _enabled;

    private static readonly DependencyProperty PendingLogicalDeltaProperty = DependencyProperty.RegisterAttached(
        "PendingLogicalDelta",
        typeof(double),
        typeof(ComfortableMouseWheelBehavior),
        new PropertyMetadata(0.0));

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ScrollViewer viewer ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            !ReferenceEquals(viewer, FindNearestScrollableViewer(
                args.OriginalSource as DependencyObject,
                args.Delta)))
        {
            return;
        }

        if (viewer.CanContentScroll)
        {
            ScrollLogicalLine(viewer, args.Delta);
        }
        else
        {
            double target = Math.Clamp(
                viewer.VerticalOffset - args.Delta / WheelDeltaPerDetent * PixelsPerDetent,
                0.0,
                viewer.ScrollableHeight);
            viewer.ScrollToVerticalOffset(target);
        }

        args.Handled = true;
    }

    private static void ScrollLogicalLine(ScrollViewer viewer, int delta)
    {
        double pending = (double)viewer.GetValue(PendingLogicalDeltaProperty) + delta;
        while (Math.Abs(pending) >= WheelDeltaPerDetent)
        {
            if (pending > 0)
            {
                viewer.LineUp();
                pending -= WheelDeltaPerDetent;
            }
            else
            {
                viewer.LineDown();
                pending += WheelDeltaPerDetent;
            }
        }

        viewer.SetValue(PendingLogicalDeltaProperty, pending);
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        viewer.ScrollableHeight > 0.0 &&
        (delta > 0 ? viewer.VerticalOffset > 0.0 : viewer.VerticalOffset < viewer.ScrollableHeight);

    private static ScrollViewer? FindNearestScrollableViewer(DependencyObject? source, int delta)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is ScrollViewer viewer && CanScroll(viewer, delta))
            {
                return viewer;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
