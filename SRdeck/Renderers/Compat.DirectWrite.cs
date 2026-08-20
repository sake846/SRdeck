namespace SRdeck.Renderers.Compat.DirectWrite
{
    internal enum TextAlignment { Leading, Center, Trailing }
    internal enum ParagraphAlignment { Near, Center, Far }

    internal class IDWriteTextFormat
    {
        public string FontFamilyName { get; }
        public float FontSize { get; }
        public TextAlignment TextAlignment { get; set; }
        public ParagraphAlignment ParagraphAlignment { get; set; }
        public IDWriteTextFormat(string fontFamilyName, float fontSize) { FontFamilyName = fontFamilyName; FontSize = fontSize; }
    }

    internal class IDWriteFactory
    {
        public IDWriteTextFormat CreateTextFormat(string fontFamilyName, float fontSize) => new(fontFamilyName, fontSize);
    }
}
