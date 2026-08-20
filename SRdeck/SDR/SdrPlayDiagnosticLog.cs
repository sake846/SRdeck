using System;
using System.IO;
using System.Text;
using SRdeck.Configuration;

namespace SRdeck.SDR;

internal static class SdrPlayDiagnosticLog
{
    private static readonly object Sync = new();

    public static bool IsEnabled { get; set; }

    public static string LogPath => Path.Combine(
        UserDataPaths.UserDataDirectory,
        "logs",
        "sdrplay-diagnostics.log");

    public static void Write(string eventName, string details)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            string path = LogPath;
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string line = $"{DateTimeOffset.Now:O}\tpid={Environment.ProcessId}\ttid={Environment.CurrentManagedThreadId}\t{eventName}\t{details}{Environment.NewLine}";
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never interfere with the native SDR callback path.
        }
    }
}
