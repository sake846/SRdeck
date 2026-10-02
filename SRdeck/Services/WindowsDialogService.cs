using Microsoft.Win32;
using SRdeck.Audio;
using System.IO;
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

    public IqPlaybackSelection? ShowIqPlaybackDialog()
    {
        string? filePath = ShowOpenFileDialog("IQ WAV (*.wav)|*.wav|すべてのファイル (*.*)|*.*");
        if (filePath is null) return null;

        IqPlaybackFileInfo fileInfo;
        try
        {
            fileInfo = IqPlaybackFileInspector.Inspect(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage(exception.Message, "IQ WAVを開けません");
            return null;
        }

        var dialog = new Views.IqPlaybackDialog(filePath, fileInfo)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true
            ? new(filePath, dialog.ResultCenterFrequencyHz, dialog.ResultStartSeconds)
            : null;
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
