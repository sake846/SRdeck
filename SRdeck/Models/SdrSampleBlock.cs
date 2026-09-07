namespace SRdeck.Models;

public readonly record struct SdrSampleMetadata(
    long Sequence,
    long AbsoluteSampleStart,
    bool IsDiscontinuous = false,
    long DroppedSamplesBefore = 0);

/// <summary>Sample arrays are valid only for the duration of the callback.</summary>
public readonly record struct SdrSampleBlock(
    short[] SamplesI, short[] SamplesQ, uint SampleCount, SdrSampleMetadata Metadata);

/// <summary>
/// Optional device capability. Hosts subscribe to this event instead of the
/// legacy SamplesReceived event when they can preserve input continuity.
/// </summary>
public interface ISdrSampleBlockSource
{
    event Action<SdrSampleBlock>? SampleBlockReceived;
}
