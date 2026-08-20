using Microsoft.Win32;
using System.Windows;

namespace SRdeck.Services;

public class WindowsDialogService : IDialogService
{
    public string? ShowOpenFileDialog(string filter = "All Files|*.*")
    {
        OpenFileDialog openFileDialog = new()
        {
            Filter = filter
        };

        if (openFileDialog.ShowDialog() == true)
        {
            return openFileDialog.FileName;
        }
        return null;
    }

    public void ShowMessage(string message, string title)
    {
        if (Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel mainViewModel)
        {
            mainViewModel.CommonOverlayTitle = title;
            mainViewModel.CommonOverlayMessageText = message;
            mainViewModel.CommonOverlayVisibility = Visibility.Visible;
            System.Media.SystemSounds.Asterisk.Play();
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
