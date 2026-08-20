namespace SRdeck.Services;

public interface IRadioProcessingPipeline
{
    bool TryRun(Func<bool> canRun, Action runCycle);
    void StopAndWait();
}

public sealed class RadioProcessingPipeline : IRadioProcessingPipeline
{
    private readonly IProcessingCycleCoordinator _cycleCoordinator;

    public RadioProcessingPipeline(IProcessingCycleCoordinator cycleCoordinator)
    {
        _cycleCoordinator = cycleCoordinator;
    }

    public bool TryRun(Func<bool> canRun, Action runCycle) =>
        _cycleCoordinator.TryRun(canRun, runCycle);

    public void StopAndWait() => _cycleCoordinator.StopAndWait();
}
