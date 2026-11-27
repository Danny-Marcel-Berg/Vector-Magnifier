using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZoomHost;

internal static class Program
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;

    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint LWA_ALPHA = 0x00000002;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint GW_HWNDPREV = 3;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int DWMWA_CLOAKED = 14;
    private static readonly nint HWND_TOPMOST = new(-1);

    private const uint WM_NULL = 0x0000;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_NCHITTEST = 0x0084;
    private static readonly nint HTTRANSPARENT = new(-1);
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint PM_REMOVE = 0x0001;

    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    private const int WH_MOUSE_LL = 14;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_0 = 0x30;
    private const uint VK_I = 0x49;
    private const uint VK_NUMPAD0 = 0x60;
    private const uint VK_ADD = 0x6B;
    private const uint VK_SUBTRACT = 0x6D;
    private const uint VK_OEM_PLUS = 0xBB;
    private const uint VK_OEM_MINUS = 0xBD;

    private const int HOTKEY_ZOOM_IN_MAIN = 1;
    private const int HOTKEY_ZOOM_IN_NUMPAD = 2;
    private const int HOTKEY_ZOOM_OUT_MAIN = 3;
    private const int HOTKEY_ZOOM_OUT_NUMPAD = 4;
    private const int HOTKEY_ZOOM_RESET_MAIN = 5;
    private const int HOTKEY_ZOOM_RESET_NUMPAD = 6;
    private const int HOTKEY_EXIT = 7;
    private const int HOTKEY_ZOOM_RESET_CTRLALT_MAIN = 8;
    private const int HOTKEY_ZOOM_RESET_CTRLALT_NUMPAD = 9;
    private const int HOTKEY_INVERT_COLORS = 10;

    private const uint SPI_SETCURSORS = 0x0057;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private static readonly uint[] SystemCursorIds =
    [
        32512, // OCR_NORMAL
        32513, // OCR_IBEAM
        32514, // OCR_WAIT
        32515, // OCR_CROSS
        32516, // OCR_UP
        32642, // OCR_SIZENWSE
        32643, // OCR_SIZENESW
        32644, // OCR_SIZEWE
        32645, // OCR_SIZENS
        32646, // OCR_SIZEALL
        32648, // OCR_NO
        32649, // OCR_HAND
        32650, // OCR_APPSTARTING
        32651, // OCR_HELP
        32671, // OCR_PIN
        32672  // OCR_PERSON
    ];

    private static CameraController? _camera;
    private static VectorRenderer? _renderer;
    private static FocusTracker? _focusTracker;
    private static ControlPanelWindow? _controlPanel;
    private static bool _vectorTextEnabled = true;
    private static int _colorMode = 0;
    private static int _lastNonZeroColorMode = 1;
    private static int _mediaMode = 0;
    private static float _gammaBoost = 0.0f;
    private static float _edgeSmoothing = 0.0f;
    private static float _cursorSizeScale = 1.0f;
    private static bool _invertCursor = false;
    private static bool _cursorFocusEnabled = true;
    private static nint _hCursorIBeam;
    private static nint _hCursorHand;
    private static nint _hCursorSizeWE;
    private static nint _hCursorSizeNS;
    private static nint _hCursorSizeAll;
    private static long _lastKeyboardOrClickTicks = 0;
    private static long _lastZoomActionTicks = 0;
    private static long _zoomActionGeneration = 0;
    private static int _isZoomedForHooks = 0;
    private static int _mainThreadSleeping = 0;
    private static bool _running = true;

    private static void NotifyUserZoomAction()
    {
        Volatile.Write(ref _lastZoomActionTicks, Environment.TickCount64);
        Interlocked.Increment(ref _zoomActionGeneration);
    }

    private static nint _mouseHook = nint.Zero;
    private static LowLevelHookProc? _mouseHookProc;
    private static Thread? _hookThread;
    private static uint _hookThreadId;
    private static uint _mainThreadId;

    private static nint _hInstance = nint.Zero;
    private static bool _magInitialized;
    private static bool _magFullscreenApplied;
    private static bool _magInvertApplied;
    private static bool _overlayHiddenForShell;
    private static bool _isUiAccessProcess;
    private static nint _activeShellHwnd = nint.Zero;
    private static bool _systemCursorHidden;
    private static bool _usedFallbackCursorHide;
    private static long _lastCursorEnforceTicks;
    private static int _captureOriginX;
    private static int _captureOriginY;
    private static float _fallbackCursorWorldX;
    private static float _fallbackCursorWorldY;
    private static readonly Func<(float x, float y)> SampleFreshCursorDelegate = SampleFreshCursorWorld;

    private static (float x, float y) SampleFreshCursorWorld()
    {
        return GetCursorPos(out POINT fresh)
            ? (fresh.X - _captureOriginX, fresh.Y - _captureOriginY)
            : (_fallbackCursorWorldX, _fallbackCursorWorldY);
    }

    private static VectorCursorShape DetectCursorShape()
    {
        if (_usedFallbackCursorHide)
        {
            return VectorCursorShape.Arrow;
        }

        CURSORINFO ci = default;
        ci.cbSize = (uint)Marshal.SizeOf<CURSORINFO>();
        if (!GetCursorInfo(ref ci) || ci.hCursor == nint.Zero)
        {
            return VectorCursorShape.Arrow;
        }

        if (ci.hCursor == _hCursorIBeam) return VectorCursorShape.IBeam;
        if (ci.hCursor == _hCursorHand) return VectorCursorShape.Hand;
        if (ci.hCursor == _hCursorSizeWE) return VectorCursorShape.SizeWE;
        if (ci.hCursor == _hCursorSizeNS) return VectorCursorShape.SizeNS;
        if (ci.hCursor == _hCursorSizeAll) return VectorCursorShape.SizeAll;
        return VectorCursorShape.Arrow;
    }

    [STAThread]
    private static void Main()
    {
        // Replace any stale previous ZoomHost instance cleanly so double-clicking never stacks invisible/conflicting overlays
        try
        {
            int selfPid = Environment.ProcessId;
            foreach (Process other in Process.GetProcessesByName("ZoomHost"))
            {
                try
                {
                    if (other.Id != selfPid)
                    {
                        other.Kill(entireProcessTree: true);
                        other.WaitForExit(1000);
                    }
                }
                catch
                {
                    // Ignore protected/exiting processes
                }
                finally
                {
                    other.Dispose();
                }
            }
        }
        catch
        {
            // Non-fatal
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemCursor();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreSystemCursor();

        using CaptureEngine capture = new(outputIndex: 0);

        int width = capture.DesktopWidth;
        int height = capture.DesktopHeight;

        _camera = new CameraController(width, height);
        AppSettings settings = AppSettings.Load();

        _hInstance = GetModuleHandle(null);
        nint hInstance = _hInstance;
        _hCursorIBeam = LoadCursor(nint.Zero, 32513);
        _hCursorHand = LoadCursor(nint.Zero, 32649);
        _hCursorSizeWE = LoadCursor(nint.Zero, 32644);
        _hCursorSizeNS = LoadCursor(nint.Zero, 32645);
        _hCursorSizeAll = LoadCursor(nint.Zero, 32646);
        string className = "VectorMagnifierOverlayClass";

        WNDCLASSEX wndClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = WndProc,
            hInstance = hInstance,
            lpszClassName = className
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed with error {Marshal.GetLastWin32Error()}.");
        }

        // WS_EX_LAYERED | WS_EX_TRANSPARENT is required by Windows win32k for cross-process mouse hover
        // and click pass-through to underlying apps (Word, Excel, Explorer, Browser).
        uint exStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        uint style = WS_POPUP;

        nint hwnd = CreateWindowEx(
            exStyle,
            className,
            "VectorMagnifier - ZoomHost",
            style,
            capture.OriginX,
            capture.OriginY,
            width,
            height,
            nint.Zero,
            nint.Zero,
            hInstance,
            nint.Zero);

        if (hwnd == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed with error {Marshal.GetLastWin32Error()}.");
        }

        if (!SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA))
        {
            throw new InvalidOperationException($"SetLayeredWindowAttributes failed with error {Marshal.GetLastWin32Error()}.");
        }

        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
        {
            throw new InvalidOperationException($"SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed with error {Marshal.GetLastWin32Error()}.");
        }

        _magInitialized = MagInitialize();
        _isUiAccessProcess = IsCurrentProcessUiAccess();

        timeBeginPeriod(1);
        System.Runtime.GCLatencyMode prevGcMode = System.Runtime.GCSettings.LatencyMode;
        try
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        }
        catch
        {
        }
        try
        {
            using Process self = Process.GetCurrentProcess();
            // AboveNormal keeps the magnifier responsive without preempting DWM, input, NVDA, or foreground apps.
            self.PriorityClass = ProcessPriorityClass.AboveNormal;
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
        }
        catch
        {
        }
        ControlPanelWindow? controlPanel = null;

        try
        {
            using SharedMemoryRingBuffer ringBuffer = new(initializeHeader: true);
            using FocusTracker focusTracker = new(enabled: _cursorFocusEnabled);
            _focusTracker = focusTracker;
            using VectorRenderer renderer = new(hwnd, capture, width, height);
            _renderer = renderer;

            Action? renderStep = null;

            controlPanel = new ControlPanelWindow(
                ownerHwnd: hwnd,
                onZoomIn: () => { NotifyUserZoomAction(); _camera.ZoomIn(); },
                onZoomOut: () => { NotifyUserZoomAction(); _camera.ZoomOut(); },
                onResetZoom: () => { NotifyUserZoomAction(); _camera.ResetZoom(); },
                onVectorTextToggled: enabled => _vectorTextEnabled = enabled,
                onGammaChanged: boost => _gammaBoost = Math.Clamp(boost, -0.5f, 1.0f),
                onExitRequested: () =>
                {
                    RestoreSystemCursor();
                    _running = false;
                    PostQuitMessage(0);
                },
                onModalRenderFrame: () => renderStep?.Invoke(),
                onEdgeSmoothingChanged: smoothing => _edgeSmoothing = Math.Clamp(smoothing, 0.0f, 1.0f),
                onMouseSizeChanged: size => _cursorSizeScale = Math.Clamp(size, 0.5f, 3.0f),
                onColorModeChanged: mode =>
                {
                    _colorMode = Math.Clamp(mode, 0, 4);
                    if (_colorMode != 0)
                    {
                        _lastNonZeroColorMode = _colorMode;
                    }
                },
                onMediaModeChanged: mode => _mediaMode = Math.Clamp(mode, 0, 2),
                onInvertCursorChanged: inverted => _invertCursor = inverted,
                onCursorFocusChanged: enabled =>
                {
                    _cursorFocusEnabled = enabled;
                    focusTracker.SetEnabled(enabled);
                },
                settings: settings);
            _controlPanel = controlPanel;

            // Restore the persisted zoom level.
            if (settings.Zoom > CameraController.MinZoom + 0.001)
            {
                _camera.SetTargetZoom((float)settings.Zoom);
            }

            controlPanel.Show();
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            controlPanel.EnsureTopmost();

            using ProcessAutoInjector autoInjector = new(hwnd, controlPanel.WindowHandle);
            bool wasZoomTextActive = false;
            bool preferCaretFocus = false;
            long lastFocusSequence = -1;
            long lastZoomGeneration = Volatile.Read(ref _zoomActionGeneration);
            int mouseAnchorX = int.MinValue;
            int mouseAnchorY = int.MinValue;
            int prevCursorScreenX = int.MinValue;
            int prevCursorScreenY = int.MinValue;
            long lastMouseMoveTicks = 0;
            long lastNewWindowScanTicks = 0;
            long lastZOrderCheckTicks = 0;
            long lastShellCheckTicks = 0;
            long lastUnmovableRetryTicks = 0;
            nint lastUnmovableTopWindow = nint.Zero;
            string lastHookState = string.Empty;
            int lastTextCount = -1, lastProcCount = -1, lastPps = -1;

            bool hasPresentedFrame = false;
            long lastPresDesktopVer = -1;
            float lastPresFocusX = float.NaN;
            float lastPresFocusY = float.NaN;
            float lastPresZoom = float.NaN;
            int lastPresCursorX = int.MinValue;
            int lastPresCursorY = int.MinValue;
            bool lastPresShowCursor = false;
            float lastPresCursorScale = float.NaN;
            bool lastPresInvertCursor = false;
            int lastPresColorMode = 0;
            int lastPresMediaMode = 0;
            VectorCursorShape lastPresCursorShape = VectorCursorShape.Arrow;
            float lastPresEdgeSmoothing = float.NaN;
            float lastPresGamma = float.NaN;
            bool lastPresVectorText = false;

            CursorSmoother cursorSmoother = new();
            Stopwatch stopwatch = Stopwatch.StartNew();
            double lastSeconds = stopwatch.Elapsed.TotalSeconds;
            double lastPresentSeconds = 0.0;

            void StepAndRenderFrame(bool allowIdleWait)
            {
                if (_camera is not CameraController camera)
                {
                    return;
                }

                bool showZoomedCursor = camera.Zoom > 1.001f || camera.TargetZoom > 1.001f;
                bool showVectorCursor = showZoomedCursor || _invertCursor || MathF.Abs(_cursorSizeScale - 1.0f) > 0.02f;
                bool needsOverlay = showZoomedCursor || showVectorCursor || _colorMode != 0;

                focusTracker.SetActive(showZoomedCursor && _cursorFocusEnabled);
                Volatile.Write(ref _isZoomedForHooks, showZoomedCursor ? 1 : 0);

                // At 1.0x zoom with standard OS cursor and no colour inversion, hide the fullscreen overlay
                // completely so ZoomHost adds 0% GPU/DWM load and 0 frames of display latency to the desktop.
                if (!needsOverlay)
                {
                    if (_overlayHiddenForShell || _magFullscreenApplied || _magInvertApplied)
                    {
                        ExitShellMagnifierMode(hwnd, controlPanel!);
                    }
                    UpdateCursorVisibility(false);
                    if (IsWindowVisible(hwnd))
                    {
                        ShowWindow(hwnd, SW_HIDE);
                    }
                    hasPresentedFrame = false;
                    lastShellCheckTicks = 0;
                    lastSeconds = stopwatch.Elapsed.TotalSeconds;

                    controlPanel!.UpdateZoomDisplay(camera.Zoom);
                    controlPanel.NotifyTargetZoom(camera.TargetZoom);

                    if (allowIdleWait)
                    {
                        MsgWaitForMultipleObjectsEx(0, nint.Zero, 30, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                    }
                    return;
                }

                long nowTicks = Environment.TickCount64;
                if (lastShellCheckTicks == 0 ||
                    nowTicks - lastShellCheckTicks >= 50 ||
                    (_activeShellHwnd != nint.Zero && nowTicks - lastShellCheckTicks >= 30))
                {
                    lastShellCheckTicks = nowTicks;
                    UpdateShellPopupState(
                        hwnd,
                        lastUnmovableTopWindow,
                        controlPanel!,
                        capture.OriginX,
                        capture.OriginY,
                        capture.DesktopWidth,
                        capture.DesktopHeight);
                }

                bool shellCandidate = _activeShellHwnd != nint.Zero && _magInitialized && (showZoomedCursor || _colorMode != 0);
                if (!shellCandidate)
                {
                    if (capture.MonitorCount > 1 && GetCursorPos(out POINT monPt) &&
                        capture.TrySwitchOutputForPoint(monPt.X, monPt.Y))
                    {
                        SetWindowPos(hwnd, HWND_TOPMOST, capture.OriginX, capture.OriginY, capture.DesktopWidth, capture.DesktopHeight, SWP_NOACTIVATE);
                        camera.Resize(capture.DesktopWidth, capture.DesktopHeight);
                        renderer.Resize(capture.DesktopWidth, capture.DesktopHeight);
                        camera.RequestCenterOnTarget();
                        hasPresentedFrame = false;
                        lastPresDesktopVer = -1;
                        controlPanel?.EnsureTopmost();
                    }

                    // Never block in DXGI AcquireNextFrame once the first desktop frame is available;
                    // idle sleeping happens in MsgWaitForMultipleObjectsEx where mouse moves wake at 0 ms.
                    uint acquireTimeout = capture.HasAcquiredFrame ? 0u : 8u;
                    capture.TryAcquireFrame(acquireTimeout, out _);
                }

                void SampleAndUpdateCamera(out float outMouseWorldX, out float outMouseWorldY, out int outCursorScreenX, out int outCursorScreenY, out bool outSmoothCursor)
                {
                    nowTicks = Environment.TickCount64;
                    double nowSeconds = stopwatch.Elapsed.TotalSeconds;
                    float deltaTime = (float)(nowSeconds - lastSeconds);
                    lastSeconds = nowSeconds;

                    outMouseWorldX = camera.FocusX;
                    outMouseWorldY = camera.FocusY;
                    outCursorScreenX = capture.OriginX + (int)MathF.Round(camera.FocusX);
                    outCursorScreenY = capture.OriginY + (int)MathF.Round(camera.FocusY);

                    if (GetCursorPos(out POINT cursorPos))
                    {
                        outCursorScreenX = cursorPos.X;
                        outCursorScreenY = cursorPos.Y;
                        outMouseWorldX = cursorPos.X - capture.OriginX;
                        outMouseWorldY = cursorPos.Y - capture.OriginY;
                    }

                    if (prevCursorScreenX != int.MinValue &&
                        (outCursorScreenX != prevCursorScreenX || outCursorScreenY != prevCursorScreenY))
                    {
                        lastMouseMoveTicks = nowTicks;
                    }
                    prevCursorScreenX = outCursorScreenX;
                    prevCursorScreenY = outCursorScreenY;

                    cursorSmoother.Update(outMouseWorldX, outMouseWorldY, deltaTime, camera.Zoom);
                    outSmoothCursor = CursorSmoother.IsActive(camera.Zoom);
                    outMouseWorldX = cursorSmoother.X;
                    outMouseWorldY = cursorSmoother.Y;

                    float targetX = outMouseWorldX;
                    float targetY = outMouseWorldY;
                    float comfortRatio = 1.0f;

                    long currentZoomGen = Volatile.Read(ref _zoomActionGeneration);
                    if (currentZoomGen != lastZoomGeneration)
                    {
                        lastZoomGeneration = currentZoomGen;
                        preferCaretFocus = false;
                        mouseAnchorX = outCursorScreenX;
                        mouseAnchorY = outCursorScreenY;
                    }
                    else
                    {
                        bool mouseMovedSinceFocus = mouseAnchorX != int.MinValue &&
                            (outCursorScreenX != mouseAnchorX || outCursorScreenY != mouseAnchorY);
                        if (mouseMovedSinceFocus)
                        {
                            preferCaretFocus = false;
                        }
                    }

                    bool zoomSuppressingCaret = camera.IsAnimating ||
                        (nowTicks - Volatile.Read(ref _lastZoomActionTicks)) < 500;

                    if (_cursorFocusEnabled &&
                        showZoomedCursor &&
                        focusTracker.TryGetFocusTarget(capture.OriginX, capture.OriginY, capture.DesktopWidth, capture.DesktopHeight,
                            out float focusWorldX, out float focusWorldY, out bool forceCenter, out long focusSeq))
                    {
                        if (zoomSuppressingCaret)
                        {
                            lastFocusSequence = focusSeq;
                            preferCaretFocus = false;
                        }
                        else if (focusSeq != lastFocusSequence)
                        {
                            lastFocusSequence = focusSeq;
                            bool recentUserIntent = (nowTicks - Volatile.Read(ref _lastKeyboardOrClickTicks)) <= 450;
                            bool mouseIdle = (nowTicks - lastMouseMoveTicks) > 120;
                            if (forceCenter || (recentUserIntent && mouseIdle))
                            {
                                preferCaretFocus = true;
                                mouseAnchorX = outCursorScreenX;
                                mouseAnchorY = outCursorScreenY;
                                if (forceCenter)
                                {
                                    camera.RequestCenterOnTarget();
                                }
                            }
                        }

                        if (preferCaretFocus)
                        {
                            targetX = focusWorldX;
                            targetY = focusWorldY;
                            comfortRatio = 0.38f;
                        }
                    }
                    else
                    {
                        preferCaretFocus = false;
                    }

                    camera.Update(deltaTime, targetX, targetY, comfortRatio);
                }

                SampleAndUpdateCamera(out float mouseWorldX, out float mouseWorldY, out int cursorScreenX, out int cursorScreenY, out bool smoothCursor);

                bool useShellMagnifier = (showZoomedCursor || _colorMode != 0) && _activeShellHwnd != nint.Zero && _magInitialized;
                if (useShellMagnifier)
                {
                    float magZoom = showZoomedCursor ? camera.Zoom : 1.0f;
                    int viewW = Math.Max(1, (int)MathF.Floor(capture.DesktopWidth / magZoom));
                    int viewH = Math.Max(1, (int)MathF.Floor(capture.DesktopHeight / magZoom));
                    int offsetX = showZoomedCursor
                        ? Math.Clamp(
                            capture.OriginX + (int)MathF.Round(camera.FocusX - (capture.DesktopWidth * 0.5f) / magZoom),
                            capture.OriginX,
                            capture.OriginX + Math.Max(0, capture.DesktopWidth - viewW))
                        : capture.OriginX;
                    int offsetY = showZoomedCursor
                        ? Math.Clamp(
                            capture.OriginY + (int)MathF.Round(camera.FocusY - (capture.DesktopHeight * 0.5f) / magZoom),
                            capture.OriginY,
                            capture.OriginY + Math.Max(0, capture.DesktopHeight - viewH))
                        : capture.OriginY;

                    if (MagSetFullscreenTransform(magZoom, offsetX, offsetY))
                    {
                        _magFullscreenApplied = true;
                        ApplyMagColorInvert(_colorMode is 1 or 2 or 3);
                        hasPresentedFrame = false;
                        if (!_overlayHiddenForShell)
                        {
                            ShowWindow(hwnd, SW_HIDE);
                            _overlayHiddenForShell = true;
                        }
                        RestoreSystemCursor();
                        if (allowIdleWait)
                        {
                            Thread.Sleep(8);
                        }
                    }
                    else
                    {
                        useShellMagnifier = false;
                    }
                }

                if (!useShellMagnifier && (_overlayHiddenForShell || _magFullscreenApplied || _magInvertApplied))
                {
                    ExitShellMagnifierMode(hwnd, controlPanel!);
                    hasPresentedFrame = false;
                }

                bool cursorOverControlPanel = !showZoomedCursor &&
                    controlPanel is not null &&
                    controlPanel.WindowHandle != nint.Zero &&
                    IsPointInExpandedWindowRect(controlPanel.WindowHandle, cursorScreenX, cursorScreenY, marginPx: 2);

                bool cursorOccluded = showVectorCursor &&
                    (cursorOverControlPanel ||
                     (lastUnmovableTopWindow != nint.Zero &&
                      IsPointInExpandedWindowRect(lastUnmovableTopWindow, cursorScreenX, cursorScreenY, marginPx: 16)));

                // Keep shared memory viewport inactive so already-injected ZoomHook64 DLLs never suppress
                // native GDI/DirectWrite drawing (e.g. Word Alt KeyTip popups). EdgeSmoothingShader renders
                // crisp magnified vector contours directly from Desktop Duplication.
                ringBuffer.UpdateMagnifierState(false, 0, 0, 0, 0);
                _ = wasZoomTextActive;
                _ = lastNewWindowScanTicks;

                if (!useShellMagnifier)
                {
                    if (shellCandidate)
                    {
                        capture.TryAcquireFrame(0, out _);
                    }
                    if (capture.HasAcquiredFrame)
                    {
                        bool drawCursor = showVectorCursor && !cursorOccluded;
                        VectorCursorShape cursorShape = drawCursor ? DetectCursorShape() : VectorCursorShape.Arrow;
                        bool overlayVisible = IsWindowVisible(hwnd);

                        bool needsRender =
                            !hasPresentedFrame ||
                            !overlayVisible ||
                            camera.IsAnimating ||
                            capture.DesktopFrameVersion != lastPresDesktopVer ||
                            MathF.Abs(camera.FocusX - lastPresFocusX) > 0.0005f ||
                            MathF.Abs(camera.FocusY - lastPresFocusY) > 0.0005f ||
                            MathF.Abs(camera.Zoom - lastPresZoom) > 0.0005f ||
                            (drawCursor && (cursorScreenX != lastPresCursorX || cursorScreenY != lastPresCursorY || cursorShape != lastPresCursorShape)) ||
                            drawCursor != lastPresShowCursor ||
                            MathF.Abs(_cursorSizeScale - lastPresCursorScale) > 0.001f ||
                            _invertCursor != lastPresInvertCursor ||
                            _colorMode != lastPresColorMode ||
                            _mediaMode != lastPresMediaMode ||
                            MathF.Abs(_edgeSmoothing - lastPresEdgeSmoothing) > 0.001f ||
                            MathF.Abs(_gammaBoost - lastPresGamma) > 0.001f ||
                            _vectorTextEnabled != lastPresVectorText;

                        if (needsRender)
                        {
                            if (hasPresentedFrame)
                            {
                                bool fastCursorOnly =
                                    !camera.IsAnimating &&
                                    capture.DesktopFrameVersion == lastPresDesktopVer &&
                                    MathF.Abs(camera.FocusX - lastPresFocusX) < 0.0005f &&
                                    MathF.Abs(camera.FocusY - lastPresFocusY) < 0.0005f &&
                                    MathF.Abs(camera.Zoom - lastPresZoom) < 0.0005f;
                                double minIntervalMs = fastCursorOnly ? 3.5 : 7.0;
                                double elapsedMs = (stopwatch.Elapsed.TotalSeconds - lastPresentSeconds) * 1000.0;
                                if (elapsedMs < minIntervalMs)
                                {
                                    if (allowIdleWait && (minIntervalMs - elapsedMs) >= 1.5)
                                    {
                                        Thread.Sleep(1);
                                    }
                                    else
                                    {
                                        Thread.Yield();
                                    }
                                    return;
                                }
                            }

                            // Wait strictly 1:1 with MaximumFrameLatency=1 so at most one GPU frame sits in flight.
                            renderer.WaitForNextFrame();

                            // Immediately before rendering, grab the freshest desktop frame & cursor position
                            // so input-to-photon latency stays minimal.
                            capture.TryAcquireFrame(0, out _);
                            SampleAndUpdateCamera(out mouseWorldX, out mouseWorldY, out cursorScreenX, out cursorScreenY, out smoothCursor);
                            cursorOverControlPanel = !showZoomedCursor &&
                                controlPanel is not null &&
                                controlPanel.WindowHandle != nint.Zero &&
                                IsPointInExpandedWindowRect(controlPanel.WindowHandle, cursorScreenX, cursorScreenY, marginPx: 2);
                            cursorOccluded = showVectorCursor &&
                                (cursorOverControlPanel ||
                                 (lastUnmovableTopWindow != nint.Zero &&
                                  IsPointInExpandedWindowRect(lastUnmovableTopWindow, cursorScreenX, cursorScreenY, marginPx: 16)));
                            drawCursor = showVectorCursor && !cursorOccluded;
                            cursorShape = drawCursor ? DetectCursorShape() : VectorCursorShape.Arrow;

                            if (!overlayVisible)
                            {
                                ShowWindow(hwnd, SW_SHOWNA);
                                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                                controlPanel?.EnsureTopmost();
                            }
                            renderer.DrainRingBuffer(ringBuffer, capture.DesktopWidth, capture.DesktopHeight, capture.OriginX, capture.OriginY);
                            _captureOriginX = capture.OriginX;
                            _captureOriginY = capture.OriginY;
                            _fallbackCursorWorldX = mouseWorldX;
                            _fallbackCursorWorldY = mouseWorldY;
                            renderer.Render(
                                capture,
                                _camera,
                                mouseWorldX,
                                mouseWorldY,
                                _vectorTextEnabled,
                                showVectorCursor: drawCursor,
                                gammaBoost: _gammaBoost,
                                edgeSmoothing: _edgeSmoothing,
                                cursorSizeScale: _cursorSizeScale,
                                invertCursor: _invertCursor,
                                colorMode: _colorMode,
                                mediaMode: _mediaMode,
                                cursorShape: cursorShape,
                                sampleFreshCursor: smoothCursor ? null : SampleFreshCursorDelegate);

                            lastPresentSeconds = stopwatch.Elapsed.TotalSeconds;
                            lastPresDesktopVer = capture.DesktopFrameVersion;
                            lastPresFocusX = camera.FocusX;
                            lastPresFocusY = camera.FocusY;
                            lastPresZoom = camera.Zoom;
                            lastPresCursorX = cursorScreenX;
                            lastPresCursorY = cursorScreenY;
                            lastPresShowCursor = drawCursor;
                            lastPresCursorShape = cursorShape;
                            lastPresCursorScale = _cursorSizeScale;
                            lastPresInvertCursor = _invertCursor;
                            lastPresColorMode = _colorMode;
                            lastPresMediaMode = _mediaMode;
                            lastPresEdgeSmoothing = _edgeSmoothing;
                            lastPresGamma = _gammaBoost;
                            lastPresVectorText = _vectorTextEnabled;
                            hasPresentedFrame = true;

                            UpdateCursorVisibility(drawCursor && capture.HasAcquiredFrame);
                        }
                        else if (allowIdleWait)
                        {
                            Volatile.Write(ref _mainThreadSleeping, 1);
                            if (!drawCursor || !GetCursorPos(out POINT recheck) || (recheck.X == lastPresCursorX && recheck.Y == lastPresCursorY))
                            {
                                MsgWaitForMultipleObjectsEx(0, nint.Zero, 4, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                            }
                            Volatile.Write(ref _mainThreadSleeping, 0);
                        }
                    }
                    else
                    {
                        hasPresentedFrame = false;
                        UpdateCursorVisibility(false);
                        if (IsWindowVisible(hwnd))
                        {
                            ShowWindow(hwnd, SW_HIDE);
                        }
                        if (allowIdleWait)
                        {
                            Thread.Sleep(16);
                        }
                    }
                }

                // Non-critical housekeeping runs strictly AFTER Present() so cursor draw is never delayed
                controlPanel!.UpdateZoomDisplay(camera.Zoom);
                controlPanel.NotifyTargetZoom(camera.TargetZoom);

                bool zoomAnimating = MathF.Abs(camera.TargetZoom - camera.Zoom) > 0.01f;
                if (!_overlayHiddenForShell && !zoomAnimating && nowTicks - lastZOrderCheckTicks >= 750)
                {
                    lastZOrderCheckTicks = nowTicks;
                    EnsureOverlayAbovePopups(hwnd, controlPanel, capture.OriginX, capture.OriginY, capture.DesktopWidth, capture.DesktopHeight, nowTicks, ref lastUnmovableTopWindow, ref lastUnmovableRetryTicks);
                }

                string hookState = !autoInjector.HookDllExists
                    ? "ZoomHook64.dll fehlt (C++-Build nötig)"
                    : autoInjector.LastHookError is not null
                        ? autoInjector.LastHookError
                        : autoInjector.IsHookActive
                            ? "Aktiv (Benutzermodus)"
                            : "Bereit (Benutzermodus)";
                if (!string.Equals(hookState, lastHookState, StringComparison.Ordinal) ||
                    renderer.ActiveVectorTextCount != lastTextCount ||
                    renderer.ConnectedProcessCount != lastProcCount ||
                    renderer.PacketsPerSecond != lastPps)
                {
                    lastHookState = hookState;
                    lastTextCount = renderer.ActiveVectorTextCount;
                    lastProcCount = renderer.ConnectedProcessCount;
                    lastPps = renderer.PacketsPerSecond;
                    controlPanel!.UpdateHookStatus(
                        $"Hook-Status: {hookState} | Vektor-Texte im Bild: {lastTextCount} (Prozesse: {lastProcCount}, {lastPps}/s)");
                }
            }

            renderStep = () => StepAndRenderFrame(allowIdleWait: false);

            // Register hotkeys, non-blocking keyboard raw input (replaces WH_KEYBOARD_LL), and minimal mouse-wheel hook.
            RegisterHotKeys(hwnd);
            RegisterKeyboardRawInput(hwnd);
            InstallHooks(hInstance);

            while (_running)
            {
                while (PeekMessage(out MSG msg, nint.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT)
                    {
                        _running = false;
                        break;
                    }

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (!_running)
                {
                    break;
                }

                StepAndRenderFrame(allowIdleWait: true);
            }
        }
        finally
        {
            try
            {
                System.Runtime.GCSettings.LatencyMode = prevGcMode;
            }
            catch
            {
            }
            timeEndPeriod(1);
            if (_magInvertApplied)
            {
                ApplyMagColorInvert(false);
            }
            if (_magFullscreenApplied)
            {
                MagSetFullscreenTransform(1.0f, 0, 0);
                _magFullscreenApplied = false;
            }
            RestoreSystemCursor();
            if (_magInitialized)
            {
                MagUninitialize();
                _magInitialized = false;
            }
            controlPanel?.CloseFromHost();
            _controlPanel = null;
            UninstallHooks();
            _focusTracker = null;
            _renderer = null;
            UnregisterHotKeys(hwnd);
            DestroyWindow(hwnd);
            UnregisterClass(className, hInstance);
        }
    }

    private static void RegisterKeyboardRawInput(nint hwnd)
    {
        // Asynchronous Raw Input notifies ZoomHost of typing for FocusTracker without installing
        // WH_KEYBOARD_LL, so every keystroke in Windows has 0 ms hook overhead.
        RAWINPUTDEVICE[] devices =
        [
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // Generic Desktop Controls
                usUsage = 0x06,     // Keyboard
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        ];
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private static void InstallHooks(nint hInstance)
    {
        _mainThreadId = GetCurrentThreadId();
        _mouseHookProc = MouseHookCallback;

        // Only install WH_MOUSE_LL (needed for Ctrl+Alt+Wheel zoom) on a dedicated message-only thread.
        // WH_KEYBOARD_LL is intentionally NOT installed so keyboard input is never intercepted synchronously.
        using ManualResetEventSlim ready = new(false);
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, hInstance, 0);
            ready.Set();

            while (GetMessage(out MSG msg, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_mouseHook != nint.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = nint.Zero;
            }
        })
        {
            IsBackground = true,
            Name = "LowLevelMouseHookThread",
            Priority = ThreadPriority.AboveNormal
        };
        _hookThread.Start();
        ready.Wait();
    }

    private static void UninstallHooks()
    {
        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, nint.Zero, nint.Zero);
            _hookThread?.Join(500);
            _hookThread = null;
            _hookThreadId = 0;
        }
    }

    private static unsafe nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam;
            // Fast-path exit for WM_MOUSEMOVE (99.9% of calls at up to 1000 Hz): only wake main loop if idle-sleeping.
            if (msg == WM_MOUSEMOVE)
            {
                if (Volatile.Read(ref _mainThreadSleeping) != 0 &&
                    Interlocked.Exchange(ref _mainThreadSleeping, 0) != 0)
                {
                    PostThreadMessage(_mainThreadId, WM_NULL, nint.Zero, nint.Zero);
                }
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (msg == WM_MOUSEWHEEL)
            {
                if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 &&
                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                {
                    MSLLHOOKSTRUCT* hookStruct = (MSLLHOOKSTRUCT*)lParam;
                    short wheelDelta = unchecked((short)((hookStruct->mouseData >> 16) & 0xFFFF));

                    if (wheelDelta > 0)
                    {
                        NotifyUserZoomAction();
                        _camera?.ZoomIn();
                        PostThreadMessage(_mainThreadId, WM_NULL, nint.Zero, nint.Zero);
                    }
                    else if (wheelDelta < 0)
                    {
                        NotifyUserZoomAction();
                        _camera?.ZoomOut();
                        PostThreadMessage(_mainThreadId, WM_NULL, nint.Zero, nint.Zero);
                    }

                    return 1;
                }
            }
            else if ((msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN) &&
                     Volatile.Read(ref _isZoomedForHooks) != 0)
            {
                Volatile.Write(ref _lastKeyboardOrClickTicks, Environment.TickCount64);
                _focusTracker?.NotifyMouseClick();
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static nint CreateTransparentCursor(nint hInstance)
    {
        // 32x32 1bpp monochrome cursor (128 bytes per plane, WORD-aligned)
        byte[] andMask = new byte[128];
        Array.Fill(andMask, (byte)0xFF);
        byte[] xorMask = new byte[128];
        return CreateCursor(hInstance, 0, 0, 32, 32, andMask, xorMask);
    }

    private static void UpdateCursorVisibility(bool hideSystemCursor)
    {
        if (hideSystemCursor)
        {
            long now = Environment.TickCount64;
            if (_systemCursorHidden && now - _lastCursorEnforceTicks < 1500)
            {
                return;
            }
            _lastCursorEnforceTicks = now;

            if (_magInitialized && MagShowSystemCursor(false))
            {
                _systemCursorHidden = true;
                return;
            }

            if (_systemCursorHidden && _usedFallbackCursorHide)
            {
                return;
            }

            bool anySet = false;
            foreach (uint ocrId in SystemCursorIds)
            {
                nint blank = CreateTransparentCursor(_hInstance);
                if (blank != nint.Zero)
                {
                    if (SetSystemCursor(blank, ocrId))
                    {
                        anySet = true;
                    }
                    else
                    {
                        DestroyCursor(blank);
                    }
                }
            }
            _usedFallbackCursorHide = anySet;
            _systemCursorHidden = anySet;
        }
        else if (_systemCursorHidden)
        {
            RestoreSystemCursor();
        }
    }

    private static void RestoreSystemCursor()
    {
        if (!_systemCursorHidden)
        {
            return;
        }

        if (_magInitialized)
        {
            MagShowSystemCursor(true);
        }

        if (_usedFallbackCursorHide)
        {
            SystemParametersInfo(SPI_SETCURSORS, 0, nint.Zero, 0);
            _usedFallbackCursorHide = false;
        }

        _systemCursorHidden = false;
    }

    private static void RegisterHotKeys(nint hwnd)
    {
        RegisterHotKey(hwnd, HOTKEY_ZOOM_IN_MAIN, MOD_ALT | MOD_NOREPEAT, VK_OEM_PLUS);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_IN_NUMPAD, MOD_ALT | MOD_NOREPEAT, VK_ADD);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_OUT_MAIN, MOD_ALT | MOD_NOREPEAT, VK_OEM_MINUS);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_OUT_NUMPAD, MOD_ALT | MOD_NOREPEAT, VK_SUBTRACT);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_RESET_MAIN, MOD_ALT | MOD_NOREPEAT, VK_0);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_RESET_NUMPAD, MOD_ALT | MOD_NOREPEAT, VK_NUMPAD0);
        RegisterHotKey(hwnd, HOTKEY_EXIT, MOD_ALT | MOD_NOREPEAT, VK_ESCAPE);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_RESET_CTRLALT_MAIN, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_0);
        RegisterHotKey(hwnd, HOTKEY_ZOOM_RESET_CTRLALT_NUMPAD, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_NUMPAD0);
        RegisterHotKey(hwnd, HOTKEY_INVERT_COLORS, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_I);
    }

    private static void UnregisterHotKeys(nint hwnd)
    {
        for (int id = HOTKEY_ZOOM_IN_MAIN; id <= HOTKEY_INVERT_COLORS; id++)
        {
            UnregisterHotKey(hwnd, id);
        }
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return HTTRANSPARENT;

            case WM_INPUT:
                bool modifierHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                if (Volatile.Read(ref _isZoomedForHooks) != 0 && !modifierHeld)
                {
                    Volatile.Write(ref _lastKeyboardOrClickTicks, Environment.TickCount64);
                    _focusTracker?.NotifyKeyboardActivity();
                }
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WM_HOTKEY:
                int id = (int)wParam;
                switch (id)
                {
                    case HOTKEY_ZOOM_IN_MAIN:
                    case HOTKEY_ZOOM_IN_NUMPAD:
                        NotifyUserZoomAction();
                        _camera?.ZoomIn();
                        break;
                    case HOTKEY_ZOOM_OUT_MAIN:
                    case HOTKEY_ZOOM_OUT_NUMPAD:
                        NotifyUserZoomAction();
                        _camera?.ZoomOut();
                        break;
                    case HOTKEY_ZOOM_RESET_MAIN:
                    case HOTKEY_ZOOM_RESET_NUMPAD:
                    case HOTKEY_ZOOM_RESET_CTRLALT_MAIN:
                    case HOTKEY_ZOOM_RESET_CTRLALT_NUMPAD:
                        NotifyUserZoomAction();
                        _camera?.ResetZoom();
                        break;
                    case HOTKEY_INVERT_COLORS:
                        if (_colorMode == 0)
                        {
                            _colorMode = _lastNonZeroColorMode != 0 ? _lastNonZeroColorMode : 1;
                        }
                        else
                        {
                            _lastNonZeroColorMode = _colorMode;
                            _colorMode = 0;
                        }
                        _controlPanel?.SetColorMode(_colorMode);
                        break;
                    case HOTKEY_EXIT:
                        _running = false;
                        PostQuitMessage(0);
                        break;
                }
                return nint.Zero;

            case WM_SIZE:
                int newWidth = (int)(lParam & 0xFFFF);
                int newHeight = (int)((lParam >> 16) & 0xFFFF);
                if (newWidth > 0 && newHeight > 0)
                {
                    _camera?.Resize(newWidth, newHeight);
                    _renderer?.Resize(newWidth, newHeight);
                }
                return nint.Zero;

            case WM_DESTROY:
                _running = false;
                PostQuitMessage(0);
                return nint.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static readonly string[] CrossBandShellClasses =
    [
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostWindow",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
        "ForegroundStaging"
    ];

    private static long _lastCrossBandScanTicks;

    private static void UpdateShellPopupState(
        nint overlayHwnd,
        nint unmovableTopWindow,
        ControlPanelWindow controlPanel,
        int originX,
        int originY,
        int desktopW,
        int desktopH)
    {
        // With Windows UIAccess (ZBID_UIACCESS), our vector overlay sits strictly ABOVE the Start Menu,
        // Search, and Shell popups — keep the D3D11 vector shader active (never fall back to bitmap).
        if (_isUiAccessProcess)
        {
            _activeShellHwnd = nint.Zero;
            return;
        }

        nint fg = GetForegroundWindow();
        if (unmovableTopWindow != nint.Zero &&
            IsShellOverlayWindow(unmovableTopWindow, originX, originY, desktopW, desktopH, fg))
        {
            _activeShellHwnd = unmovableTopWindow;
            return;
        }

        if (_activeShellHwnd != nint.Zero &&
            IsShellOverlayWindow(_activeShellHwnd, originX, originY, desktopW, desktopH, fg))
        {
            return;
        }

        nint controlPanelHwnd = controlPanel.WindowHandle;

        // 1) Fast O(1) foreground check: catches Start Menu (StartMenuExperienceHost), Search (SearchHost),
        //    Quick Settings (ShellHost/ShellExperienceHost), Emoji (TextInputHost), and Alt+Tab immediately.
        if (fg != nint.Zero && fg != overlayHwnd && fg != controlPanelHwnd &&
            IsShellOverlayWindow(fg, originX, originY, desktopW, desktopH, fg))
        {
            _activeShellHwnd = fg;
            return;
        }

        long nowTicks = Environment.TickCount64;
        if (_activeShellHwnd == nint.Zero && nowTicks - _lastCrossBandScanTicks < 250)
        {
            return;
        }
        _lastCrossBandScanTicks = nowTicks;

        // 2) Cross-band lookup via FindWindowEx: on Windows 10/11, EnumWindows and GetWindow(GW_HWNDPREV)
        //    explicitly filter out ZBID_IMMERSIVE bands (StartMenuExperienceHost / SearchHost) for non-UIAccess
        //    processes! FindWindowEx crosses all ZBID bands so we reliably detect an open, uncloaked Start Menu
        //    even after focus moves (e.g. clicking the ZoomHost panel or pressing zoom hotkeys).
        uint selfPid = (uint)Environment.ProcessId;
        foreach (string cls in CrossBandShellClasses)
        {
            nint h = nint.Zero;
            for (int i = 0; i < 64; i++)
            {
                h = FindWindowEx(nint.Zero, h, cls, null);
                if (h == nint.Zero)
                {
                    break;
                }
                if (h == overlayHwnd || h == controlPanelHwnd)
                {
                    continue;
                }
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0 || pid == selfPid)
                {
                    continue;
                }
                if (IsShellOverlayWindow(h, originX, originY, desktopW, desktopH, fg))
                {
                    _activeShellHwnd = h;
                    return;
                }
            }
        }

        _activeShellHwnd = nint.Zero;
    }

    private static bool IsCurrentProcessUiAccess()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out nint token))
        {
            return false;
        }

        try
        {
            return GetTokenInformation(token, TOKEN_INFORMATION_UIACCESS, out uint uiAccess, sizeof(uint), out _) &&
                   uiAccess != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static unsafe void ApplyMagColorInvert(bool invert)
    {
        if (!_magInitialized || _magInvertApplied == invert)
        {
            return;
        }

        MAGCOLOREFFECT effect = default;
        if (invert)
        {
            effect.transform[0] = -1.0f;
            effect.transform[6] = -1.0f;
            effect.transform[12] = -1.0f;
            effect.transform[18] = 1.0f;
            effect.transform[20] = 1.0f;
            effect.transform[21] = 1.0f;
            effect.transform[22] = 1.0f;
            effect.transform[24] = 1.0f;
        }
        else
        {
            effect.transform[0] = 1.0f;
            effect.transform[6] = 1.0f;
            effect.transform[12] = 1.0f;
            effect.transform[18] = 1.0f;
            effect.transform[24] = 1.0f;
        }

        if (MagSetFullscreenColorEffect(ref effect))
        {
            _magInvertApplied = invert;
        }
    }

    private static void ExitShellMagnifierMode(nint overlayHwnd, ControlPanelWindow controlPanel)
    {
        if (_magInvertApplied)
        {
            ApplyMagColorInvert(false);
        }

        if (_magFullscreenApplied && _magInitialized)
        {
            MagSetFullscreenTransform(1.0f, 0, 0);
            _magFullscreenApplied = false;
        }

        if (_overlayHiddenForShell)
        {
            ShowWindow(overlayHwnd, SW_SHOWNA);
            SetWindowPos(overlayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            controlPanel.EnsureTopmost();
            _overlayHiddenForShell = false;
        }
    }

    private static bool IsWindowCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static readonly System.Text.StringBuilder _shellClassNameBuf = new(128);
    private static readonly System.Text.StringBuilder _procImageBuf = new(260);
    private static readonly System.Collections.Generic.Dictionary<uint, string> _procNameCache = new(64);

    private static string GetCachedProcessBaseName(uint pid)
    {
        if (_procNameCache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string name = string.Empty;
        nint hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != nint.Zero)
        {
            try
            {
                uint size = (uint)_procImageBuf.Capacity;
                _procImageBuf.Clear();
                if (QueryFullProcessImageName(hProc, 0, _procImageBuf, ref size) && size > 0)
                {
                    name = System.IO.Path.GetFileNameWithoutExtension(_procImageBuf.ToString());
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
        else
        {
            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                name = proc.ProcessName;
            }
            catch
            {
                name = string.Empty;
            }
        }

        if (_procNameCache.Count >= 64)
        {
            _procNameCache.Clear();
        }
        _procNameCache[pid] = name;
        return name;
    }

    private static bool IsShellOverlayWindow(nint targetHwnd, int originX, int originY, int desktopW, int desktopH, nint fgHwnd)
    {
        if (targetHwnd == nint.Zero || !IsWindow(targetHwnd) || !IsWindowVisible(targetHwnd) || IsWindowCloaked(targetHwnd))
        {
            return false;
        }

        if (!GetWindowRect(targetHwnd, out RECT rect) ||
            (rect.Right - rect.Left) <= 8 ||
            (rect.Bottom - rect.Top) <= 8 ||
            rect.Right <= originX ||
            rect.Bottom <= originY ||
            rect.Left >= originX + desktopW ||
            rect.Top >= originY + desktopH)
        {
            return false;
        }

        _shellClassNameBuf.Clear();
        GetClassName(targetHwnd, _shellClassNameBuf, _shellClassNameBuf.Capacity);
        string className = _shellClassNameBuf.ToString();
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
        {
            return false;
        }

        if (className is "MultitaskingViewFrame" or "XamlExplorerHostWindow" or "TaskSwitcherWnd" or "ForegroundStaging")
        {
            return true;
        }

        GetWindowThreadProcessId(targetHwnd, out uint pid);
        if (pid == 0)
        {
            return false;
        }

        string name = GetCachedProcessBaseName(pid);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (string.Equals(name, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "SearchApp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "SearchUI", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "LockApp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (targetHwnd == fgHwnd &&
            (string.Equals(name, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, "ShellHost", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, "TextInputHost", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) &&
            className.Contains("Multitasking", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void EnsureOverlayAbovePopups(
        nint overlayHwnd,
        ControlPanelWindow controlPanel,
        int originX,
        int originY,
        int desktopW,
        int desktopH,
        long nowTicks,
        ref nint lastUnmovableTopWindow,
        ref long lastUnmovableRetryTicks)
    {
        nint windowAbove = FindVisibleWindowAboveOverlay(overlayHwnd, controlPanel.WindowHandle, originX, originY, desktopW, desktopH);
        if (windowAbove == nint.Zero)
        {
            lastUnmovableTopWindow = nint.Zero;
            return;
        }

        if (windowAbove == lastUnmovableTopWindow && nowTicks - lastUnmovableRetryTicks < 250)
        {
            return;
        }

        lastUnmovableRetryTicks = nowTicks;
        SetWindowPos(overlayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        controlPanel.EnsureTopmost();

        nint stillAbove = FindVisibleWindowAboveOverlay(overlayHwnd, controlPanel.WindowHandle, originX, originY, desktopW, desktopH);
        lastUnmovableTopWindow = (stillAbove == windowAbove && stillAbove != controlPanel.WindowHandle) ? stillAbove : nint.Zero;
    }

    private static bool IsPointInExpandedWindowRect(nint hwnd, int screenX, int screenY, int marginPx)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out RECT rect))
        {
            return false;
        }

        return screenX >= rect.Left - marginPx &&
               screenX <= rect.Right + marginPx &&
               screenY >= rect.Top - marginPx &&
               screenY <= rect.Bottom + marginPx;
    }

    private static nint FindVisibleWindowAboveOverlay(nint overlayHwnd, nint controlPanelHwnd, int originX, int originY, int desktopW, int desktopH)
    {
        uint selfPid = (uint)Environment.ProcessId;
        nint current = overlayHwnd;

        for (int i = 0; i < 256; i++)
        {
            nint prev = GetWindow(current, GW_HWNDPREV);
            if (prev == nint.Zero)
            {
                break;
            }

            current = prev;

            if (!IsWindowVisible(prev))
            {
                continue;
            }

            if (prev == controlPanelHwnd)
            {
                // Keep the dialog strictly below overlayHwnd so Desktop Duplication always zooms it with the screen
                return prev;
            }

            GetWindowThreadProcessId(prev, out uint pid);
            if (pid == selfPid)
            {
                continue;
            }

            _shellClassNameBuf.Clear();
            GetClassName(prev, _shellClassNameBuf, _shellClassNameBuf.Capacity);
            string prevClass = _shellClassNameBuf.ToString();
            if (prevClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                continue;
            }

            if (GetWindowRect(prev, out RECT rect) &&
                (rect.Right - rect.Left) > 4 &&
                (rect.Bottom - rect.Top) > 4 &&
                rect.Right > originX &&
                rect.Bottom > originY &&
                rect.Left < originX + desktopW &&
                rect.Top < originY + desktopH &&
                (DwmGetWindowAttribute(prev, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0))
            {
                return prev;
            }
        }

        return nint.Zero;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate nint LowLevelHookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
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
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount,
        nint pHandles,
        uint dwMilliseconds,
        uint dwWakeMask,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(nint hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyCursor(nint hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(nint hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("magnification.dll", SetLastError = true)]
    private static extern bool MagInitialize();

    [DllImport("magnification.dll", SetLastError = true)]
    private static extern bool MagUninitialize();

    [DllImport("magnification.dll", SetLastError = true)]
    private static extern bool MagShowSystemCursor(bool show);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MAGCOLOREFFECT
    {
        public fixed float transform[25];
    }

    [DllImport("magnification.dll", SetLastError = true)]
    private static extern bool MagSetFullscreenTransform(float magLevel, int xOffset, int yOffset);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TOKEN_INFORMATION_UIACCESS = 26;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint TokenHandle, int TokenInformationClass, out uint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("magnification.dll", SetLastError = true)]
    private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);
}
