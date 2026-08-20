using System.Windows;
using SRdeck.ViewModels;

namespace SRdeck.Views;

/// <summary>
/// MainWindow のキーボード入力イベント処理を定義する部分クラスです。
/// </summary>
public partial class MainWindow : Window
{
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_viewModel.IsHelpVisible && e.Key == System.Windows.Input.Key.Escape)
        {
            _viewModel.IsHelpVisible = false;
            e.Handled = true;
            return;
        }
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            long delta = 0;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Left:
                case System.Windows.Input.Key.H:
                    delta = -500_000;
                    break;
                case System.Windows.Input.Key.Right:
                case System.Windows.Input.Key.L:
                    delta = 500_000;
                    break;
                case System.Windows.Input.Key.Up:
                case System.Windows.Input.Key.K:
                    delta = 500_000;
                    break;
                case System.Windows.Input.Key.Down:
                case System.Windows.Input.Key.J:
                    delta = -500_000;
                    break;
            }
            if (delta != 0)
            {
                _viewModel.ShiftCenterFrequency(delta);
                e.Handled = true;
            }
        }
        else
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.T:
                    if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    {
                        _viewModel.AdvanceStep(1);
                    }
                    else
                    {
                        _viewModel.AdvanceStep(-1);
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.P:
                    if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    {
                        _viewModel.FinerSpan(2);
                    }
                    else
                    {
                        _viewModel.FinerSpan(1);
                    }
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.D:
                    if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    {
                        _viewModel.AdvanceDemodMode(2);
                    }
                    else
                    {
                        _viewModel.AdvanceDemodMode(1);
                    }
                    e.Handled = true;
                    break;
            }
        }
    }
}
