using System;

namespace SRdeck.Models
{
    public class WavFileInfo
    {
        public string BaseName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public string Location { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Freq { get; set; } = string.Empty;
        public string SampleRate { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
    }
}
