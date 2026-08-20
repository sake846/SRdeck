using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SRdeck.Services;

public interface IGpuUsageMonitor
{
    GpuUsageSnapshot GetUsage();
}

public sealed class GpuUsageMonitor : IGpuUsageMonitor, IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const int ErrorSuccess = 0;
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private const string GpuEngineUtilizationCounter = @"\GPU Engine(*)\Utilization Percentage";

    private IntPtr _query;
    private IntPtr _counter;
    private bool _isAvailable;
    private readonly object _sync = new();
    private readonly int _processId = Environment.ProcessId;
    private long _lastSampleTicks;
    private GpuUsageSnapshot _lastUsage;

    public GpuUsageMonitor()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != ErrorSuccess) return;
            if (PdhAddEnglishCounter(_query, GpuEngineUtilizationCounter, IntPtr.Zero, out _counter) != ErrorSuccess) return;
            if (PdhCollectQueryData(_query) != ErrorSuccess) return;
            _isAvailable = true;
        }
        catch
        {
            _isAvailable = false;
        }
    }

    public GpuUsageSnapshot GetUsage()
    {
        lock (_sync)
        {
            return GetUsageCore();
        }
    }

    private GpuUsageSnapshot GetUsageCore()
    {
        if (!_isAvailable) return default;

        long now = Environment.TickCount64;
        if (_lastSampleTicks != 0 && now - _lastSampleTicks < 500)
        {
            return _lastUsage;
        }

        _lastSampleTicks = now;
        try
        {
            if (PdhCollectQueryData(_query) != ErrorSuccess) return _lastUsage;

            int bufferSize = 0;
            int itemCount = 0;
            int status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != PdhMoreData || bufferSize <= 0 || itemCount <= 0) return _lastUsage;

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
                if (status != ErrorSuccess) return _lastUsage;

                var usageByEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var appUsageByEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                int itemSize = Marshal.SizeOf<PdhCounterValueItemDouble>();
                for (int index = 0; index < itemCount; index++)
                {
                    IntPtr itemPtr = IntPtr.Add(buffer, index * itemSize);
                    var counterItem = Marshal.PtrToStructure<PdhCounterValueItemDouble>(itemPtr);
                    if (counterItem.Value.CStatus == ErrorSuccess)
                    {
                        string? instanceName = Marshal.PtrToStringUni(counterItem.Name);
                        if (!TryParseInstance(instanceName, out int parsedProcessId, out string engineKey)) continue;

                        double usagePercentage = Math.Clamp(counterItem.Value.DoubleValue, 0.0, 100.0);
                        if (parsedProcessId == _processId)
                        {
                            appUsageByEngine.TryGetValue(engineKey, out double appEngineUsage);
                            appUsageByEngine[engineKey] = appEngineUsage + usagePercentage;
                        }

                        usageByEngine.TryGetValue(engineKey, out double engineUsage);
                        usageByEngine[engineKey] = engineUsage + usagePercentage;
                    }
                }

                double appUsage = GetBusiestEngineUsage(appUsageByEngine);
                double totalUsage = GetBusiestEngineUsage(usageByEngine);

                _lastUsage = new GpuUsageSnapshot(
                    Math.Clamp(appUsage, 0.0, 100.0),
                    Math.Clamp(totalUsage, 0.0, 100.0));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            _isAvailable = false;
        }

        return _lastUsage;
    }

    private static double GetBusiestEngineUsage(Dictionary<string, double> usageByEngine)
    {
        double usage = 0.0;
        foreach (double engineUsage in usageByEngine.Values)
        {
            usage = Math.Max(usage, engineUsage);
        }
        return usage;
    }

    private static bool TryParseInstance(string? instanceName, out int processId, out string engineKey)
    {
        processId = 0;
        engineKey = string.Empty;
        if (string.IsNullOrEmpty(instanceName) || !instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int processIdEndIndex = instanceName.IndexOf('_', 4);
        if (processIdEndIndex <= 4 || !int.TryParse(instanceName.AsSpan(4, processIdEndIndex - 4), NumberStyles.None, CultureInfo.InvariantCulture, out processId))
        {
            return false;
        }

        // The suffix identifies the adapter and physical engine. Removing the PID lets
        // per-process utilization values be combined into the utilization of that engine.
        engineKey = instanceName[processIdEndIndex..];
        int duplicateSuffixIndex = engineKey.LastIndexOf('#');
        if (duplicateSuffixIndex >= 0)
        {
            engineKey = engineKey[..duplicateSuffixIndex];
        }
        return engineKey.Length > 1;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _isAvailable = false;
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _counter = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValueItemDouble
    {
        public IntPtr Name;
        public PdhFmtCounterValueDouble Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueDouble
    {
        public int CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, ref int itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}

public readonly record struct GpuUsageSnapshot(double AppUsagePercent, double TotalUsagePercent);
