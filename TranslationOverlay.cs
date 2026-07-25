using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace EasyGameTranslator;

/// <summary>
/// An overlay made of ordinary opaque card windows, not a transparent/layered full-screen window.
/// WDA_EXCLUDEFROMCAPTURE is unreliable for layered WPF windows, while normal top-level windows
/// can be excluded by Windows Graphics Capture.
/// </summary>
public sealed class TranslationOverlay : IDisposable
{
    private readonly List<TranslationCardWindow> _cards = [];
    private readonly System.Drawing.Rectangle _screenBounds;
    private double _fontSize = 26;
    private bool _shown;
    private int _visibleCardCount;
    private bool _hiddenForCapture;

    public TranslationOverlay(System.Drawing.Rectangle screenBounds) => _screenBounds = screenBounds;

    public void Show() => _shown = true;

    public void Render(IReadOnlyList<TranslatedLine> lines)
    {
        if (!_shown) return;
        while (_cards.Count < lines.Count)
            _cards.Add(new TranslationCardWindow());

        for (var index = 0; index < lines.Count; index++)
        {
            var card = _cards[index];
            card.Update(lines[index], _fontSize);
            if (!card.IsVisible)
                card.Show();
        }

        for (var index = lines.Count; index < _cards.Count; index++)
            _cards[index].Hide();
        _visibleCardCount = lines.Count;
    }

    public void SetFontSize(double fontSize) => _fontSize = Math.Clamp(fontSize, 14, 44);

    public void UpdateBounds(System.Drawing.Rectangle screenBounds)
    {
        // Card coordinates come from the captured primary monitor and are updated on each render.
    }

    public void HideForCapture()
    {
        if (_hiddenForCapture) return;
        for (var index = 0; index < _visibleCardCount; index++)
            _cards[index].Hide();
        // Wait for one DWM composition so Desktop Duplication receives the
        // source frame rather than our own black cards.
        DwmFlush();
        _hiddenForCapture = true;
    }

    public void RestoreAfterCapture()
    {
        if (!_hiddenForCapture) return;
        for (var index = 0; index < _visibleCardCount; index++)
            _cards[index].Show();
        _hiddenForCapture = false;
    }

    public void Dispose()
    {
        foreach (var card in _cards)
            card.Close();
        _cards.Clear();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

internal sealed class TranslationCardWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private readonly TextBlock _text;
    private readonly Border _background;
    private double _scaleX = 1;
    private double _scaleY = 1;

    public TranslationCardWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.Height;

        _text = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12, 8, 12, 8)
        };
        _background = new Border
        {
            // The source remains visible through the card while the Russian
            // text itself stays fully opaque and easy to read.
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 0, 0, 0)),
            Child = _text
        };
        Content = _background;
        SourceInitialized += (_, _) => ConfigureCaptureExclusion();
    }

    public void Update(TranslatedLine line, double fontSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _scaleX = dpi.DpiScaleX;
        _scaleY = dpi.DpiScaleY;
        _text.Text = line.Text;
        _text.FontSize = fontSize / _scaleY;
        _text.Margin = new Thickness(12 / _scaleX, 8 / _scaleY, 12 / _scaleX, 8 / _scaleY);
        Width = Math.Max(155 / _scaleX, line.Bounds.Width / _scaleX);
        MinHeight = Math.Max(24 / _scaleY, line.Bounds.Height / _scaleY);
        Left = Math.Max(0, line.Bounds.Left / _scaleX - 5);
        Top = Math.Max(0, line.Bounds.Top / _scaleY - 3);
    }

    private void ConfigureCaptureExclusion()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is not null)
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        var margins = new DwmMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
            throw new InvalidOperationException($"Windows не включила исключение оверлея из захвата (код {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }
}
