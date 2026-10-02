using System.Diagnostics;

namespace SRdeck.SDR;

/// <summary>
/// Keeps a native stream handle alive until both cancellation and the reader task
/// have completed. A bounded caller wait must not decide native resource lifetime.
/// </summary>
internal static class NativeStreamShutdown
{
    internal static Task Begin(
        Action requestStop,
        Task? reader,
        Action release,
        Action<Exception> reportFailure) =>
        BeginAsync(() =>
        {
            requestStop();
            return Task.CompletedTask;
        }, reader, release, reportFailure);

    internal static Task BeginAsync(
        Func<Task> requestStop,
        Task? reader,
        Action release,
        Action<Exception> reportFailure) =>
        Task.Run(async () =>
        {
            try
            {
                await requestStop().ConfigureAwait(false);
                if (reader != null)
                {
                    try
                    {
                        await reader.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceWarning($"Native reader exited with an error: {exception.Message}");
                    }
                }

                release();
            }
            catch (Exception exception)
            {
                // Keep the handle owned by the controller so a later Stop can retry.
                reportFailure(exception);
            }
        });
}
