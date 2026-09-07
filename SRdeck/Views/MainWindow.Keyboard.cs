using System.Windows;
using System.Windows.Input;

namespace SRdeck.Views;

/// <summary>
/// MainWindow のキーボード入力イベント処理を定義する部分クラスです。
/// </summary>
public partial class MainWindow : Window
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_viewModel.IsHelpVisible && e.Key == Key.Escape)
        {
            _viewModel.IsHelpVisible = false;
            e.Handled = true;
        }
    }
}
