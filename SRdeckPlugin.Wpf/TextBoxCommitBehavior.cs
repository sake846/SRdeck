using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SRdeckPlugin.Wpf;

/// <summary>
/// Commits bound text when editing finishes, consistently across host and plugin views.
/// </summary>
public static class TextBoxCommitBehavior
{
    private static int _isEnabled;

    /// <summary>
    /// Enables the behavior for every WPF <see cref="TextBox"/> in the current process.
    /// Calling this method more than once has no effect.
    /// </summary>
    public static void Enable()
    {
        if (Interlocked.Exchange(ref _isEnabled, 1) != 0)
        {
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(TextBox),
            Keyboard.KeyDownEvent,
            new KeyEventHandler(OnKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus),
            handledEventsToo: true);
    }

    /// <summary>
    /// Updates the source of the Text binding, if the text box has one.
    /// </summary>
    public static bool Commit(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);

        if (textBox.IsReadOnly)
        {
            return false;
        }

        var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
        if (bindingExpression is null)
        {
            return false;
        }

        bindingExpression.UpdateSource();
        return true;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox ||
            (e.Key != Key.Enter && e.Key != Key.Return) ||
            textBox.AcceptsReturn)
        {
            return;
        }

        // Do not handle the event. Frequency fields and other specialized inputs
        // may still need to process Enter after their binding has been committed.
        Commit(textBox);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            Commit(textBox);
        }
    }
}
