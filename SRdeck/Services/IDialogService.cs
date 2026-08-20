namespace SRdeck.Services;

public interface IDialogService
{
    string? ShowOpenFileDialog(string filter = "All Files|*.*");
    void ShowMessage(string message, string title);
}
