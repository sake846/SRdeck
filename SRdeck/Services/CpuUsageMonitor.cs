using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SRdeck.Services;

public interface ICpuUsageMonitor
{
    CpuUsageSnapshot GetUsage();
}

public readonly record struct CpuUsageSnapshot(double AppUsagePercent, double TotalUsagePercent);

public sealed class CpuUsageMonitor : ICpuUsageMonitor
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    private long _lastIdleTime;
    private long _lastSystemTime;
    private long _lastAppTicks;
    private long _lastRealTimeTicks;
    private CpuUsageSnapshot _lastUsage;
    private readonly object _sync = new();
    private readonly int _processorCount = Environment.ProcessorCount;

    public CpuUsageMonitor()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            _lastIdleTime = idle;
            _lastSystemTime = kernel + user;
        }
        _lastAppTicks = GetAppProcessorTicks();
        _lastRealTimeTicks = DateTime.UtcNow.Ticks;
    }

    public CpuUsageSnapshot GetUsage()
    {
        lock (_sync)
        {
            return GetUsageCore();
        }
    }

    private CpuUsageSnapshot GetUsageCore()
    {
        long now = DateTime.UtcNow.Ticks;
        // TimeSpan.Ticks per millisecond is 10,000
        if (_lastRealTimeTicks != 0 && now - _lastRealTimeTicks < 500 * 10000)
        {
            return _lastUsage;
        }

        double totalUsage = 0.0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long systemTime = kernel + user;
            long systemDelta = systemTime - _lastSystemTime;
            long idleDelta = idle - _lastIdleTime;

            if (systemDelta > 0)
            {
                totalUsage = (systemDelta - idleDelta) * 100.0 / systemDelta;
            }

            _lastIdleTime = idle;
            _lastSystemTime = systemTime;
        }

        long appProcessorTicks = GetAppProcessorTicks();
        long appTicksDelta = appProcessorTicks - _lastAppTicks;
        long elapsedTicks = now - _lastRealTimeTicks;
        double appUsage = 0.0;

        if (elapsedTicks > 0)
        {
            appUsage = (double)appTicksDelta / elapsedTicks * 100.0 / _processorCount;
        }

        _lastAppTicks = appProcessorTicks;
        _lastRealTimeTicks = now;

        _lastUsage = new CpuUsageSnapshot(
            Math.Clamp(appUsage, 0.0, 100.0),
            Math.Clamp(totalUsage, 0.0, 100.0));

        return _lastUsage;
    }

    private static long GetAppProcessorTicks()
    {
        long ticks = 0;
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            ticks += currentProcess.TotalProcessorTime.Ticks;

            var sdrplayProcesses = Process.GetProcessesByName("sdrplay_apiService");
            foreach (var p in sdrplayProcesses)
            {
                try
                {
                    ticks += p.TotalProcessorTime.Ticks;
                }
                catch
                {
                    // Ignore access denied, etc.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // Ignore
        }
        return ticks;
    }
}
