using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyGameTranslator;

public sealed class RegionSelectorWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly System.Windows.Shapes.Rectangle _selection = new() { Stroke = System.Windows.Media.Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(45, 0, 130, 255)), Visibility = Visibility.Collapsed };
    private System.Windows.Point _start;
    private System.Drawing.Rectangle? _result;
    private bool _isSelecting;

    private RegionSelectorWindow()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(65, 0, 0, 0));
        Cursor = System.Windows.Input.Cursors.Cross;
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(new TextBlock
        {
            Text = "Выделите только область реплики/диалога. Esc — отмена",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 20,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 0, 0, 0)),
            Padding = new Thickness(10)
        });
        Content = _canvas;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;
    }

    public static System.Drawing.Rectangle? Select()
    {
        var selector = new RegionSelectorWindow();
        selector.ShowDialog();
        return selector._result;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _start = e.GetPosition(this);
        _selection.Visibility = Visibility.Visible;
        _isSelecting = CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSelecting)
            return;
        DrawSelection(e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
            return;
        var end = e.GetPosition(this);
        DrawSelection(end);
        _isSelecting = false;
        ReleaseMouseCapture();
        var startOnScreen = PointToScreen(_start);
        var endOnScreen = PointToScreen(end);
        var left = Math.Min(startOnScreen.X, endOnScreen.X);
        var top = Math.Min(startOnScreen.Y, endOnScreen.Y);
        var width = Math.Abs(endOnScreen.X - startOnScreen.X);
        var height = Math.Abs(endOnScreen.Y - startOnScreen.Y);
        if (width >= 40 && height >= 25)
            _result = new System.Drawing.Rectangle((int)Math.Round(Left + left), (int)Math.Round(Top + top), (int)Math.Round(width), (int)Math.Round(height));
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void DrawSelection(System.Windows.Point end)
    {
        Canvas.SetLeft(_selection, Math.Min(_start.X, end.X));
        Canvas.SetTop(_selection, Math.Min(_start.Y, end.Y));
        _selection.Width = Math.Abs(end.X - _start.X);
        _selection.Height = Math.Abs(end.Y - _start.Y);
    }
}
