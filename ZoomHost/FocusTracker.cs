using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace ZoomHost;

/// <summary>
/// Tracks text carets, active input fields, Microsoft Word cursors, and Microsoft Excel active cells
/// on a dedicated background MTA thread so UIA/MSAA queries never stall the DXGI render loop.
/// </summary>
public sealed class FocusTracker : IDisposable
{
    private const uint OBJID_CARET = 0xFFFFFFF8;
    private const int CHILDID_SELF = 0;

    private static readonly Guid IID_IAccessible = new("618736e0-3c3d-11cf-810c-00aa00389b71");

    private readonly int _selfPid = Environment.ProcessId;
    private readonly AutoResetEvent _wakeSignal = new(false);
    private readonly object _stateLock = new();
    private readonly Thread? _workerThread;

    private const long MinCrossProcessProbeIntervalMs = 200;

    private volatile bool _enabled;
    private volatile bool _active;
    private volatile bool _disposed;
    private volatile bool _hasPendingUserTrigger;

    private bool _hasTarget;
    private float _targetScreenX;
    private float _targetScreenY;
    private bool _pendingForceCenter;
    private long _sequence;
    private nint _lastFocusHwnd;
    private nint _lastProbedFgHwnd;
    private nint _lastProbedFocusHwnd;
    private long _lastCrossProcessProbeTicks;
    private string _lastControlKey = string.Empty;

    public FocusTracker(bool enabled = true, bool startWorker = true)
    {
        _enabled = enabled;
        if (startWorker)
        {
            _workerThread = new Thread(WorkerLoop)
            {
                Name = "ZoomHost.FocusTracker",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _workerThread.SetApartmentState(ApartmentState.MTA);
            _workerThread.Start();
        }
    }

    public bool IsEnabled => _enabled;

    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        if (!active)
        {
            _hasPendingUserTrigger = false;
            lock (_stateLock)
            {
                _hasTarget = false;
                _pendingForceCenter = false;
            }
        }
        else if (_enabled)
        {
            _wakeSignal.Set();
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _hasPendingUserTrigger = false;
            lock (_stateLock)
            {
                _hasTarget = false;
                _pendingForceCenter = false;
            }
        }
        else if (_active)
        {
            _hasPendingUserTrigger = true;
            _wakeSignal.Set();
        }
    }

    public void NotifyKeyboardActivity()
    {
        if (!_enabled || !_active || _disposed)
        {
            return;
        }

        _hasPendingUserTrigger = true;
        _wakeSignal.Set();
    }

    public void NotifyMouseClick()
    {
        if (!_enabled || !_active || _disposed)
        {
            return;
        }

        _hasPendingUserTrigger = true;
        _wakeSignal.Set();
    }

    public bool TryGetFocusTarget(
        int desktopOriginX,
        int desktopOriginY,
        int desktopWidth,
        int desktopHeight,
        out float worldX,
        out float worldY,
        out bool forceCenter,
        out long sequence)
    {
        lock (_stateLock)
        {
            if (!_enabled || !_hasTarget)
            {
                worldX = 0;
                worldY = 0;
                forceCenter = false;
                sequence = _sequence;
                return false;
            }

            float wx = _targetScreenX - desktopOriginX;
            float wy = _targetScreenY - desktopOriginY;
            if (wx < 0.0f || wx > desktopWidth || wy < 0.0f || wy > desktopHeight)
            {
                worldX = 0;
                worldY = 0;
                forceCenter = false;
                sequence = _sequence;
                return false;
            }

            worldX = wx;
            worldY = wy;
            forceCenter = _pendingForceCenter;
            _pendingForceCenter = false;
            sequence = _sequence;
            return true;
        }
    }

    /// <summary>
    /// Runs one synchronous focus probe against the current foreground window (useful for tests/diagnostics).
    /// </summary>
    public bool TryProbeCurrentFocus(out float screenX, out float screenY, out string controlKey, out string source)
    {
        return ProbeForegroundFocus(allowCrossProcess: true, triggered: true, out screenX, out screenY, out controlKey, out source);
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            if (!_enabled || !_active)
            {
                _wakeSignal.WaitOne(500);
                continue;
            }

            _wakeSignal.WaitOne(160);
            if (_disposed || !_enabled || !_active)
            {
                continue;
            }

            bool triggered = _hasPendingUserTrigger;
            long nowTicks = Environment.TickCount64;
            bool cooldownReady = (nowTicks - _lastCrossProcessProbeTicks) >= MinCrossProcessProbeIntervalMs;

            // Only clear pending trigger when we can actually run a cross-process probe,
            // so rapid typing coalesces cleanly into at most one probe per 200 ms instead of
            // hammering Chrome/Word/Excel/NVDA UIA trees on every single keystroke.
            if (triggered && cooldownReady)
            {
                _hasPendingUserTrigger = false;
            }

            if (!ProbeForegroundFocus(allowCrossProcess: cooldownReady, triggered: triggered, out float sx, out float sy, out string controlKey, out _))
            {
                continue;
            }

            nint fg = GetForegroundWindow();
            lock (_stateLock)
            {
                float dx = sx - _targetScreenX;
                float dy = sy - _targetScreenY;
                float distSq = dx * dx + dy * dy;

                bool controlChanged = fg != _lastFocusHwnd || !string.Equals(controlKey, _lastControlKey, StringComparison.Ordinal);
                bool movedEnough = !_hasTarget || distSq >= 2.25f; // >= 1.5 px

                if (controlChanged || movedEnough || triggered)
                {
                    bool largeJump = !_hasTarget || controlChanged || distSq > (96.0f * 96.0f);
                    _targetScreenX = sx;
                    _targetScreenY = sy;
                    _lastFocusHwnd = fg;
                    _lastControlKey = controlKey;
                    _hasTarget = true;

                    if (largeJump)
                    {
                        _pendingForceCenter = true;
                    }

                    if (controlChanged || movedEnough)
                    {
                        _sequence++;
                    }
                }
            }
        }
    }

    private bool ProbeForegroundFocus(
        bool allowCrossProcess,
        bool triggered,
        out float screenX,
        out float screenY,
        out string controlKey,
        out string source)
    {
        screenX = 0;
        screenY = 0;
        controlKey = string.Empty;
        source = string.Empty;

        nint fg = GetForegroundWindow();
        if (fg == nint.Zero)
        {
            return false;
        }

        uint fgThreadId = GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == 0 || fgPid == (uint)_selfPid)
        {
            return false;
        }

        GUITHREADINFO gui = new() { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        bool hasGui = GetGUIThreadInfo(fgThreadId, ref gui);
        nint focusHwnd = hasGui && gui.hwndFocus != nint.Zero ? gui.hwndFocus : fg;

        bool focusWindowChanged = fg != _lastProbedFgHwnd || focusHwnd != _lastProbedFocusHwnd;

        // Tier 1: Classic Win32 / Office Caret via GetGUIThreadInfo (kernel-only, ~0.03 ms, zero cross-process IPC)
        if (hasGui && gui.hwndCaret != nint.Zero &&
            gui.rcCaret.Right >= gui.rcCaret.Left &&
            gui.rcCaret.Bottom > gui.rcCaret.Top)
        {
            POINT pt = new()
            {
                X = (gui.rcCaret.Left + gui.rcCaret.Right) / 2,
                Y = (gui.rcCaret.Top + gui.rcCaret.Bottom) / 2
            };
            if (ClientToScreen(gui.hwndCaret, ref pt) &&
                GetWindowRect(gui.hwndCaret, out RECT caretWndRect) &&
                pt.X >= caretWndRect.Left - 8 && pt.X <= caretWndRect.Right + 8 &&
                pt.Y >= caretWndRect.Top - 8 && pt.Y <= caretWndRect.Bottom + 8)
            {
                _lastProbedFgHwnd = fg;
                _lastProbedFocusHwnd = focusHwnd;
                screenX = pt.X;
                screenY = pt.Y;
                controlKey = $"GUI:{gui.hwndCaret}";
                source = "GetGUIThreadInfo";
                return true;
            }
        }

        // Cross-process MSAA/UIA queries are expensive (especially when NVDA/JAWS is active).
        // Only run them on explicit user keyboard/click activity or foreground focus change, and respect the cooldown.
        if (!allowCrossProcess || (!triggered && !focusWindowChanged))
        {
            return false;
        }

        _lastCrossProcessProbeTicks = Environment.TickCount64;
        _lastProbedFgHwnd = fg;
        _lastProbedFocusHwnd = focusHwnd;

        // Tier 2: MSAA OBJID_CARET (gold standard for Microsoft Word _WwG, Excel formula bar, and Chromium)
        if (TryProbeMsaaCaret(focusHwnd, out screenX, out screenY))
        {
            controlKey = $"MSAA:{focusHwnd}";
            source = "MSAA_OBJID_CARET";
            return true;
        }

        // Tier 3: UI Automation (Word TextPattern, Excel active cell SelectionPattern / DataItem, Input Controls)
        return TryProbeUiAutomation(fg, focusHwnd, out screenX, out screenY, out controlKey, out source);
    }

    private static bool TryProbeMsaaCaret(nint hwndFocus, out float screenX, out float screenY)
    {
        screenX = 0;
        screenY = 0;
        if (hwndFocus == nint.Zero)
        {
            return false;
        }

        try
        {
            Guid iid = IID_IAccessible;
            int hr = AccessibleObjectFromWindow(hwndFocus, OBJID_CARET, ref iid, out object? accObj);
            if (hr != 0 || accObj is not IAccessible acc)
            {
                return false;
            }

            acc.accLocation(out int left, out int top, out int width, out int height, CHILDID_SELF);
            if (height <= 0 || height > 400 || width < 0 || width > 200)
            {
                return false;
            }

            if (left == 0 && top == 0 && width == 0 && height == 0)
            {
                return false;
            }

            if (GetWindowRect(hwndFocus, out RECT wndRect))
            {
                float cx = left + width * 0.5f;
                float cy = top + height * 0.5f;
                if (cx >= wndRect.Left - 16 && cx <= wndRect.Right + 16 &&
                    cy >= wndRect.Top - 16 && cy <= wndRect.Bottom + 16)
                {
                    screenX = cx;
                    screenY = cy;
                    return true;
                }
            }
        }
        catch
        {
            // Ignore apps whose MSAA caret provider isn't active
        }

        return false;
    }

    private static readonly CacheRequest UiaElementCacheRequest = CreateUiaCacheRequest();

    private static CacheRequest CreateUiaCacheRequest()
    {
        CacheRequest req = new()
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full
        };
        req.Add(AutomationElement.ProcessIdProperty);
        req.Add(AutomationElement.IsOffscreenProperty);
        req.Add(AutomationElement.ControlTypeProperty);
        req.Add(AutomationElement.BoundingRectangleProperty);
        req.Add(AutomationElement.ClassNameProperty);
        req.Add(AutomationElement.AutomationIdProperty);
        req.Add(AutomationElement.IsKeyboardFocusableProperty);
        req.Add(TextPattern.Pattern);
        req.Add(SelectionPattern.Pattern);
        return req;
    }

    private bool TryProbeUiAutomation(
        nint fgHwnd,
        nint focusHwnd,
        out float screenX,
        out float screenY,
        out string controlKey,
        out string source)
    {
        screenX = 0;
        screenY = 0;
        controlKey = string.Empty;
        source = string.Empty;

        try
        {
            AutomationElement? focused;
            using (UiaElementCacheRequest.Activate())
            {
                focused = AutomationElement.FocusedElement;
            }
            if (focused is null)
            {
                return false;
            }

            AutomationElement.AutomationElementInformation info = focused.Cached;
            if (info.ProcessId == _selfPid || info.IsOffscreen)
            {
                return false;
            }

            string runtimeKey = $"UIA:{fgHwnd}:{focusHwnd}:{info.ControlType.ProgrammaticName}:{info.AutomationId}:{info.ClassName}";

            // 3a: TextPattern selection / caret (Word, Edge/Chrome inputs, Notepad, WPF/WinUI edits, Excel formula edit)
            if (focused.TryGetCachedPattern(TextPattern.Pattern, out object? textPatternObj) &&
                textPatternObj is TextPattern textPattern)
            {
                var selection = textPattern.GetSelection();
                if (selection is { Length: > 0 })
                {
                    Rect[] rects = selection[0].GetBoundingRectangles();
                    if (rects is { Length: > 0 })
                    {
                        Rect r = rects[^1];
                        if (IsReasonableRect(r, maxW: 1800, maxH: 400, allowZeroWidth: true))
                        {
                            screenX = r.Width <= 120.0
                                ? (float)(r.Left + r.Width * 0.5)
                                : (float)(r.Left + 60.0);
                            screenY = (float)(r.Top + r.Height * 0.5);
                            controlKey = runtimeKey;
                            source = "UIA_TextPattern";
                            return true;
                        }
                    }
                }
            }

            // 3b: SelectionPattern (Excel active worksheet cell / grid selection)
            if (focused.TryGetCachedPattern(SelectionPattern.Pattern, out object? selObj) &&
                selObj is SelectionPattern selPattern)
            {
                AutomationElement[] items = selPattern.Current.GetSelection();
                if (items is { Length: > 0 })
                {
                    Rect r = items[0].Current.BoundingRectangle;
                    if (IsReasonableRect(r, maxW: 1100, maxH: 260, allowZeroWidth: false))
                    {
                        screenX = (float)(r.Left + Math.Min(r.Width * 0.5, 96.0));
                        screenY = (float)(r.Top + r.Height * 0.5);
                        controlKey = $"{runtimeKey}:SEL:{(int)r.Left}:{(int)r.Top}";
                        source = "UIA_SelectionPattern";
                        return true;
                    }
                }
            }

            // 3c: Direct focused Input control or Excel cell BoundingRectangle
            ControlType ct = info.ControlType;
            bool isTextInput = ct == ControlType.Edit || ct == ControlType.ComboBox;
            bool isCellOrGridItem =
                ct == ControlType.DataItem ||
                ct == ControlType.Custom ||
                (info.ClassName?.StartsWith("EXCEL", StringComparison.OrdinalIgnoreCase) ?? false);

            if (isTextInput || isCellOrGridItem)
            {
                Rect bounds = info.BoundingRectangle;
                double maxW = isTextInput ? 1400.0 : 950.0;
                double maxH = isTextInput ? 350.0 : 240.0;
                if (IsReasonableRect(bounds, maxW, maxH, allowZeroWidth: false))
                {
                    screenX = (float)(bounds.Left + Math.Min(bounds.Width * 0.45, 84.0));
                    screenY = bounds.Height > 64.0
                        ? (float)(bounds.Top + 22.0)
                        : (float)(bounds.Top + bounds.Height * 0.5);
                    controlKey = runtimeKey;
                    source = isCellOrGridItem ? "UIA_CellBounds" : "UIA_InputBounds";
                    return true;
                }
            }
        }
        catch
        {
            // Transient ElementNotAvailable / COM timeouts from external apps are expected
        }

        return false;
    }

    private static bool IsReasonableRect(Rect r, double maxW, double maxH, bool allowZeroWidth)
    {
        if (r.IsEmpty ||
            double.IsNaN(r.Left) || double.IsInfinity(r.Left) ||
            double.IsNaN(r.Top) || double.IsInfinity(r.Top) ||
            double.IsNaN(r.Width) || double.IsInfinity(r.Width) ||
            double.IsNaN(r.Height) || double.IsInfinity(r.Height))
        {
            return false;
        }

        double minW = allowZeroWidth ? 0.0 : 2.0;
        return r.Width >= minW && r.Width <= maxW && r.Height >= 2.0 && r.Height <= maxH;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wakeSignal.Set();
        _workerThread?.Join(500);
        _wakeSignal.Dispose();
    }

    [ComImport]
    [Guid("618736e0-3c3d-11cf-810c-00aa00389b71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IAccessible
    {
        void get_accParent([MarshalAs(UnmanagedType.IDispatch)] out object? ppdispParent);
        void get_accChildCount(out int pcountChildren);
        void get_accChild([MarshalAs(UnmanagedType.Struct)] object varChild, [MarshalAs(UnmanagedType.IDispatch)] out object? ppdispChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string? get_accName([MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string? get_accValue([MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string? get_accDescription([MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? get_accRole([MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? get_accState([MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string? get_accHelp([MarshalAs(UnmanagedType.Struct)] object varChild);
        int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string? pszHelpFile, [MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string? get_accKeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? get_accFocus();
        [return: MarshalAs(UnmanagedType.Struct)]
        object? get_accSelection();
        [return: MarshalAs(UnmanagedType.BStr)]
        string? get_accDefaultAction([MarshalAs(UnmanagedType.Struct)] object varChild);
        void accSelect(int flagsSelect, [MarshalAs(UnmanagedType.Struct)] object varChild);
        void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, [MarshalAs(UnmanagedType.Struct)] object varChild);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);
}
