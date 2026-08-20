using System;
using System.Threading;

namespace SRdeck.Models;

public interface IInputSessionStateMachine
{
    InputSessionState Current { get; }
    bool IsPlaying { get; }
    bool IsSdrRunning { get; }
    bool TryStart(InputSessionState targetState);
    void Stop(InputSessionState expectedState);
    void MarkDisposed();
}

public sealed class InputSessionStateMachine : IInputSessionStateMachine
{
    private int _current = (int)InputSessionState.Stopped;

    public InputSessionState Current => (InputSessionState)Volatile.Read(ref _current);
    public bool IsPlaying => Current == InputSessionState.PlayingFile;
    public bool IsSdrRunning => Current == InputSessionState.ReceivingSdr;

    public bool TryStart(InputSessionState targetState)
    {
        if (targetState is not (InputSessionState.ReceivingSdr or InputSessionState.PlayingFile))
        {
            throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "Only active input session states can be started.");
        }

        while (true)
        {
            InputSessionState current = Current;
            if (current == targetState) return true;
            if (current != InputSessionState.Stopped) return false;
            if (Interlocked.CompareExchange(
                    ref _current,
                    (int)targetState,
                    (int)InputSessionState.Stopped) == (int)InputSessionState.Stopped)
            {
                return true;
            }
        }
    }

    public void Stop(InputSessionState expectedState)
    {
        Interlocked.CompareExchange(
            ref _current,
            (int)InputSessionState.Stopped,
            (int)expectedState);
    }

    public void MarkDisposed()
    {
        Interlocked.Exchange(ref _current, (int)InputSessionState.Disposed);
    }
}
