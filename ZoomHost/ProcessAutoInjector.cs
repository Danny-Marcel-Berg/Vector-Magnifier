using System.IO;
using System.Runtime.InteropServices;

namespace ZoomHost;

/// <summary>
/// Manages user-mode global DLL injection of <c>ZoomHook64.dll</c> via
/// <c>SetWindowsHookExW(WH_CALLWNDPROC, ...)</c> without requiring administrator privileges.
/// </summary>
public sealed class ProcessAutoInjector : IDisposable
{
    private const int WH_CALLWNDPROC = 4;
    private const uint DWMWA_CLOAKED = 14;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;

    private readonly nint _overlayHwnd;
    private readonly nint _controlPanelHwnd;
    private readonly string _hookDllPath;
    private readonly HashSet<nint> _invalidatedHwnds = [];

    private readonly bool _hookDllExists;

    private nint _hHookModule;
    private nint _hWindowsHook;
    private string? _lastHookError;

    public bool HookDllExists => _hookDllExists;
    public bool IsHookActive => _hWindowsHook != nint.Zero;
    public string? LastHookError => _lastHookError;

    public ProcessAutoInjector(nint overlayHwnd, nint controlPanelHwnd)
    {
        _overlayHwnd = overlayHwnd;
        _controlPanelHwnd = controlPanelHwnd;
        _hookDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZoomHook64.dll");
        _hookDllExists = File.Exists(_hookDllPath);
    }

    public void EnsureHookActive()
    {
        if (_hWindowsHook != nint.Zero)
        {
            return;
        }

        if (!_hookDllExists && !File.Exists(_hookDllPath))
        {
            _lastHookError = "ZoomHook64.dll fehlt (C++-Build nötig)";
            return;
        }

        if (_hHookModule == nint.Zero)
        {
            _hHookModule = LoadLibraryW(_hookDllPath);
            if (_hHookModule == nint.Zero)
            {
                _lastHookError = $"LoadLibraryW Fehler ({Marshal.GetLastWin32Error()})";
                return;
            }
        }

        nint hookProcAddr = GetProcAddress(_hHookModule, "HookProc");
        if (hookProcAddr == nint.Zero)
        {
            _lastHookError = $"HookProc nicht gefunden ({Marshal.GetLastWin32Error()})";
            FreeLibrary(_hHookModule);
            _hHookModule = nint.Zero;
            return;
        }

        _hWindowsHook = SetWindowsHookExW(WH_CALLWNDPROC, hookProcAddr, _hHookModule, 0);
        if (_hWindowsHook == nint.Zero)
        {
            _lastHookError = $"SetWindowsHookExW Fehler ({Marshal.GetLastWin32Error()})";
            FreeLibrary(_hHookModule);
            _hHookModule = nint.Zero;
            return;
        }

        _lastHookError = null;
    }

    public void DisableHook()
    {
        _invalidatedHwnds.Clear();

        if (_hWindowsHook != nint.Zero)
        {
            UnhookWindowsHookEx(_hWindowsHook);
            _hWindowsHook = nint.Zero;
        }

        if (_hHookModule != nint.Zero)
        {
            FreeLibrary(_hHookModule);
            _hHookModule = nint.Zero;
        }
    }

    public void RedrawVisibleWindows(WorldBounds visibleBounds, int originX, int originY, bool onlyNewWindows = false)
    {
        List<nint> hwnds = GetVisibleIntersectingHwnds(visibleBounds, originX, originY);
        if (hwnds.Count == 0)
        {
            return;
        }

        List<nint> targets = [];
        foreach (nint hwnd in hwnds)
        {
            if (!onlyNewWindows || _invalidatedHwnds.Add(hwnd))
            {
                _invalidatedHwnds.Add(hwnd);
                targets.Add(hwnd);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (nint hwnd in targets)
            {
                // Wake up target window's message thread so WH_CALLWNDPROC immediately loads ZoomHook64.dll
                SendMessageTimeoutW(hwnd, 0x0000, nint.Zero, nint.Zero, 0x0002, 40, out _);
                InvalidateRect(hwnd, nint.Zero, true);
                RedrawWindow(hwnd, nint.Zero, nint.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            }
        });
    }

    private List<nint> GetVisibleIntersectingHwnds(WorldBounds visibleBounds, int originX, int originY)
    {
        List<nint> hwnds = [];
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == _overlayHwnd || hwnd == _controlPanelHwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return true;
            }

            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out RECT rect))
            {
                return true;
            }

            float left = rect.Left - originX;
            float top = rect.Top - originY;
            float right = rect.Right - originX;
            float bottom = rect.Bottom - originY;

            if (right > left && bottom > top && visibleBounds.Intersects(left, top, right, bottom))
            {
                hwnds.Add(hwnd);
            }

            return true;
        }, nint.Zero);

        return hwnds;
    }

    public void Dispose()
    {
        DisableHook();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(nint hLibModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string lpProcName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(nint hWnd, nint lprcUpdate, nint hrgnUpdate, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);
}
