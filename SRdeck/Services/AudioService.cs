using SRdeck.Audio;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IAudioService
{
    int BufferedBytes { get; }
    int PlaybackSampleRateHz { get; }
    string? CurrentPlaybackFileName { get; }
    void InitializeOutput(int sampleRate, int channels);
    bool OpenPlayback(string filePath, double startSeconds);
    void ClosePlayback();
    void PlayOutput();
    void StopOutput();
    Task RunPlaybackAsync(PlaybackProcessingRequest request);
    void Shutdown();
}

public sealed class AudioService : IAudioService
{
    private readonly IPlaybackProcessor _playbackProcessor;
    private readonly IAudioOutput _output;
    private readonly IAudioFileReader _fileReader;

    public AudioService(
        IAudioOutput output,
        IAudioFileReader fileReader,
        IPlaybackProcessor playbackProcessor)
    {
        _output = output;
        _fileReader = fileReader;
        _playbackProcessor = playbackProcessor;
    }

    public int BufferedBytes => _output.GetBufferedBytes();
    public int PlaybackSampleRateHz => _fileReader.CurrentSampleRateHz;
    public string? CurrentPlaybackFileName => _fileReader.CurrentFileName;

    public void InitializeOutput(int sampleRate, int channels) =>
        _output.Initialize(sampleRate, channels);

    public bool OpenPlayback(string filePath, double startSeconds) =>
        _fileReader.Open(filePath, startSeconds);

    public void ClosePlayback() => _fileReader.Close();

    public void PlayOutput() => _output.Play();

    public void StopOutput() => _output.Stop();

    public Task RunPlaybackAsync(PlaybackProcessingRequest request) =>
        _playbackProcessor.RunAsync(request);

    public void Shutdown()
    {
        _output.Stop();
        _output.ClearBuffer();
        _output.Dispose();
    }
}
