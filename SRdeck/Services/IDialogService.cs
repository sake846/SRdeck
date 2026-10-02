namespace SRdeck.Services;

public sealed record IqPlaybackSelection(string FilePath, int CenterFrequencyHz, double StartSeconds);

public interface IDialogService
{
    string? ShowOpenFileDialog(string filter = "All Files|*.*");
    IqPlaybackSelection? ShowIqPlaybackDialog();
    void ShowMessage(string message, string title);
}
