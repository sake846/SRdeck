namespace SRdeck.Services;

internal interface IApplicationStartupProgress
{
    void Report(string status);
}

internal sealed class NullApplicationStartupProgress : IApplicationStartupProgress
{
    public static NullApplicationStartupProgress Instance { get; } = new();

    private NullApplicationStartupProgress() { }

    public void Report(string status) { }
}
