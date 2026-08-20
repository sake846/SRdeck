using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using Microsoft.Xaml.Behaviors;
using SRdeck.Models;

namespace SRdeck.Behaviors
{
    public class ScrollViewerDragBehavior : Behavior<ScrollViewer>
    {
        private Point? _scrollStartPoint;
        private Point _scrollStartOffset;
        private System.Windows.Interop.HwndSource? _hwndSource;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.PreviewMouseLeftButtonDown += AssociatedObject_PreviewMouseLeftButtonDown;
            AssociatedObject.PreviewMouseMove += AssociatedObject_PreviewMouseMove;
            AssociatedObject.PreviewMouseLeftButtonUp += AssociatedObject_PreviewMouseLeftButtonUp;
            AssociatedObject.Loaded += AssociatedObject_Loaded;
            AssociatedObject.Unloaded += AssociatedObject_Unloaded;
        }

        private void AssociatedObject_Loaded(object sender, RoutedEventArgs e)
        {
            _hwndSource = PresentationSource.FromVisual(AssociatedObject) as System.Windows.Interop.HwndSource;
            _hwndSource?.AddHook(WndProc);
        }

        private void AssociatedObject_Unloaded(object sender, RoutedEventArgs e)
        {
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }

        private IntPtr WndProc(IntPtr windowHandle, int messageId, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (messageId == AppConstants.WM_MOUSEHWHEEL && AssociatedObject != null && AssociatedObject.IsMouseOver)
            {
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                AssociatedObject.ScrollToHorizontalOffset(AssociatedObject.HorizontalOffset + delta);
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnDetaching()
        {
            AssociatedObject.PreviewMouseLeftButtonDown -= AssociatedObject_PreviewMouseLeftButtonDown;
            AssociatedObject.PreviewMouseMove -= AssociatedObject_PreviewMouseMove;
            AssociatedObject.PreviewMouseLeftButtonUp -= AssociatedObject_PreviewMouseLeftButtonUp;
            AssociatedObject.Loaded -= AssociatedObject_Loaded;
            AssociatedObject.Unloaded -= AssociatedObject_Unloaded;
            
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;

            base.OnDetaching();
        }

        private void AssociatedObject_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs mouseButtonEventArgs)
        {
            if (mouseButtonEventArgs.OriginalSource is DependencyObject element && IsDescendantOfIgnoredControl(element))
            {
                return;
            }

            _scrollStartPoint = mouseButtonEventArgs.GetPosition(AssociatedObject);
            _scrollStartOffset = new Point(AssociatedObject.HorizontalOffset, AssociatedObject.VerticalOffset);
        }

        private bool IsDescendantOfIgnoredControl(DependencyObject element)
        {
            var current = element as FrameworkElement;
            while (current != null)
            {
                if (current is ScrollBar || 
                    current is Slider ||
                    current is Thumb ||
                    current is ButtonBase)
                {
                    return true;
                }
                
                if (current.TemplatedParent is FrameworkElement templatedParent) 
                {
                    current = templatedParent;
                    continue;
                }

                DependencyObject parentObject = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
                current = parentObject as FrameworkElement;
            }
            return false;
        }

        private void AssociatedObject_PreviewMouseMove(object sender, MouseEventArgs mouseEventArgs)
        {
            if (_scrollStartPoint.HasValue)
            {
                Point currentPoint = mouseEventArgs.GetPosition(AssociatedObject);
                Vector delta = currentPoint - _scrollStartPoint.Value;

                if (AssociatedObject.IsMouseCaptured)
                {
                    AssociatedObject.ScrollToHorizontalOffset(_scrollStartOffset.X - delta.X);
                    mouseEventArgs.Handled = true;
                }
                else if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance ||
                         Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    AssociatedObject.CaptureMouse();
                }
            }
        }

        private void AssociatedObject_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs mouseButtonEventArgs)
        {
            if (AssociatedObject.IsMouseCaptured)
            {
                AssociatedObject.ReleaseMouseCapture();
            }
            _scrollStartPoint = null;
        }
    }
}
