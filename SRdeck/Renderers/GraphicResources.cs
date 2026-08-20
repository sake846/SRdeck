using System.Windows.Media;
using System.Globalization;

namespace SRdeck.Renderers;

/// <summary>
/// アプリケーション全体の描画エンジン（Spectrum, Waterfall, Receiver 等）で共有される
/// 静的な描画リソース（Brush, Pen, Typeface 等）管理・提供するユーティリティクラス
/// 描画ループ（毎秒数十回）での都度インスタンス生成およびガベージコレクションスパイクを防ぐため、
/// すべてのリソースは起動時に一度だけ生成され Freeze() されます
/// </summary>
public static class GraphicResources
{
    // Fonts
    public static readonly Typeface FontUDGothic = new Typeface("BIZ UDゴシック");

    // Brushes (Backgrounds)
    public static readonly SolidColorBrush BrBackBlack = CreateFrozenBrush(Color.FromArgb(255, 0, 0, 0));
    public static readonly SolidColorBrush BrBackDarkGray = CreateFrozenBrush(Color.FromArgb(255, 30, 30, 30));
    public static readonly SolidColorBrush BrBackDarkAmber = CreateFrozenBrush(Color.FromArgb(255, 30, 15, 0));

    // Brushes (Forgrounds / Text)
    public static readonly SolidColorBrush BrForeWhite = CreateFrozenBrush(Color.FromArgb(255, 255, 255, 255));
    public static readonly SolidColorBrush BrForeAmber = CreateFrozenBrush(Color.FromArgb(255, 255, 180, 70));
    public static readonly SolidColorBrush BrForeGray = CreateFrozenBrush(Color.FromArgb(255, 214, 214, 214));
    public static readonly SolidColorBrush BrForeRed = CreateFrozenBrush(Color.FromArgb(255, 255, 60, 60));

    // Brushes (Indicators)
    public static readonly SolidColorBrush BrMeterGreen = CreateFrozenBrush(Color.FromArgb(255, 100, 200, 0));
    public static readonly SolidColorBrush BrMeterRed = CreateFrozenBrush(Color.FromArgb(255, 200, 40, 0));

    // Pens (Borders / Lines)
    public static readonly Pen PnBorderLightGray = CreateFrozenPen(Color.FromArgb(255, 225, 225, 225), 3);
    public static readonly Pen PnBorderGray = CreateFrozenPen(Color.FromArgb(255, 150, 150, 150), 1);
    public static readonly Pen PnBorderAmber = CreateFrozenPen(Color.FromArgb(255, 255, 150, 30), 1);
    
    // Grid Lines
    public static readonly Pen PnGridHeavyGray = CreateFrozenPen(Color.FromArgb(255, 60, 60, 60), 1);
    public static readonly Pen PnGridLightGray = CreateFrozenPen(Color.FromArgb(255, 40, 40, 40), 1);
    public static readonly Pen PnGridHeavyRed = CreateFrozenPen(Color.FromArgb(255, 60, 30, 30), 1);
    public static readonly Pen PnGridLightRed = CreateFrozenPen(Color.FromArgb(255, 40, 20, 20), 1);
    
    // Cursors
    public static readonly Pen PnCursorLightGray = CreateFrozenPen(Color.FromArgb(200, 100, 100, 100), 1);
    public static readonly SolidColorBrush BrCursorFill = CreateFrozenBrush(Color.FromArgb(50, 255, 255, 255));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
