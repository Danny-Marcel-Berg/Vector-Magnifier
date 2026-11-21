using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ZoomHost;

/// <summary>
/// Compact, draggable, sharply scalable control panel positioned directly below the
/// magnifier overlay so the panel itself zooms cleanly with the screen while remaining above app windows.
/// </summary>
public partial class ControlPanelWindow : Window
{
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_TIMER = 0x0113;
    private const int WM_MOVING = 0x0216;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const nuint MODAL_RENDER_TIMER_ID = 1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_TOPMOST = new(-1);

    private readonly nint _ownerHwnd;
    private readonly Action _onZoomIn;
    private readonly Action _onZoomOut;
    private readonly Action _onResetZoom;
    private readonly Action<bool> _onVectorTextToggled;
    private readonly Action<float> _onGammaChanged;
    private readonly Action<float>? _onEdgeSmoothingChanged;
    private readonly Action<float>? _onMouseSizeChanged;
    private readonly Action<bool>? _onInvertColorsChanged;
    private readonly Action<bool>? _onInvertCursorChanged;
    private readonly Action<bool>? _onCursorFocusChanged;
    private readonly Action _onExitRequested;
    private readonly Action? _onModalRenderFrame;

    private HwndSource? _hwndSource;
    private float _lastDisplayedZoom = -1.0f;
    private string _lastStatusText = string.Empty;
    private bool _closingFromHost;
    private bool _inSizeMoveModalLoop;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveTimer;
    private bool _applyingSettings;

    public nint WindowHandle { get; private set; }

    public ControlPanelWindow(
        nint ownerHwnd,
        Action onZoomIn,
        Action onZoomOut,
        Action onResetZoom,
        Action<bool> onVectorTextToggled,
        Action<float> onGammaChanged,
        Action onExitRequested,
        Action? onModalRenderFrame = null,
        Action<float>? onEdgeSmoothingChanged = null,
        Action<float>? onMouseSizeChanged = null,
        Action<bool>? onInvertColorsChanged = null,
        Action<bool>? onInvertCursorChanged = null,
        Action<bool>? onCursorFocusChanged = null,
        AppSettings? settings = null)
    {
        _settings = settings ?? new AppSettings();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _settings.Save();
        };

        _ownerHwnd = ownerHwnd;
        _onZoomIn = onZoomIn;
        _onZoomOut = onZoomOut;
        _onResetZoom = onResetZoom;
        _onVectorTextToggled = onVectorTextToggled;
        _onGammaChanged = onGammaChanged;
        _onEdgeSmoothingChanged = onEdgeSmoothingChanged;
        _onMouseSizeChanged = onMouseSizeChanged;
        _onInvertColorsChanged = onInvertColorsChanged;
        _onInvertCursorChanged = onInvertCursorChanged;
        _onCursorFocusChanged = onCursorFocusChanged;
        _onExitRequested = onExitRequested;
        _onModalRenderFrame = onModalRenderFrame;

        _applyingSettings = true; // XAML default values must not overwrite the loaded settings
        InitializeComponent();
        ApplySettings();
        LocationChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && double.IsFinite(Left) && double.IsFinite(Top))
            {
                _settings.PanelLeft = Left;
                _settings.PanelTop = Top;
                ScheduleSave();
            }
        };
        Closing += (_, _) =>
        {
            SaveSettingsNow();
            if (!_closingFromHost)
            {
                _onExitRequested();
            }
        };
    }

    /// <summary>Applies the loaded settings to the controls and forwards them to the host.</summary>
    private void ApplySettings()
    {
        _applyingSettings = true;
        try
        {
            UiScaleSlider.Value = _settings.UiScale;
            EdgeSmoothingSlider.Value = _settings.EdgeSmoothing;
            GammaSlider.Value = _settings.Gamma;
            MouseSizeSlider.Value = _settings.MouseSize;
            VectorTextEnabledCheckBox.IsChecked = _settings.VectorText;
            InvertColorsCheckBox.IsChecked = _settings.InvertColors;
            InvertCursorCheckBox.IsChecked = _settings.InvertCursor;
            CursorFocusCheckBox.IsChecked = _settings.CursorFocus;

            // Forward explicitly: no change event fires when a value equals the XAML default.
            _onEdgeSmoothingChanged?.Invoke((float)(_settings.EdgeSmoothing / 100.0));
            _onGammaChanged?.Invoke((float)(_settings.Gamma / 100.0));
            _onMouseSizeChanged?.Invoke((float)(_settings.MouseSize / 100.0));
            _onVectorTextToggled?.Invoke(_settings.VectorText);
            _onInvertColorsChanged?.Invoke(_settings.InvertColors);
            _onInvertCursorChanged?.Invoke(_settings.InvertCursor);
            _onCursorFocusChanged?.Invoke(_settings.CursorFocus);

            if (_settings.PanelCollapsed)
            {
                MainContentPanel.Visibility = Visibility.Collapsed;
                CollapseButton.Content = "▴";
            }

            if (_settings.PanelLeft is double left && _settings.PanelTop is double top &&
                left >= SystemParameters.VirtualScreenLeft - 50 &&
                top >= SystemParameters.VirtualScreenTop - 10 &&
                left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
                top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void Remember(Action update)
    {
        if (_applyingSettings)
        {
            return;
        }

        update();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_applyingSettings)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Called by the host whenever the target zoom changes.</summary>
    public void NotifyTargetZoom(float targetZoom)
    {
        if (Math.Abs(_settings.Zoom - targetZoom) < 0.001)
        {
            return;
        }

        Remember(() => _settings.Zoom = targetZoom);
    }

    public void SaveSettingsNow()
    {
        _saveTimer.Stop();
        _settings.Save();
    }

    public void CloseFromHost()
    {
        SaveSettingsNow();
        _closingFromHost = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowInteropHelper helper = new(this);
        WindowHandle = helper.Handle;
        if (WindowHandle != nint.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(WindowHandle);
            _hwndSource?.AddHook(WndProcHook);
            EnsureTopmost();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_inSizeMoveModalLoop && WindowHandle != nint.Zero)
        {
            KillTimer(WindowHandle, MODAL_RENDER_TIMER_ID);
            _inSizeMoveModalLoop = false;
        }
        _hwndSource?.RemoveHook(WndProcHook);
        _hwndSource = null;
        base.OnClosed(e);
    }

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE)
        {
            _inSizeMoveModalLoop = true;
            SetTimer(hwnd, MODAL_RENDER_TIMER_ID, 16, nint.Zero);
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            if (_inSizeMoveModalLoop)
            {
                KillTimer(hwnd, MODAL_RENDER_TIMER_ID);
                _inSizeMoveModalLoop = false;
            }
        }
        else if (msg == WM_TIMER && (nuint)wParam == MODAL_RENDER_TIMER_ID)
        {
            _onModalRenderFrame?.Invoke();
        }
        else if (msg == WM_MOVING)
        {
            _onModalRenderFrame?.Invoke();
        }
        else if (msg == WM_WINDOWPOSCHANGING && lParam != nint.Zero &&
                 _ownerHwnd != nint.Zero && WindowState != WindowState.Minimized && IsWindowVisible(_ownerHwnd))
        {
            WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0 && pos.hwndInsertAfter != _ownerHwnd)
            {
                pos.hwndInsertAfter = _ownerHwnd;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return nint.Zero;
    }

    public void EnsureTopmost()
    {
        if (WindowHandle != nint.Zero && WindowState != WindowState.Minimized)
        {
            nint insertAfter = (_ownerHwnd != nint.Zero && IsWindowVisible(_ownerHwnd)) ? _ownerHwnd : HWND_TOPMOST;
            SetWindowPos(WindowHandle, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public bool IsScreenPointInside(int screenX, int screenY)
    {
        if (WindowHandle == nint.Zero || WindowState == WindowState.Minimized || !GetWindowRect(WindowHandle, out RECT rect))
        {
            return false;
        }

        return screenX >= rect.Left && screenX <= rect.Right &&
               screenY >= rect.Top && screenY <= rect.Bottom;
    }

    public void UpdateZoomDisplay(float zoom)
    {
        if (MathF.Abs(zoom - _lastDisplayedZoom) < 0.05f)
        {
            return;
        }

        _lastDisplayedZoom = zoom;
        ZoomValueText.Text = $"Zoom: {zoom:F1}x";
        CompactZoomText.Text = $"{zoom:F1}x";
    }

    public void UpdateHookStatus(string statusLine)
    {
        if (statusLine == _lastStatusText)
        {
            return;
        }

        _lastStatusText = statusLine;
        HookStatusText.Text = statusLine;
    }

    private void UiScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (UiScaleTransform is null || UiScaleText is null)
        {
            return;
        }

        double scale = e.NewValue;
        UiScaleTransform.ScaleX = scale;
        UiScaleTransform.ScaleY = scale;
        UiScaleText.Text = $"{Math.Round(scale * 100.0):0}%";
        Remember(() => _settings.UiScale = scale);
    }

    private void GammaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GammaValueText is null)
        {
            return;
        }

        double val = e.NewValue;
        GammaValueText.Text = val < 0.5 ? "0 (Normal)" : $"+{val:0}";
        _onGammaChanged?.Invoke((float)(val / 100.0));
        Remember(() => _settings.Gamma = val);
    }

    private void EdgeSmoothingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EdgeSmoothingValueText is null)
        {
            return;
        }

        double val = e.NewValue;
        EdgeSmoothingValueText.Text = val < 0.5 ? "0" : val >= 99.5 ? "100 (Max)" : $"{val:0}";
        _onEdgeSmoothingChanged?.Invoke((float)(val / 100.0));
        Remember(() => _settings.EdgeSmoothing = val);
    }

    private void MouseSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MouseSizeValueText is null)
        {
            return;
        }

        double val = e.NewValue;
        MouseSizeValueText.Text = $"{val:0} %";
        _onMouseSizeChanged?.Invoke((float)(val / 100.0));
        Remember(() => _settings.MouseSize = val);
    }

    public void SetInvertColors(bool enabled)
    {
        if (InvertColorsCheckBox.IsChecked == enabled)
        {
            return;
        }

        _applyingSettings = true;
        try
        {
            InvertColorsCheckBox.IsChecked = enabled;
        }
        finally
        {
            _applyingSettings = false;
        }

        Remember(() => _settings.InvertColors = enabled);
    }

    private void InvertColors_Changed(object sender, RoutedEventArgs e)
    {
        bool on = InvertColorsCheckBox.IsChecked == true;
        _onInvertColorsChanged?.Invoke(on);
        Remember(() => _settings.InvertColors = on);
    }

    private void InvertCursor_Changed(object sender, RoutedEventArgs e)
    {
        bool on = InvertCursorCheckBox.IsChecked == true;
        _onInvertCursorChanged?.Invoke(on);
        Remember(() => _settings.InvertCursor = on);
    }

    private void CursorFocus_Changed(object sender, RoutedEventArgs e)
    {
        bool on = CursorFocusCheckBox.IsChecked == true;
        _onCursorFocusChanged?.Invoke(on);
        Remember(() => _settings.CursorFocus = on);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _onZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _onZoomOut();
    private void ResetZoom_Click(object sender, RoutedEventArgs e) => _onResetZoom();

    private void VectorTextEnabled_Changed(object sender, RoutedEventArgs e)
    {
        bool on = VectorTextEnabledCheckBox.IsChecked == true;
        _onVectorTextToggled?.Invoke(on);
        Remember(() => _settings.VectorText = on);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _onExitRequested();

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        bool collapsed = MainContentPanel.Visibility == Visibility.Visible;
        MainContentPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = collapsed ? "▴" : "▾";
        Remember(() => _settings.PanelCollapsed = collapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(nint hWnd, nuint uIDEvent);
}
