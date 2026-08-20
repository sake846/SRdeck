using System.Diagnostics;
using System.IO;
using System.Media;

namespace SRdeck.Services.Plugins;

public sealed class PluginNotificationService : SRdeckPlugin.Contracts.IPluginNotificationService
{
    private static readonly long MinimumAlarmIntervalTicks = Stopwatch.Frequency;
    private static readonly Lazy<byte[]?> ReceptionAlarmWav = new(CreateReceptionAlarmWav);
    private static readonly Lazy<byte[]?> ShortReceptionAlarmWav = new(CreateShortReceptionAlarmWav);
    private int _shortAlarmInProgress;
    private long _nextAlarmAllowedTicks;

    public void PlayReceptionAlarm(TimeSpan delay = default)
    {
        long now = Stopwatch.GetTimestamp();
        while (true)
        {
            long nextAllowed = Volatile.Read(ref _nextAlarmAllowedTicks);
            if (now < nextAllowed) return;
            if (Interlocked.CompareExchange(ref _nextAlarmAllowedTicks,
                    now + MinimumAlarmIntervalTicks, nextAllowed) == nextAllowed)
                break;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                if (TryPlayGeneratedAlarm())
                {
                    return;
                }

                PlayFallbackAlarm();
            }
            catch
            {
                // Notifications must never interrupt reception processing.
            }
        });
    }

    public void PlayShortReceptionAlarm(TimeSpan delay = default)
    {
        if (Interlocked.CompareExchange(ref _shortAlarmInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                if (TryPlayGeneratedAlarm(ShortReceptionAlarmWav.Value))
                {
                    return;
                }

                PlayShortFallbackAlarm();
            }
            catch
            {
                // Notifications must never interrupt reception processing.
            }
            finally
            {
                Volatile.Write(ref _shortAlarmInProgress, 0);
            }
        });
    }

    private static bool TryPlayGeneratedAlarm()
    {
        return TryPlayGeneratedAlarm(ReceptionAlarmWav.Value);
    }

    private static bool TryPlayGeneratedAlarm(byte[]? wavData)
    {
        if (wavData is not byte[])
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(wavData, writable: false);
            using var player = new SoundPlayer(stream);
            player.PlaySync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PlayShortFallbackAlarm()
    {
        try
        {
            Console.Beep(440, 40);
            Console.Beep(622, 40);
        }
        catch
        {
            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch
            {
            }
        }
    }

    private static void PlayFallbackAlarm()
    {
        try
        {
            Console.Beep(440, 120);
            Console.Beep(622, 120);
        }
        catch
        {
            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch
            {
            }
        }
    }

    private static byte[]? CreateReceptionAlarmWav()
    {
        return CreateAlarmWav(250);
    }

    private static byte[]? CreateShortReceptionAlarmWav()
    {
        return CreateAlarmWav(80);
    }

    private static byte[]? CreateAlarmWav(int durationMilliseconds)
    {
        const double frequency1 = 440;
        const double frequency2 = 622;
        const int sampleRate = 44100;

        try
        {
            int sampleCount = (int)(sampleRate * (durationMilliseconds / 1000.0));
            short[] samples = new short[sampleCount];

            const double attackMilliseconds = 5.0;
            const double burstMilliseconds = 25.0;
            const double releaseMilliseconds = 12.0;

            for (int index = 0; index < sampleCount; index++)
            {
                double time = (double)index / sampleRate;
                double timeMilliseconds = time * 1000.0;
                double envelope;

                if (timeMilliseconds < attackMilliseconds)
                {
                    envelope = (timeMilliseconds / attackMilliseconds) * 1.18;
                }
                else if (timeMilliseconds < burstMilliseconds)
                {
                    double progress = (timeMilliseconds - attackMilliseconds) /
                                      (burstMilliseconds - attackMilliseconds);
                    envelope = 1.18 - progress * 0.23;
                }
                else if (timeMilliseconds < durationMilliseconds - releaseMilliseconds)
                {
                    envelope = 0.95;
                }
                else
                {
                    double progress = Math.Max(
                        0.0,
                        (durationMilliseconds - timeMilliseconds) / releaseMilliseconds);
                    envelope = progress * progress * 0.95;
                }

                double raw = 0.5 * Math.Sin(2 * Math.PI * frequency1 * time) +
                             0.5 * Math.Sin(2 * Math.PI * frequency2 * time);
                double saturated = Math.Tanh(raw * 1.3);
                samples[index] = (short)(saturated * envelope * 16000);
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write(36 + sampleCount * sizeof(short));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(sampleCount * sizeof(short));
            foreach (short sample in samples)
            {
                writer.Write(sample);
            }

            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
