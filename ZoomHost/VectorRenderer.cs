using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace ZoomHost;

/// <summary>
/// Renders the captured desktop texture via Direct2D (Pass 1) and overlays crisp
/// mathematical DirectWrite vector text, high-resolution icons, and vector lines/outlines
/// from shared memory (Pass 2) using the exact same <see cref="CameraController.WorldToScreenMatrix"/>.
/// </summary>
public sealed class VectorRenderer : IDisposable
{
    private sealed class ManagedVectorPacket
    {
        public uint ProcessId;
        public ulong Hwnd;
        public ulong FrameId;
        public VectorPacketKind Kind;
        public float ScreenX;
        public float ScreenY;
        public float BoundsLeft;
        public float BoundsTop;
        public float BoundsRight;
        public float BoundsBottom;
        public float ClipLeft;
        public float ClipTop;
        public float ClipRight;
        public float ClipBottom;
        public float FontEmSize;
        public Color4 TextColor;
        public Color4 BgColor;
        public FontWeight FontWeight;
        public FontStyle FontStyle;
        public string FontFamily = "Segoe UI";
        public string Text = string.Empty;
        public ushort[]? GlyphIndices;
        public float[]? GlyphAdvances;
        public nint IconHandle;
    }

    private sealed class HwndFrameBucket
    {
        public uint ProcessId;
        public ulong FrameId;
        public RECT LastWindowRect;
        public bool HasWindowRect;
        public List<ManagedVectorPacket> Packets { get; } = [];
    }

    private readonly record struct FontFaceKey(string Family, FontWeight Weight, FontStyle Style);
    private readonly record struct TextFormatKey(string Family, FontWeight Weight, FontStyle Style, float EmSize);

    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly bool _isHdrDesktop;
    private readonly Format _swapFormat;
    private readonly float _sdrWhiteScale;
    private readonly EdgeSmoothingShader _edgeShader;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2dContext;
    private readonly IDWriteFactory _dwriteFactory;
    private readonly IDWriteFontCollection _fontCollection;
    private readonly ID2D1SolidColorBrush _vectorBrush;
    private readonly ID2D1SolidColorBrush _cursorFillBrush;
    private readonly ID2D1SolidColorBrush _cursorStrokeBrush;
    private readonly ID2D1SolidColorBrush _invertedCursorFillBrush;
    private readonly ID2D1SolidColorBrush _invertedCursorStrokeBrush;
    private readonly ID2D1PathGeometry _cursorGeometry;
    private readonly IDXGISwapChain1 _swapChain;

    private readonly Dictionary<ulong, HwndFrameBucket> _activePacketsByHwnd = [];
    private readonly Dictionary<FontFaceKey, IDWriteFontFace?> _fontFaceCache = [];
    private readonly Dictionary<TextFormatKey, IDWriteTextFormat> _textFormatCache = [];
    private readonly Dictionary<nint, ID2D1Bitmap?> _iconBitmapCache = [];
    private readonly HashSet<uint> _activeProcessIds = [];

    private ID3D11Texture2D? _backBufferTex;
    private ID3D11RenderTargetView? _backBufferRtv;
    private ID3D11Texture2D? _cachedZoomTex;
    private ID3D11RenderTargetView? _cachedZoomRtv;
    private ID2D1Bitmap1? _targetBitmap;
    private ID2D1Bitmap1? _desktopBitmap;
    private ID3D11Texture2D? _trackedTexture;
    private IDWriteRenderingParams? _textRenderingParams;
    private float _appliedTextGamma = float.NaN;
    private nint _frameLatencyWaitable;
    private bool _hasCachedZoomFrame;
    private long _cachedDesktopVersion = -1;
    private int _cachedDesktopW;
    private int _cachedDesktopH;
    private int _cachedBackBufferW;
    private int _cachedBackBufferH;
    private float _cachedFocusX;
    private float _cachedFocusY;
    private float _cachedZoom;
    private float _cachedEdgeSmoothing;
    private float _cachedGammaBoost;
    private int _cachedColorMode;
    private int _cachedMediaMode;

    private long _lastValidationTicks;
    private long _lastPpsTicks = Environment.TickCount64;
    private int _packetsThisSecond;
    private bool _disposed;
    private float _prevCursorFocusX = float.NaN;
    private float _prevCursorFocusY = float.NaN;
    private float _prevCursorZoom = float.NaN;
    private int _consecutiveDesktopUpdates;
    private long _lastSeenDesktopVersion = -1;

    public long TotalReceivedPackets { get; private set; }
    public int ActiveVectorTextCount { get; private set; }
    public int PacketsPerSecond { get; private set; }
    public int ConnectedProcessCount => _activeProcessIds.Count;

    public VectorRenderer(nint hwnd, CaptureEngine capture, int width, int height)
    {
        _d3dDevice = capture.Device;
        _d3dContext = capture.Context;
        // Always present through a standard B8G8R8A8_UNorm sRGB swapchain (even on HDR monitors!).
        // CaptureEngine still captures HDR desktops in R16G16B16A16_Float (zero highlight clipping),
        // and EdgeSmoothing.hlsl converts scRGB -> exact Windows piecewise sRGB using the exact
        // DISPLAYCONFIG SDRWhiteLevel, so DWM displays the layered overlay with 100% identical
        // brightness, gamma, black/white levels, and colours as every other window on screen.
        _isHdrDesktop = false;
        _swapFormat = Format.B8G8R8A8_UNorm;
        _sdrWhiteScale = 1.0f;

        using (IDXGIDevice1 dxgiDev1 = capture.DxgiDevice.QueryInterface<IDXGIDevice1>())
        {
            dxgiDev1.MaximumFrameLatency = 1;
        }
        _edgeShader = new EdgeSmoothingShader(capture.Device, capture.Context);

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(capture.DxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.UnitMode = UnitMode.Pixels;
        _d2dContext.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _fontCollection = _dwriteFactory.GetSystemFontCollection(false);
        _vectorBrush = _d2dContext.CreateSolidColorBrush(ToRenderColor(new Color4(1.0f, 1.0f, 1.0f, 1.0f)));
        _cursorFillBrush = _d2dContext.CreateSolidColorBrush(ToRenderColor(new Color4(1.0f, 1.0f, 1.0f, 1.0f)));
        _cursorStrokeBrush = _d2dContext.CreateSolidColorBrush(ToRenderColor(new Color4(0.05f, 0.05f, 0.05f, 1.0f)));
        _invertedCursorFillBrush = _d2dContext.CreateSolidColorBrush(ToRenderColor(new Color4(0.03f, 0.03f, 0.04f, 1.0f)));
        _invertedCursorStrokeBrush = _d2dContext.CreateSolidColorBrush(ToRenderColor(new Color4(1.0f, 1.0f, 1.0f, 1.0f)));

        _cursorGeometry = CreateCursorGeometry(_d2dFactory);

        using IDXGIFactory2 dxgiFactory = capture.DxgiAdapter.GetParent<IDXGIFactory2>();

        SwapChainDescription1 swapChainDesc = new()
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            Format = _swapFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 3,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject
        };

        _swapChain = dxgiFactory.CreateSwapChainForHwnd(capture.Device, hwnd, swapChainDesc);
        using (IDXGISwapChain2? sc2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
        {
            if (sc2 is not null)
            {
                sc2.MaximumFrameLatency = 1;
                _frameLatencyWaitable = sc2.FrameLatencyWaitableObject;
            }
        }
        CreateSwapChainTargetBitmap();
    }

    public void WaitForNextFrame()
    {
        if (!_disposed && _frameLatencyWaitable != nint.Zero)
        {
            WaitForSingleObjectEx(_frameLatencyWaitable, 32, false);
        }
    }

    private Color4 ToRenderColor(Color4 srgb)
    {
        if (!_isHdrDesktop)
        {
            return srgb;
        }
        return new Color4(
            SrgbToLinear(srgb.R) * _sdrWhiteScale,
            SrgbToLinear(srgb.G) * _sdrWhiteScale,
            SrgbToLinear(srgb.B) * _sdrWhiteScale,
            srgb.A);
    }

    private static float SrgbToLinear(float c)
    {
        float clamped = Math.Clamp(c, 0.0f, 1.0f);
        return clamped <= 0.04045f ? clamped / 12.92f : MathF.Pow((clamped + 0.055f) / 1.055f, 2.4f);
    }

    private static ID2D1PathGeometry CreateCursorGeometry(ID2D1Factory1 factory)
    {
        ID2D1PathGeometry geometry = factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();
        sink.BeginFigure(new Vector2(0.0f, 0.0f), FigureBegin.Filled);
        sink.AddLine(new Vector2(0.0f, 17.0f));
        sink.AddLine(new Vector2(4.5f, 13.0f));
        sink.AddLine(new Vector2(7.8f, 20.5f));
        sink.AddLine(new Vector2(10.5f, 19.2f));
        sink.AddLine(new Vector2(7.2f, 11.8f));
        sink.AddLine(new Vector2(12.5f, 11.8f));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geometry;
    }

    private void CreateSwapChainTargetBitmap()
    {
        _d2dContext.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;
        _backBufferTex?.Dispose();
        _backBufferTex = null;

        _backBufferTex = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = _d3dDevice.CreateRenderTargetView(_backBufferTex);

        using IDXGISurface dxgiBackBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        BitmapProperties1 targetProps = new(
            new PixelFormat(_swapFormat, AlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);

        _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(dxgiBackBuffer, targetProps);
        _d2dContext.Target = _targetBitmap;
    }

    public void Resize(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
        {
            return;
        }

        _d2dContext.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;
        _backBufferTex?.Dispose();
        _backBufferTex = null;
        _cachedZoomRtv?.Dispose();
        _cachedZoomRtv = null;
        _cachedZoomTex?.Dispose();
        _cachedZoomTex = null;
        _hasCachedZoomFrame = false;

        _swapChain.ResizeBuffers(3, (uint)width, (uint)height, _swapFormat, SwapChainFlags.FrameLatencyWaitableObject);
        CreateSwapChainTargetBitmap();
    }

    private void EnsureCachedZoomTarget(int bbW, int bbH)
    {
        if (_cachedZoomTex is not null && _cachedBackBufferW == bbW && _cachedBackBufferH == bbH)
        {
            return;
        }

        _cachedZoomRtv?.Dispose();
        _cachedZoomRtv = null;
        _cachedZoomTex?.Dispose();
        Texture2DDescription cacheDesc = new()
        {
            Width = (uint)bbW,
            Height = (uint)bbH,
            MipLevels = 1,
            ArraySize = 1,
            Format = _swapFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        _cachedZoomTex = _d3dDevice.CreateTexture2D(cacheDesc);
        _cachedZoomRtv = _d3dDevice.CreateRenderTargetView(_cachedZoomTex);
        _cachedBackBufferW = bbW;
        _cachedBackBufferH = bbH;
    }

    private void EnsureDesktopBitmap(ID3D11Texture2D? sharedTexture)
    {
        if (sharedTexture is null || ReferenceEquals(sharedTexture, _trackedTexture))
        {
            return;
        }

        _desktopBitmap?.Dispose();
        _desktopBitmap = null;
        _trackedTexture = sharedTexture;

        using IDXGISurface dxgiSurface = sharedTexture.QueryInterface<IDXGISurface>();
        BitmapProperties1 srcProps = new(
            new PixelFormat(sharedTexture.Description.Format, AlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.None);

        _desktopBitmap = _d2dContext.CreateBitmapFromDxgiSurface(dxgiSurface, srcProps);
    }

    public void ClearAllVectorPackets()
    {
        _activePacketsByHwnd.Clear();
        ActiveVectorTextCount = 0;
    }

    private static bool BoxesOverlap(in ManagedVectorPacket a, in VectorTextPacket b)
    {
        if (a.Kind != (VectorPacketKind)b.PacketKind)
        {
            return false;
        }

        if (MathF.Abs(a.ScreenX - b.ScreenX) < 2.0f && MathF.Abs(a.ScreenY - b.ScreenY) < 2.0f)
        {
            return true;
        }

        float overlapX = MathF.Min(a.BoundsRight, b.BoundsRight) - MathF.Max(a.BoundsLeft, b.BoundsLeft);
        float overlapY = MathF.Min(a.BoundsBottom, b.BoundsBottom) - MathF.Max(a.BoundsTop, b.BoundsTop);
        if (overlapX <= 1.0f || overlapY <= 1.0f)
        {
            return false;
        }

        float minH = MathF.Max(1.0f, MathF.Min(a.BoundsBottom - a.BoundsTop, b.BoundsBottom - b.BoundsTop));
        return overlapY >= minH * 0.5f;
    }

    public unsafe void DrainRingBuffer(SharedMemoryRingBuffer ringBuffer, int desktopWidth, int desktopHeight, int originX = 0, int originY = 0)
    {
        long nowTicks = Environment.TickCount64;

        while (ringBuffer.TryRead(out VectorTextPacket raw))
        {
            raw.ScreenX -= originX;
            raw.ScreenY -= originY;
            raw.BoundsLeft -= originX;
            raw.BoundsTop -= originY;
            raw.BoundsRight -= originX;
            raw.BoundsBottom -= originY;
            if (raw.ClipRight > raw.ClipLeft)
            {
                raw.ClipLeft -= originX;
                raw.ClipTop -= originY;
                raw.ClipRight -= originX;
                raw.ClipBottom -= originY;
            }
            VectorPacketKind kind = (VectorPacketKind)raw.PacketKind;

            float boxWidth = raw.BoundsRight - raw.BoundsLeft;
            float boxHeight = raw.BoundsBottom - raw.BoundsTop;

            if (raw.BoundsLeft < -50.0f ||
                raw.BoundsTop < -50.0f ||
                raw.BoundsRight > desktopWidth + 50.0f ||
                raw.BoundsBottom > desktopHeight + 50.0f ||
                boxWidth <= 0.5f ||
                boxWidth >= 2600.0f ||
                boxHeight <= 0.5f ||
                boxHeight >= 600.0f)
            {
                continue;
            }

            if (kind == VectorPacketKind.Text && (raw.FontEmSize <= 3.0f || raw.FontEmSize >= 160.0f))
            {
                continue;
            }

            if (raw.ProcessId > 0)
            {
                _activeProcessIds.Add(raw.ProcessId);
            }

            if (!_activePacketsByHwnd.TryGetValue(raw.Hwnd, out HwndFrameBucket? bucket))
            {
                bucket = new HwndFrameBucket
                {
                    ProcessId = raw.ProcessId,
                    FrameId = raw.FrameId
                };
                if (raw.Hwnd != 0 && GetWindowRect((nint)raw.Hwnd, out RECT winRect))
                {
                    bucket.LastWindowRect = winRect;
                    bucket.HasWindowRect = true;
                }
                _activePacketsByHwnd[raw.Hwnd] = bucket;
            }
            else
            {
                if (raw.FrameId > bucket.FrameId)
                {
                    bucket.FrameId = raw.FrameId;
                }

                // Replace any older packet in the same HWND occupying the same screen position/line
                for (int i = bucket.Packets.Count - 1; i >= 0; i--)
                {
                    if (BoxesOverlap(bucket.Packets[i], in raw))
                    {
                        bucket.Packets.RemoveAt(i);
                    }
                }

                if (bucket.Packets.Count > 512)
                {
                    bucket.Packets.RemoveRange(0, bucket.Packets.Count - 512);
                }
            }

            ManagedVectorPacket managed = new()
            {
                ProcessId = raw.ProcessId,
                Hwnd = raw.Hwnd,
                FrameId = raw.FrameId,
                Kind = kind,
                ScreenX = raw.ScreenX,
                ScreenY = raw.ScreenY,
                BoundsLeft = raw.BoundsLeft,
                BoundsTop = raw.BoundsTop,
                BoundsRight = raw.BoundsRight,
                BoundsBottom = raw.BoundsBottom,
                ClipLeft = raw.ClipLeft,
                ClipTop = raw.ClipTop,
                ClipRight = raw.ClipRight,
                ClipBottom = raw.ClipBottom,
                FontEmSize = MathF.Max(1.0f, raw.FontEmSize),
                TextColor = RgbaToColor4(raw.TextColorRGBA),
                BgColor = RgbaToColor4(raw.BgColorRGBA),
                FontWeight = (FontWeight)(raw.FontWeight > 0 ? raw.FontWeight : 400),
                FontStyle = (FontStyle)Math.Clamp(raw.FontStyle, 0, 2)
            };

            if (kind == VectorPacketKind.Text)
            {
                string family = VectorTextPacket.ReadFixedString(raw.FontFamily, VectorTextPacket.MaxFontFamilyLength);
                string text = VectorTextPacket.ReadFixedString(raw.Text, VectorTextPacket.MaxTextLength);

                if (raw.GlyphCount <= 0 && string.IsNullOrEmpty(text))
                {
                    continue;
                }

                int glyphCount = Math.Clamp(raw.GlyphCount, 0, VectorTextPacket.MaxGlyphCount);
                if (glyphCount > 0)
                {
                    ushort[] indices = new ushort[glyphCount];
                    float[] advances = new float[glyphCount];
                    for (int i = 0; i < glyphCount; i++)
                    {
                        indices[i] = raw.GlyphIndices[i];
                        advances[i] = raw.GlyphAdvances[i];
                    }
                    managed.GlyphIndices = indices;
                    managed.GlyphAdvances = advances;
                }

                managed.FontFamily = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family;
                managed.Text = text;
            }
            else if (kind == VectorPacketKind.Icon)
            {
                ulong iconVal =
                    (ulong)raw.GlyphIndices[0] |
                    ((ulong)raw.GlyphIndices[1] << 16) |
                    ((ulong)raw.GlyphIndices[2] << 32) |
                    ((ulong)raw.GlyphIndices[3] << 48);
                managed.IconHandle = (nint)iconVal;
            }
            else if (kind == VectorPacketKind.Line)
            {
                managed.GlyphAdvances = [raw.GlyphAdvances[0], raw.GlyphAdvances[1]];
            }

            bucket.Packets.Add(managed);
            TotalReceivedPackets++;
            _packetsThisSecond++;
        }

        if (nowTicks - _lastPpsTicks >= 1000)
        {
            PacketsPerSecond = _packetsThisSecond;
            _packetsThisSecond = 0;
            _lastPpsTicks = nowTicks;
        }

        if (nowTicks - _lastValidationTicks >= 150)
        {
            _lastValidationTicks = nowTicks;
            ValidateActiveWindows();
        }
    }

    private void ValidateActiveWindows()
    {
        if (_activePacketsByHwnd.Count == 0)
        {
            return;
        }

        List<ulong>? toRemove = null;
        foreach ((ulong hwndVal, HwndFrameBucket bucket) in _activePacketsByHwnd)
        {
            if (hwndVal == 0)
            {
                continue;
            }

            nint hwnd = (nint)hwndVal;
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
            {
                toRemove ??= [];
                toRemove.Add(hwndVal);
                continue;
            }

            if (GetWindowRect(hwnd, out RECT currentRect))
            {
                if (bucket.HasWindowRect &&
                    (currentRect.Left != bucket.LastWindowRect.Left ||
                     currentRect.Top != bucket.LastWindowRect.Top ||
                     currentRect.Right != bucket.LastWindowRect.Right ||
                     currentRect.Bottom != bucket.LastWindowRect.Bottom))
                {
                    int dx = currentRect.Left - bucket.LastWindowRect.Left;
                    int dy = currentRect.Top - bucket.LastWindowRect.Top;
                    foreach (ManagedVectorPacket pkt in bucket.Packets)
                    {
                        pkt.ScreenX += dx;
                        pkt.ScreenY += dy;
                        pkt.BoundsLeft += dx;
                        pkt.BoundsTop += dy;
                        pkt.BoundsRight += dx;
                        pkt.BoundsBottom += dy;
                        if (pkt.ClipRight > pkt.ClipLeft)
                        {
                            pkt.ClipLeft += dx;
                            pkt.ClipTop += dy;
                            pkt.ClipRight += dx;
                            pkt.ClipBottom += dy;
                        }
                        if (pkt.Kind == VectorPacketKind.Line && pkt.GlyphAdvances is { Length: >= 2 })
                        {
                            pkt.GlyphAdvances[0] += dx;
                            pkt.GlyphAdvances[1] += dy;
                        }
                    }
                    bucket.LastWindowRect = currentRect;
                }
                else if (!bucket.HasWindowRect)
                {
                    bucket.LastWindowRect = currentRect;
                    bucket.HasWindowRect = true;
                }
            }
        }

        if (toRemove is not null)
        {
            foreach (ulong h in toRemove)
            {
                _activePacketsByHwnd.Remove(h);
            }
        }
    }

    private static Color4 RgbaToColor4(uint rgba)
    {
        float r = ((rgba >> 24) & 0xFF) / 255.0f;
        float g = ((rgba >> 16) & 0xFF) / 255.0f;
        float b = ((rgba >> 8) & 0xFF) / 255.0f;
        float a = (rgba & 0xFF) / 255.0f;
        return new Color4(r, g, b, a);
    }

    private IDWriteFontFace? GetFontFace(string familyName, FontWeight weight, FontStyle style)
    {
        if (familyName.Equals("MS Shell Dlg", StringComparison.OrdinalIgnoreCase) ||
            familyName.Equals("MS Shell Dlg 2", StringComparison.OrdinalIgnoreCase))
        {
            familyName = "Segoe UI";
        }

        FontFaceKey key = new(familyName, weight, style);
        if (_fontFaceCache.TryGetValue(key, out IDWriteFontFace? cached))
        {
            return cached;
        }

        IDWriteFontFace? fontFace = null;
        if (_fontCollection.FindFamilyName(familyName, out uint familyIndex))
        {
            using IDWriteFontFamily family = _fontCollection.GetFontFamily(familyIndex);
            using IDWriteFont font = family.GetFirstMatchingFont(weight, FontStretch.Normal, style);
            fontFace = font.CreateFontFace();
        }

        _fontFaceCache[key] = fontFace;
        return fontFace;
    }

    private IDWriteTextFormat GetTextFormat(string familyName, FontWeight weight, FontStyle style, float emSize)
    {
        TextFormatKey key = new(familyName, weight, style, emSize);
        if (_textFormatCache.TryGetValue(key, out IDWriteTextFormat? cached))
        {
            return cached;
        }

        IDWriteTextFormat format = _dwriteFactory.CreateTextFormat(
            familyName,
            weight,
            style,
            FontStretch.Normal,
            emSize);
        format.WordWrapping = WordWrapping.NoWrap;

        _textFormatCache[key] = format;
        return format;
    }

    private unsafe ID2D1Bitmap? TryGetIconBitmap(nint hIcon)
    {
        if (hIcon == nint.Zero)
        {
            return null;
        }

        if (_iconBitmapCache.TryGetValue(hIcon, out ID2D1Bitmap? cached))
        {
            return cached;
        }

        ID2D1Bitmap? result = null;
        if (GetIconInfo(hIcon, out ICONINFO iconInfo))
        {
            try
            {
                nint hBmp = iconInfo.hbmColor != nint.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
                if (hBmp != nint.Zero && GetObject(hBmp, sizeof(BITMAP), out BITMAP bmp) != 0 && bmp.bmWidth > 0 && bmp.bmHeight > 0)
                {
                    int w = bmp.bmWidth;
                    int h = bmp.bmHeight;
                    byte[] pixels = new byte[w * h * 4];

                    BITMAPINFO bmi = default;
                    bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                    bmi.bmiHeader.biWidth = w;
                    bmi.bmiHeader.biHeight = -h; // top-down
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0; // BI_RGB

                    nint hdc = GetDC(nint.Zero);
                    if (hdc != nint.Zero)
                    {
                        try
                        {
                            fixed (byte* pPixels = pixels)
                            {
                                if (GetDIBits(hdc, hBmp, 0, (uint)h, (nint)pPixels, ref bmi, 0) > 0)
                                {
                                    // Premultiply alpha for Direct2D
                                    for (int i = 0; i < pixels.Length; i += 4)
                                    {
                                        byte a = pixels[i + 3];
                                        pixels[i + 0] = (byte)((pixels[i + 0] * a) / 255);
                                        pixels[i + 1] = (byte)((pixels[i + 1] * a) / 255);
                                        pixels[i + 2] = (byte)((pixels[i + 2] * a) / 255);
                                    }

                                    BitmapProperties props = new(
                                        new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                                        96.0f,
                                        96.0f);
                                    result = _d2dContext.CreateBitmap(new SizeI(w, h), (nint)pPixels, (uint)(w * 4), props);
                                }
                            }
                        }
                        finally
                        {
                            ReleaseDC(nint.Zero, hdc);
                        }
                    }
                }
            }
            finally
            {
                if (iconInfo.hbmColor != nint.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != nint.Zero) DeleteObject(iconInfo.hbmMask);
            }
        }

        if (_iconBitmapCache.Count > 256)
        {
            foreach (ID2D1Bitmap? b in _iconBitmapCache.Values) b?.Dispose();
            _iconBitmapCache.Clear();
        }
        _iconBitmapCache[hIcon] = result;
        return result;
    }

    private void UpdateTextGammaParams(float textGamma)
    {
        float clamped = Math.Clamp(textGamma, -0.5f, 1.0f);
        if (MathF.Abs(clamped - _appliedTextGamma) < 0.001f)
        {
            return;
        }

        _appliedTextGamma = clamped;
        _d2dContext.TextRenderingParams = null;
        _textRenderingParams?.Dispose();
        _textRenderingParams = null;

        if (MathF.Abs(clamped) > 0.0001f)
        {
            // 0 = default system text gamma; increasing gently tunes DirectWrite glyph edge gamma & contrast (text-only)
            float gamma = Math.Clamp(1.8f + clamped * 0.60f, 1.4f, 2.4f);
            float enhancedContrast = Math.Clamp(clamped * 0.35f, 0.0f, 0.35f);
            _textRenderingParams = _dwriteFactory.CreateCustomRenderingParams(
                gamma,
                enhancedContrast,
                0.0f,
                PixelGeometry.Flat,
                Vortice.DirectWrite.RenderingMode.Default);
            _d2dContext.TextRenderingParams = _textRenderingParams;
        }
    }

    public void Render(
        CaptureEngine capture,
        CameraController camera,
        float mouseWorldX,
        float mouseWorldY,
        bool vectorTextEnabled,
        bool showVectorCursor = true,
        float gammaBoost = 0.0f,
        float edgeSmoothing = 0.0f,
        float cursorSizeScale = 1.0f,
        bool invertCursor = false,
        int colorMode = 0,
        int mediaMode = 0,
        VectorCursorShape cursorShape = VectorCursorShape.Arrow,
        Func<(float x, float y)>? sampleFreshCursor = null)
    {
        if (_disposed)
        {
            return;
        }

        UpdateTextGammaParams(gammaBoost);

        bool viewportStationary =
            MathF.Abs(_prevCursorFocusX - camera.FocusX) < 0.005f &&
            MathF.Abs(_prevCursorFocusY - camera.FocusY) < 0.005f &&
            MathF.Abs(_prevCursorZoom - camera.Zoom) < 0.001f;
        _prevCursorFocusX = camera.FocusX;
        _prevCursorFocusY = camera.FocusY;
        _prevCursorZoom = camera.Zoom;

        if (capture.DesktopFrameVersion != _lastSeenDesktopVersion)
        {
            _lastSeenDesktopVersion = capture.DesktopFrameVersion;
            _consecutiveDesktopUpdates = Math.Min(_consecutiveDesktopUpdates + 1, 4);
        }
        else
        {
            _consecutiveDesktopUpdates = 0;
        }

        bool copiedDirect = false;
        if (vectorTextEnabled && camera.Zoom > 1.001f && capture.SharedDesktopTexture is not null && _backBufferTex is not null)
        {
            int bbW = (int)_backBufferTex.Description.Width;
            int bbH = (int)_backBufferTex.Description.Height;

            bool cacheHit =
                _hasCachedZoomFrame &&
                _cachedZoomTex is not null &&
                _cachedBackBufferW == bbW &&
                _cachedBackBufferH == bbH &&
                _cachedDesktopVersion == capture.DesktopFrameVersion &&
                _cachedDesktopW == capture.DesktopWidth &&
                _cachedDesktopH == capture.DesktopHeight &&
                MathF.Abs(_cachedFocusX - camera.FocusX) < 0.0001f &&
                MathF.Abs(_cachedFocusY - camera.FocusY) < 0.0001f &&
                MathF.Abs(_cachedZoom - camera.Zoom) < 0.0001f &&
                MathF.Abs(_cachedEdgeSmoothing - edgeSmoothing) < 0.0001f &&
                MathF.Abs(_cachedGammaBoost - gammaBoost) < 0.0001f &&
                _cachedColorMode == colorMode &&
                _cachedMediaMode == mediaMode;

            if (cacheHit)
            {
                _d3dContext.CopyResource(_backBufferTex, _cachedZoomTex!);
            }
            else if (!viewportStationary || _consecutiveDesktopUpdates >= 2)
            {
                // Actively panning/zooming: render directly to the swapchain backbuffer to avoid
                // an extra 15-30 MB VRAM copy every single frame at 120 Hz.
                _edgeShader.Render(
                    _backBufferRtv!,
                    capture.SharedDesktopTexture,
                    camera,
                    capture.DesktopWidth,
                    capture.DesktopHeight,
                    sharpness: edgeSmoothing,
                    gammaBoost: gammaBoost,
                    isHdr: capture.IsHdrDesktop,
                    sdrWhiteScale: capture.SdrWhiteScale,
                    desktopVersion: capture.DesktopFrameVersion,
                    colorMode: colorMode,
                    mediaMode: mediaMode);
                _hasCachedZoomFrame = false;
            }
            else
            {
                EnsureCachedZoomTarget(bbW, bbH);

                // Stationary viewport: render into cached zoomed texture so cursor-only frames skip the shader.
                _edgeShader.Render(
                    _cachedZoomRtv!,
                    capture.SharedDesktopTexture,
                    camera,
                    capture.DesktopWidth,
                    capture.DesktopHeight,
                    sharpness: edgeSmoothing,
                    gammaBoost: gammaBoost,
                    isHdr: capture.IsHdrDesktop,
                    sdrWhiteScale: capture.SdrWhiteScale,
                    desktopVersion: capture.DesktopFrameVersion,
                    colorMode: colorMode,
                    mediaMode: mediaMode);

                _cachedDesktopVersion = capture.DesktopFrameVersion;
                _cachedDesktopW = capture.DesktopWidth;
                _cachedDesktopH = capture.DesktopHeight;
                _cachedFocusX = camera.FocusX;
                _cachedFocusY = camera.FocusY;
                _cachedZoom = camera.Zoom;
                _cachedEdgeSmoothing = edgeSmoothing;
                _cachedGammaBoost = gammaBoost;
                _cachedColorMode = colorMode;
                _cachedMediaMode = mediaMode;
                _hasCachedZoomFrame = true;
                _d3dContext.CopyResource(_backBufferTex, _cachedZoomTex!);
            }

            copiedDirect = true;
        }
        else if (camera.Zoom <= 1.001f &&
                 capture.SharedDesktopTexture is not null && _backBufferTex is not null)
        {
            if (colorMode == 0 &&
                capture.SharedDesktopTexture.Description.Format == _swapFormat &&
                capture.SharedDesktopTexture.Description.Width == _backBufferTex.Description.Width &&
                capture.SharedDesktopTexture.Description.Height == _backBufferTex.Description.Height)
            {
                // SDR 1.0x without colour filter: bit-for-bit identical copy into the swapchain backbuffer
                _d3dContext.CopyResource(_backBufferTex, capture.SharedDesktopTexture);
            }
            else if (_backBufferRtv is not null)
            {
                int bbW = (int)_backBufferTex.Description.Width;
                int bbH = (int)_backBufferTex.Description.Height;
                bool cacheHit1x =
                    _hasCachedZoomFrame &&
                    _cachedZoomTex is not null &&
                    _cachedBackBufferW == bbW &&
                    _cachedBackBufferH == bbH &&
                    _cachedDesktopVersion == capture.DesktopFrameVersion &&
                    _cachedDesktopW == capture.DesktopWidth &&
                    _cachedDesktopH == capture.DesktopHeight &&
                    _cachedZoom <= 1.001f &&
                    _cachedColorMode == colorMode;

                if (cacheHit1x)
                {
                    _d3dContext.CopyResource(_backBufferTex, _cachedZoomTex!);
                }
                else if (_consecutiveDesktopUpdates >= 2)
                {
                    // Active desktop stream at 1.0x HDR/filtered: render directly to swapchain backbuffer.
                    _edgeShader.Render(
                        _backBufferRtv,
                        capture.SharedDesktopTexture,
                        camera,
                        capture.DesktopWidth,
                        capture.DesktopHeight,
                        sharpness: edgeSmoothing,
                        gammaBoost: gammaBoost,
                        isHdr: capture.IsHdrDesktop,
                        sdrWhiteScale: capture.SdrWhiteScale,
                        desktopVersion: capture.DesktopFrameVersion,
                        colorMode: colorMode,
                        mediaMode: mediaMode);
                    _hasCachedZoomFrame = false;
                }
                else
                {
                    // Static desktop at 1.0x HDR/filtered: cache converted SDR frame so cursor moves are a fast VRAM copy.
                    EnsureCachedZoomTarget(bbW, bbH);
                    _edgeShader.Render(
                        _cachedZoomRtv!,
                        capture.SharedDesktopTexture,
                        camera,
                        capture.DesktopWidth,
                        capture.DesktopHeight,
                        sharpness: edgeSmoothing,
                        gammaBoost: gammaBoost,
                        isHdr: capture.IsHdrDesktop,
                        sdrWhiteScale: capture.SdrWhiteScale,
                        desktopVersion: capture.DesktopFrameVersion,
                        colorMode: colorMode,
                        mediaMode: mediaMode);

                    _cachedDesktopVersion = capture.DesktopFrameVersion;
                    _cachedDesktopW = capture.DesktopWidth;
                    _cachedDesktopH = capture.DesktopHeight;
                    _cachedFocusX = 0.0f;
                    _cachedFocusY = 0.0f;
                    _cachedZoom = 1.0f;
                    _cachedEdgeSmoothing = edgeSmoothing;
                    _cachedGammaBoost = gammaBoost;
                    _cachedColorMode = colorMode;
                    _cachedMediaMode = mediaMode;
                    _hasCachedZoomFrame = true;
                    _d3dContext.CopyResource(_backBufferTex, _cachedZoomTex!);
                }
            }
            copiedDirect = true;
        }

        ActiveVectorTextCount = 0;

        // Pure D3D11 fast path: draw the magnified cursor directly via SDF shader on _backBufferRtv,
        // completely eliminating per-frame D3D11<->D2D1 BeginDraw/EndDraw interop flushes.
        if (copiedDirect)
        {
            if (showVectorCursor && _backBufferRtv is not null)
            {
                if (viewportStationary && sampleFreshCursor is not null)
                {
                    (mouseWorldX, mouseWorldY) = sampleFreshCursor();
                }

                Vector2 screenPos = Vector2.Transform(
                    new Vector2(mouseWorldX, mouseWorldY),
                    camera.WorldToScreenMatrix);
                screenPos.X = Math.Clamp(screenPos.X, 1.0f, MathF.Max(1.0f, camera.ScreenWidth - 2.0f));
                screenPos.Y = Math.Clamp(screenPos.Y, 1.0f, MathF.Max(1.0f, camera.ScreenHeight - 2.0f));

                float baseCursorScale = camera.Zoom > 1.001f
                    ? Math.Clamp(3.15f + (camera.Zoom - 1.0f) * 0.18f, 3.15f, 5.2f)
                    : 1.5f;
                float cursorScale = baseCursorScale * Math.Clamp(cursorSizeScale, 0.5f, 3.0f);

                _edgeShader.RenderCursor(
                    _backBufferRtv,
                    camera.ScreenWidth,
                    camera.ScreenHeight,
                    screenPos.X,
                    screenPos.Y,
                    cursorScale,
                    invertCursor,
                    _isHdrDesktop,
                    _sdrWhiteScale,
                    cursorShape);
            }
        }
        else
        {
            _d2dContext.BeginDraw();
            _d2dContext.Transform = Matrix3x2.Identity;

            EnsureDesktopBitmap(capture.SharedDesktopTexture);
            _d2dContext.Clear(new Color4(0.0f, 0.0f, 0.0f, 1.0f));

            if (_desktopBitmap is not null)
            {
                _d2dContext.Transform = camera.Zoom <= 1.001f ? Matrix3x2.Identity : camera.WorldToScreenMatrix;
                Rect destRect = new(0.0f, 0.0f, capture.DesktopWidth, capture.DesktopHeight);
                _d2dContext.DrawBitmap(
                    _desktopBitmap,
                    destRect,
                    1.0f,
                    BitmapInterpolationMode.Linear,
                    null);
                _d2dContext.Transform = Matrix3x2.Identity;
            }

            if (showVectorCursor)
            {
                if (viewportStationary && sampleFreshCursor is not null)
                {
                    (mouseWorldX, mouseWorldY) = sampleFreshCursor();
                }

                Vector2 screenPos = Vector2.Transform(
                    new Vector2(mouseWorldX, mouseWorldY),
                    camera.WorldToScreenMatrix);
                screenPos.X = Math.Clamp(screenPos.X, 1.0f, MathF.Max(1.0f, camera.ScreenWidth - 2.0f));
                screenPos.Y = Math.Clamp(screenPos.Y, 1.0f, MathF.Max(1.0f, camera.ScreenHeight - 2.0f));

                float baseCursorScale = camera.Zoom > 1.001f
                    ? Math.Clamp(3.15f + (camera.Zoom - 1.0f) * 0.18f, 3.15f, 5.2f)
                    : 1.5f;
                float cursorScale = baseCursorScale * Math.Clamp(cursorSizeScale, 0.5f, 3.0f);

                _d2dContext.Transform =
                    Matrix3x2.CreateScale(cursorScale) *
                    Matrix3x2.CreateTranslation(screenPos.X, screenPos.Y);

                ID2D1SolidColorBrush fillBrush = invertCursor ? _invertedCursorFillBrush : _cursorFillBrush;
                ID2D1SolidColorBrush strokeBrush = invertCursor ? _invertedCursorStrokeBrush : _cursorStrokeBrush;
                _d2dContext.FillGeometry(_cursorGeometry, fillBrush);
                _d2dContext.DrawGeometry(_cursorGeometry, strokeBrush, 1.25f);
            }

            _d2dContext.Transform = Matrix3x2.Identity;
            _d2dContext.EndDraw();
        }

        _swapChain.Present(0, PresentFlags.None);
    }

    private void RenderVectorTextPass(CameraController camera)
    {
        if (_activePacketsByHwnd.Count == 0)
        {
            ActiveVectorTextCount = 0;
            return;
        }

        int renderedCount = 0;

        float halfViewW = (camera.ScreenWidth / 2.0f) / camera.Zoom;
        float halfViewH = (camera.ScreenHeight / 2.0f) / camera.Zoom;
        float viewLeft = camera.FocusX - halfViewW;
        float viewTop = camera.FocusY - halfViewH;
        float viewRight = camera.FocusX + halfViewW;
        float viewBottom = camera.FocusY + halfViewH;

        Matrix3x2 worldToScreen =
            Matrix3x2.CreateTranslation(-camera.FocusX, -camera.FocusY) *
            Matrix3x2.CreateScale(camera.Zoom) *
            Matrix3x2.CreateTranslation(camera.ScreenWidth / 2.0f, camera.ScreenHeight / 2.0f);

        _d2dContext.Transform = camera.Zoom <= 1.001f ? Matrix3x2.Identity : worldToScreen;

        foreach (HwndFrameBucket bucket in _activePacketsByHwnd.Values)
        {
            foreach (ManagedVectorPacket pkt in bucket.Packets)
            {
                if (pkt.BoundsRight < viewLeft || pkt.BoundsLeft > viewRight ||
                    pkt.BoundsBottom < viewTop || pkt.BoundsTop > viewBottom)
                {
                    continue;
                }

                bool hasClip = pkt.ClipRight > pkt.ClipLeft + 1.0f && pkt.ClipBottom > pkt.ClipTop + 1.0f;
                if (hasClip)
                {
                    _d2dContext.PushAxisAlignedClip(
                        new Rect(pkt.ClipLeft, pkt.ClipTop, pkt.ClipRight - pkt.ClipLeft, pkt.ClipBottom - pkt.ClipTop),
                        AntialiasMode.PerPrimitive);
                }

                try
                {
                    if (pkt.Kind == VectorPacketKind.Icon)
                    {
                        ID2D1Bitmap? iconBmp = TryGetIconBitmap(pkt.IconHandle);
                        if (iconBmp is not null)
                        {
                            Rect dest = new(
                                pkt.BoundsLeft,
                                pkt.BoundsTop,
                                MathF.Max(1.0f, pkt.BoundsRight - pkt.BoundsLeft),
                                MathF.Max(1.0f, pkt.BoundsBottom - pkt.BoundsTop));
                            _d2dContext.DrawBitmap(iconBmp, dest, 1.0f, BitmapInterpolationMode.Linear, null);
                            renderedCount++;
                        }
                        continue;
                    }

                    if (pkt.Kind == VectorPacketKind.Line && pkt.GlyphAdvances is { Length: >= 2 })
                    {
                        _vectorBrush.Color = ToRenderColor(pkt.TextColor);
                        _d2dContext.DrawLine(
                            new Vector2(pkt.ScreenX, pkt.ScreenY),
                            new Vector2(pkt.GlyphAdvances[0], pkt.GlyphAdvances[1]),
                            _vectorBrush,
                            MathF.Max(1.0f / camera.Zoom, pkt.FontEmSize));
                        renderedCount++;
                        continue;
                    }

                    if (pkt.Kind == VectorPacketKind.RectOutline)
                    {
                        _vectorBrush.Color = ToRenderColor(pkt.TextColor);
                        _d2dContext.DrawRectangle(
                            new Rect(
                                pkt.BoundsLeft,
                                pkt.BoundsTop,
                                MathF.Max(1.0f, pkt.BoundsRight - pkt.BoundsLeft),
                                MathF.Max(1.0f, pkt.BoundsBottom - pkt.BoundsTop)),
                            _vectorBrush,
                            MathF.Max(1.0f / camera.Zoom, pkt.FontEmSize));
                        renderedCount++;
                        continue;
                    }

                    _vectorBrush.Color = ToRenderColor(pkt.TextColor);

                    if (pkt.GlyphIndices is { Length: > 0 } && pkt.GlyphAdvances is not null)
                    {
                        IDWriteFontFace? fontFace = GetFontFace(pkt.FontFamily, pkt.FontWeight, pkt.FontStyle);
                        if (fontFace is not null)
                        {
                            using GlyphRun glyphRun = new()
                            {
                                FontFace = fontFace,
                                FontEmSize = pkt.FontEmSize,
                                Indices = pkt.GlyphIndices,
                                Advances = pkt.GlyphAdvances,
                                IsSideways = false,
                                BidiLevel = 0
                            };

                            _d2dContext.DrawGlyphRun(
                                new Vector2(pkt.ScreenX, pkt.ScreenY),
                                glyphRun,
                                _vectorBrush,
                                MeasuringMode.Natural);
                            renderedCount++;
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(pkt.Text))
                    {
                        IDWriteTextFormat format = GetTextFormat(pkt.FontFamily, pkt.FontWeight, pkt.FontStyle, pkt.FontEmSize);
                        _d2dContext.DrawText(
                            pkt.Text,
                            format,
                            new Rect(
                                pkt.ScreenX,
                                pkt.BoundsTop,
                                MathF.Max(1.0f, pkt.BoundsRight - pkt.ScreenX),
                                MathF.Max(1.0f, pkt.BoundsBottom - pkt.BoundsTop)),
                            _vectorBrush);
                        renderedCount++;
                    }
                }
                finally
                {
                    if (hasClip)
                    {
                        _d2dContext.PopAxisAlignedClip();
                    }
                }
            }
        }

        ActiveVectorTextCount = renderedCount;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (IDWriteFontFace? ff in _fontFaceCache.Values)
        {
            ff?.Dispose();
        }
        _fontFaceCache.Clear();

        foreach (IDWriteTextFormat fmt in _textFormatCache.Values)
        {
            fmt.Dispose();
        }
        _textFormatCache.Clear();

        foreach (ID2D1Bitmap? bmp in _iconBitmapCache.Values)
        {
            bmp?.Dispose();
        }
        _iconBitmapCache.Clear();

        _d2dContext.Target = null;
        _d2dContext.TextRenderingParams = null;
        _textRenderingParams?.Dispose();
        _textRenderingParams = null;
        _desktopBitmap?.Dispose();
        _targetBitmap?.Dispose();
        _cachedZoomRtv?.Dispose();
        _cachedZoomTex?.Dispose();
        _backBufferRtv?.Dispose();
        _backBufferTex?.Dispose();
        _edgeShader.Dispose();
        _cursorGeometry.Dispose();
        _invertedCursorStrokeBrush.Dispose();
        _invertedCursorFillBrush.Dispose();
        _cursorStrokeBrush.Dispose();
        _cursorFillBrush.Dispose();
        _vectorBrush.Dispose();
        _fontCollection.Dispose();
        _dwriteFactory.Dispose();
        _d2dContext.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _swapChain.Dispose();
        if (_frameLatencyWaitable != nint.Zero)
        {
            CloseHandle(_frameLatencyWaitable);
            _frameLatencyWaitable = nint.Zero;
        }
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
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint hObject, int nCount, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbmp, uint uStartScan, uint cScanLines, nint lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObjectEx(nint hHandle, uint dwMilliseconds, bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
