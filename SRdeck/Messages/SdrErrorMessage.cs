namespace SRdeck.Messages;

public class SdrErrorMessage(string message)
{
    public string Message { get; } = message;
}
