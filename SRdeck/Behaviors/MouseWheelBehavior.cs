using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;
using SRdeck.Models;

namespace SRdeck.Behaviors
{
    public class MouseWheelBehavior : Behavior<FrameworkElement>
    {
        public static readonly DependencyProperty HorizontalDeltaCommandProperty =
            DependencyProperty.Register(nameof(HorizontalDeltaCommand), typeof(ICommand), typeof(MouseWheelBehavior));

        public ICommand? HorizontalDeltaCommand
        {
            get => (ICommand?)GetValue(HorizontalDeltaCommandProperty);
            set => SetValue(HorizontalDeltaCommandProperty, value);
        }

        public static readonly DependencyProperty VerticalDeltaCommandProperty =
            DependencyProperty.Register(nameof(VerticalDeltaCommand), typeof(ICommand), typeof(MouseWheelBehavior));

        public ICommand? VerticalDeltaCommand
        {
            get => (ICommand?)GetValue(VerticalDeltaCommandProperty);
            set => SetValue(VerticalDeltaCommandProperty, value);
        }

        public double HorizontalMultiplier { get; set; } = 1.0;
        public double VerticalMultiplier { get; set; } = 1.0;
        public bool PassPositionTuple { get; set; }

        public static readonly DependencyProperty CtrlVerticalDeltaCommandProperty =
            DependencyProperty.Register(nameof(CtrlVerticalDeltaCommand), typeof(ICommand), typeof(MouseWheelBehavior));

        public ICommand? CtrlVerticalDeltaCommand
        {
            get => (ICommand?)GetValue(CtrlVerticalDeltaCommandProperty);
            set => SetValue(CtrlVerticalDeltaCommandProperty, value);
        }

        private HwndSource? _hwndSource;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.Loaded += AssociatedObject_Loaded;
            AssociatedObject.Unloaded += AssociatedObject_Unloaded;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject.Loaded -= AssociatedObject_Loaded;
            AssociatedObject.Unloaded -= AssociatedObject_Unloaded;
            RemoveHook();
        }

        private void AssociatedObject_Loaded(object sender, RoutedEventArgs e)
        {
            AddHook();
        }

        private void AssociatedObject_Unloaded(object sender, RoutedEventArgs e)
        {
            RemoveHook();
        }

        private void AddHook()
        {
            if (_hwndSource == null)
            {
                _hwndSource = PresentationSource.FromVisual(AssociatedObject) as HwndSource;
                _hwndSource?.AddHook(WndProc);
            }
        }

        private void RemoveHook()
        {
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != AppConstants.WM_MOUSEHWHEEL && msg != AppConstants.WM_MOUSEWHEEL)
            {
                return IntPtr.Zero;
            }

            if (!AssociatedObject.IsVisible || PresentationSource.FromVisual(AssociatedObject) == null)
            {
                return IntPtr.Zero;
            }

            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            int screenX = (short)(lParam.ToInt64() & 0xFFFF);
            int screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

            Point windowPoint = AssociatedObject.PointFromScreen(new Point(screenX, screenY));

            if (windowPoint.X < 0 || windowPoint.X > AssociatedObject.ActualWidth ||
                windowPoint.Y < 0 || windowPoint.Y > AssociatedObject.ActualHeight)
            {
                return IntPtr.Zero;
            }

            if (!AssociatedObject.IsMouseOver)
            {
                return IntPtr.Zero;
            }

            if (msg == AppConstants.WM_MOUSEHWHEEL && HorizontalDeltaCommand != null)
            {
                double scaledDeltaX = delta * HorizontalMultiplier;
                object commandArgument = PassPositionTuple 
                    ? new Tuple<Vector, Point>(new Vector(scaledDeltaX, 0), windowPoint) 
                    : new Vector(scaledDeltaX, 0);

                if (HorizontalDeltaCommand.CanExecute(commandArgument))
                {
                    HorizontalDeltaCommand.Execute(commandArgument);
                    handled = true;
                }
            }
            else if (msg == AppConstants.WM_MOUSEWHEEL && (VerticalDeltaCommand != null || CtrlVerticalDeltaCommand != null))
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && CtrlVerticalDeltaCommand != null)
                {
                    object ctrlCommandArgument = new Tuple<Vector, Point, double>(new Vector(0, -delta), windowPoint, AssociatedObject.ActualWidth);
                    if (CtrlVerticalDeltaCommand.CanExecute(ctrlCommandArgument))
                    {
                        CtrlVerticalDeltaCommand.Execute(ctrlCommandArgument);
                        handled = true;
                        return IntPtr.Zero;
                    }

                    return IntPtr.Zero;
                }

                if (VerticalDeltaCommand == null)
                {
                    return IntPtr.Zero;
                }

                double scaledDeltaY = delta * VerticalMultiplier;
                object commandArgument = PassPositionTuple 
                    ? new Tuple<Vector, Point>(new Vector(0, scaledDeltaY), windowPoint) 
                    : new Vector(0, scaledDeltaY);

                if (VerticalDeltaCommand.CanExecute(commandArgument))
                {
                    VerticalDeltaCommand.Execute(commandArgument);
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private bool IsMouseDirectlyOverAssociatedObject(Point screenPoint)
        {
            var window = Window.GetWindow(AssociatedObject);
            if (window == null)
            {
                return true;
            }

            Point windowPoint = window.PointFromScreen(screenPoint);
            HitTestResult? result = VisualTreeHelper.HitTest(window, windowPoint);
            if (result?.VisualHit is not DependencyObject hit)
            {
                return true;
            }

            while (hit != null)
            {
                if (ReferenceEquals(hit, AssociatedObject))
                {
                    return true;
                }

                hit = VisualTreeHelper.GetParent(hit);
            }

            return false;
        }
    }
}
