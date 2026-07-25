using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace EasyGameTranslator;

public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;
    private const int StopHotKeyId = 1;
    private const int StartHotKeyId = 2;
    private const int RestartHotKeyId = 3;
    private readonly CaptureCoordinator _coordinator;
    private readonly UserSettings _settings;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private IntPtr _handle;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isClosing;
    private bool _isTranslationRunning;

    public MainWindow()
    {
        InitializeComponent();
        _coordinator = new CaptureCoordinator(Dispatcher, SetStatus, ShowCaptureFailure);
        _settings = UserSettings.Load();
        FontSizeSlider.Value = Math.Clamp(_settings.FontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
        CreateTrayIcon();
        RefreshWindows();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProc);
        RegisterHotKey(_handle, StartHotKeyId, 0, (uint)KeyInterop.VirtualKeyFromKey(Key.F7));
        RegisterHotKey(_handle, RestartHotKeyId, 0, (uint)KeyInterop.VirtualKeyFromKey(Key.F6));
        RegisterHotKey(_handle, StopHotKeyId, 0, (uint)KeyInterop.VirtualKeyFromKey(Key.F8));
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartTranslationAsync();

    private async Task StartTranslationAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_isTranslationRunning)
            {
                SetStatus("Перевод уже запущен. F6 перезапускает захват.");
                return;
            }
            await StartSelectedWindowCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task StartSelectedWindowCoreAsync()
    {
        if (WindowBox.SelectedItem is not GameWindowInfo targetWindow)
        {
            Show();
            Activate();
            SetStatus("Выберите окно игры или браузера.");
            return;
        }

        Hide();
        try
        {
            var started = await _coordinator.StartAsync(targetWindow, "en", FontSizeSlider.Value, null);
            if (!started)
            {
                Show();
                return;
            }
        }
        catch (Exception ex)
        {
            ShowCaptureFailure($"Ошибка запуска перевода: {ex.Message}");
            return;
        }

        _isTranslationRunning = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopAndShowAsync();

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var selectedHandle = (WindowBox.SelectedItem as GameWindowInfo)?.Handle;
        var windows = GameWindowInfo.GetOpenWindows();
        WindowBox.ItemsSource = windows;
        WindowBox.SelectedItem = windows.FirstOrDefault(window => window.Handle == selectedHandle) ?? windows.FirstOrDefault();
        if (windows.Count == 0)
            SetStatus("Открытых окон для захвата не найдено. Откройте игру и нажмите «Обновить».");
    }

    private async Task StopAndShowAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            await _coordinator.StopAsync();
            _isTranslationRunning = false;
            Show();
            Activate();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SetStatus("Перевод остановлен. F7 — запустить снова.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RestartTranslationAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            SetStatus("Перезапускаю захват и перевод…");
            await _coordinator.StopAsync();
            _isTranslationRunning = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            await StartSelectedWindowCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeText is not null)
            FontSizeText.Text = $"{Math.Round(e.NewValue):0} px";
        if (_settings is not null)
        {
            _settings.FontSize = e.NewValue;
            _settings.Save();
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotKey)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case StartHotKeyId:
                _ = StartTranslationAsync();
                handled = true;
                break;
            case RestartHotKeyId:
                _ = RestartTranslationAsync();
                handled = true;
                break;
            case StopHotKeyId:
                _ = StopAndShowAsync();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            _isClosing = true;
            await _coordinator.StopAsync();
            Close();
            return;
        }

        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, StartHotKeyId);
            UnregisterHotKey(_handle, RestartHotKeyId);
            UnregisterHotKey(_handle, StopHotKeyId);
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnClosing(e);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowCaptureFailure(string message)
    {
        _isTranslationRunning = false;
        Show();
        Activate();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetStatus(message);
        System.Windows.MessageBox.Show(message, "EasyGameTranslator", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Показать настройки", null, async (_, _) => await StopAndShowAsync());
        menu.Items.Add("Запустить перевод (F7)", null, async (_, _) => await StartTranslationAsync());
        menu.Items.Add("Перезапустить перевод (F6)", null, async (_, _) => await RestartTranslationAsync());
        menu.Items.Add("Остановить перевод (F8)", null, async (_, _) => await StopAndShowAsync());
        menu.Items.Add("Выход", null, (_, _) => Close());
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "EasyGameTranslator",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += async (_, _) => await StopAndShowAsync();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
