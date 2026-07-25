using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace EasyGameTranslator;

public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WhKeyboardLl = 13;
    private const uint ModNoRepeat = 0x4000;
    private const int StopHotKeyId = 1;
    private const int StartHotKeyId = 2;
    private const int RestartHotKeyId = 3;
    private readonly CaptureCoordinator _coordinator;
    private readonly UserSettings _settings;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<int, long> _lastHotKeyDispatch = [];
    private readonly HashSet<uint> _pressedHotKeys = [];
    private IntPtr _handle;
    private IntPtr _keyboardHook;
    private LowLevelKeyboardProcedure? _keyboardHookProcedure;
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
        RegisterHotKey(_handle, StartHotKeyId, ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.F7));
        RegisterHotKey(_handle, RestartHotKeyId, ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.F6));
        RegisterHotKey(_handle, StopHotKeyId, ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.F8));

        // RegisterHotKey may fail when a game or another overlay already owns
        // an F-key. A low-level listener provides a non-blocking fallback and
        // still lets the original key reach the game.
        _keyboardHookProcedure = KeyboardHookProcedure;
        _keyboardHook = SetWindowsHookEx(
            WhKeyboardLl,
            _keyboardHookProcedure,
            GetModuleHandle(null),
            0);
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
                DispatchHotKey(StartHotKeyId);
                handled = true;
                break;
            case RestartHotKeyId:
                DispatchHotKey(RestartHotKeyId);
                handled = true;
                break;
            case StopHotKeyId:
                DispatchHotKey(StopHotKeyId);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private IntPtr KeyboardHookProcedure(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var keyData = Marshal.PtrToStructure<LowLevelKeyboardInput>(lParam);
            var hotKeyId = keyData.VirtualKey switch
            {
                0x75 => RestartHotKeyId, // F6
                0x76 => StartHotKeyId,   // F7
                0x77 => StopHotKeyId,    // F8
                _ => 0
            };
            var message = wParam.ToInt32();
            if (hotKeyId != 0 &&
                (message == WmKeyDown || message == WmSysKeyDown) &&
                _pressedHotKeys.Add(keyData.VirtualKey))
                Dispatcher.BeginInvoke(() => DispatchHotKey(hotKeyId));
            else if (message == WmKeyUp || message == WmSysKeyUp)
                _pressedHotKeys.Remove(keyData.VirtualKey);
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void DispatchHotKey(int hotKeyId)
    {
        var now = Environment.TickCount64;
        if (_lastHotKeyDispatch.TryGetValue(hotKeyId, out var previous) && now - previous < 250)
            return;
        _lastHotKeyDispatch[hotKeyId] = now;

        switch (hotKeyId)
        {
            case StartHotKeyId:
                _ = StartTranslationAsync();
                break;
            case RestartHotKeyId:
                _ = RestartTranslationAsync();
                break;
            case StopHotKeyId:
                _ = StopAndShowAsync();
                break;
        }
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
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
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
        System.Windows.MessageBox.Show(message, "EasyGameTranslator 0.1 Beta", MessageBoxButton.OK, MessageBoxImage.Error);
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
            Text = "EasyGameTranslator 0.1 Beta",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += async (_, _) => await StopAndShowAsync();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr LowLevelKeyboardProcedure(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProcedure procedure,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
