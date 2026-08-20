using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace SRdeck.Behaviors
{
    public class MouseEventToCommandBehavior : TriggerAction<UIElement>
    {
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register("Command", typeof(ICommand), typeof(MouseEventToCommandBehavior), new PropertyMetadata(null));

        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public bool CaptureMouseOnEvent { get; set; }
        public bool ReleaseMouseCaptureOnEvent { get; set; }
        public bool PassMouseButtonInfo { get; set; }

        protected override void Invoke(object parameter)
        {
            if (parameter is MouseEventArgs mouseEventArgs && AssociatedObject is IInputElement element)
            {
                var point = mouseEventArgs.GetPosition(element);
                
                object commandArgument = point;
                if (PassMouseButtonInfo && mouseEventArgs is MouseButtonEventArgs mouseButtonEventArgs)
                {
                    commandArgument = new Tuple<Point, MouseButton>(point, mouseButtonEventArgs.ChangedButton);
                }

                if (CaptureMouseOnEvent)
                {
                    AssociatedObject.CaptureMouse();
                }

                if (ReleaseMouseCaptureOnEvent)
                {
                    AssociatedObject.ReleaseMouseCapture();
                }
                
                if (Command?.CanExecute(commandArgument) == true)
                {
                    Command.Execute(commandArgument);
                }
            }
        }
    }
}
