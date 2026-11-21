using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ZoomHost;

/// <summary>
/// Captures the desktop background via the DXGI Desktop Duplication API (IDXGIOutputDuplication)
/// into a persistent GPU texture with zero CPU readback.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly int _outputIndex;
    private readonly object _cpuLock = new();
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private byte[]? _cpuPixels;
    private int _cpuWidth;
    private int _cpuHeight;
    private bool _stagingDirty;
    private long _lastStagingCopyTicks;
    private long _lastRecoveryAttemptTicks;
    private bool _disposed;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIDevice DxgiDevice { get; }
    public IDXGIAdapter DxgiAdapter { get; }

    public ID3D11Texture2D? SharedDesktopTexture { get; private set; }
    public bool HasAcquiredFrame { get; private set; }
    public long DesktopFrameVersion { get; private set; }
    public Format DesktopFormat { get; private set; } = Format.B8G8R8A8_UNorm;
    public bool IsHdrDesktop { get; private set; }
    public float SdrWhiteScale { get; private set; } = 1.0f;

    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public int DesktopWidth { get; private set; }
    public int DesktopHeight { get; private set; }

    public bool IsHardwarePointerVisible { get; private set; }
    public int PointerWorldX { get; private set; }
    public int PointerWorldY { get; private set; }

    public CaptureEngine(int outputIndex = 0)
    {
        _outputIndex = outputIndex;

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        Result hr = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device? device);

        if (hr.Failure || device is null)
        {
            throw new InvalidOperationException($"Failed to create D3D11 device: {hr}");
        }

        Device = device;
        Context = Device.ImmediateContext;
        DxgiDevice = Device.QueryInterface<IDXGIDevice>();
        DxgiAdapter = DxgiDevice.GetAdapter();

        InitializeDuplication();
    }

    private void InitializeDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;

        Result enumResult = DxgiAdapter.EnumOutputs((uint)_outputIndex, out IDXGIOutput? output);
        if (enumResult.Failure || output is null)
        {
            throw new InvalidOperationException($"Failed to enumerate DXGI output {_outputIndex}: {enumResult}");
        }

        using (output)
        {
            OutputDescription desc = output.Description;
            RawRect bounds = desc.DesktopCoordinates;
            OriginX = bounds.Left;
            OriginY = bounds.Top;
            DesktopWidth = Math.Max(1, bounds.Right - bounds.Left);
            DesktopHeight = Math.Max(1, bounds.Bottom - bounds.Top);

            using IDXGIOutput6? output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
            IsHdrDesktop = output6 is not null &&
                           output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
            SdrWhiteScale = IsHdrDesktop ? QuerySdrWhiteScale(desc.DeviceName) : 1.0f;

            Format initialFormat = IsHdrDesktop ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
            EnsureSharedTexture(DesktopWidth, DesktopHeight, initialFormat);

            try
            {
                using IDXGIOutput5? output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
                if (output5 is not null)
                {
                    Format[] supportedFormats = IsHdrDesktop
                        ? [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm]
                        : [Format.B8G8R8A8_UNorm];
                    _duplication = output5.DuplicateOutput1(Device, supportedFormats);
                }
                else
                {
                    using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    _duplication = output1.DuplicateOutput(Device);
                }
            }
            catch
            {
                // Fallback to DuplicateOutput or retry later if Windows temporarily denies duplication
                try
                {
                    using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    _duplication = output1.DuplicateOutput(Device);
                }
                catch
                {
                    _duplication = null;
                }
            }
        }
    }

    private void EnsureSharedTexture(int width, int height, Format format)
    {
        if (SharedDesktopTexture is not null &&
            SharedDesktopTexture.Description.Width == width &&
            SharedDesktopTexture.Description.Height == height &&
            SharedDesktopTexture.Description.Format == format)
        {
            return;
        }

        DesktopFormat = format;
        IsHdrDesktop = format == Format.R16G16B16A16_Float;

        SharedDesktopTexture?.Dispose();

        Texture2DDescription texDesc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        SharedDesktopTexture = Device.CreateTexture2D(texDesc);

        _stagingTexture?.Dispose();
        _stagingTexture = null;
        if (format == Format.B8G8R8A8_UNorm)
        {
            Texture2DDescription stagingDesc = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            _stagingTexture = Device.CreateTexture2D(stagingDesc);
        }
        _stagingDirty = true;
    }

    /// <summary>
    /// Attempts to acquire the latest desktop frame and copy it to <see cref="SharedDesktopTexture"/>.
    /// Returns true if a new desktop image frame was copied, false on timeout, pointer-only update, or recovery.
    /// </summary>
    public bool TryAcquireFrame(uint timeoutMs = 0) => TryAcquireFrame(timeoutMs, out _);

    /// <summary>
    /// Attempts to acquire the latest desktop frame (blocking up to <paramref name="timeoutMs"/> for either
    /// a desktop image update or a hardware pointer update). Copies to <see cref="SharedDesktopTexture"/>
    /// only when desktop pixels actually changed (<c>AccumulatedFrames &gt; 0</c> or first frame).
    /// </summary>
    public bool TryAcquireFrame(uint timeoutMs, out bool mouseUpdated)
    {
        mouseUpdated = false;
        if (_disposed)
        {
            return false;
        }

        if (_duplication is null)
        {
            long nowTicks = Environment.TickCount64;
            if (nowTicks - _lastRecoveryAttemptTicks < 200)
            {
                return false;
            }
            _lastRecoveryAttemptTicks = nowTicks;

            try
            {
                InitializeDuplication();
            }
            catch
            {
                return false;
            }
        }

        if (_duplication is null)
        {
            return false;
        }

        Result result = _duplication.AcquireNextFrame(timeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource? desktopResource);

        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return false;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost ||
            result == Vortice.DXGI.ResultCode.InvalidCall ||
            result.Failure)
        {
            desktopResource?.Dispose();
            RecoverDuplication();
            return false;
        }

        try
        {
            if (frameInfo.LastMouseUpdateTime > 0)
            {
                IsHardwarePointerVisible = frameInfo.PointerPosition.Visible;
                PointerWorldX = frameInfo.PointerPosition.Position.X;
                PointerWorldY = frameInfo.PointerPosition.Position.Y;
                mouseUpdated = true;
            }

            if (desktopResource is not null &&
                frameInfo.LastPresentTime > 0 &&
                (frameInfo.AccumulatedFrames > 0 || !HasAcquiredFrame))
            {
                using ID3D11Texture2D acquiredTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                Texture2DDescription desc = acquiredTexture.Description;
                EnsureSharedTexture((int)desc.Width, (int)desc.Height, desc.Format);

                if (SharedDesktopTexture is not null)
                {
                    Context.CopyResource(SharedDesktopTexture, acquiredTexture);
                    HasAcquiredFrame = true;
                    DesktopFrameVersion++;
                    _stagingDirty = true;
                }
                return true;
            }

            return false;
        }
        finally
        {
            desktopResource?.Dispose();
            try
            {
                _duplication?.ReleaseFrame();
            }
            catch
            {
                RecoverDuplication();
            }
        }
    }

    /// <summary>
    /// Must be called on the render thread. Copies the latest GPU desktop frame to a CPU-readable
    /// staging buffer at most ~30 Hz when <paramref name="active"/> is true.
    /// </summary>
    public unsafe void UpdateCpuSnapshotIfNeeded(bool active)
    {
        if (!active || !_stagingDirty || SharedDesktopTexture is null || _stagingTexture is null)
        {
            return;
        }

        long nowTicks = Environment.TickCount64;
        if (nowTicks - _lastStagingCopyTicks < 33)
        {
            return;
        }

        _lastStagingCopyTicks = nowTicks;
        _stagingDirty = false;

        Context.CopyResource(_stagingTexture, SharedDesktopTexture);
        MappedSubresource mapped = Context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int w = DesktopWidth;
            int h = DesktopHeight;
            int rowBytes = w * 4;
            byte[] buffer = new byte[rowBytes * h];

            byte* srcBase = (byte*)mapped.DataPointer;
            fixed (byte* dstBase = buffer)
            {
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (long)y * mapped.RowPitch,
                        dstBase + (long)y * rowBytes,
                        rowBytes,
                        rowBytes);
                }
            }

            lock (_cpuLock)
            {
                _cpuPixels = buffer;
                _cpuWidth = w;
                _cpuHeight = h;
            }
        }
        finally
        {
            Context.Unmap(_stagingTexture, 0);
        }
    }

    /// <summary>
    /// Thread-safe O(1) pixel color sampler returning 0xRRGGBBFF from the latest CPU snapshot.
    /// </summary>
    public uint SamplePixelRgba(int localX, int localY)
    {
        lock (_cpuLock)
        {
            if (_cpuPixels is null || _cpuWidth <= 0 || _cpuHeight <= 0)
            {
                return 0xFFFFFFFFu;
            }

            int x = Math.Clamp(localX, 0, _cpuWidth - 1);
            int y = Math.Clamp(localY, 0, _cpuHeight - 1);
            int idx = (y * _cpuWidth + x) * 4;

            byte b = _cpuPixels[idx];
            byte g = _cpuPixels[idx + 1];
            byte r = _cpuPixels[idx + 2];

            return ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | 0xFFu;
        }
    }

    private void RecoverDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
        try
        {
            InitializeDuplication();
        }
        catch
        {
            // Will retry on next frame (e.g. while UAC / lock screen is active)
        }
    }

    private static unsafe float QuerySdrWhiteScale(string gdiDeviceName)
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 ||
            pathCount == 0)
        {
            return 2.5f;
        }

        DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, nint.Zero) != 0)
        {
            return 2.5f;
        }

        for (uint i = 0; i < pathCount; i++)
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME sourceName = new()
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id
                }
            };

            if (DisplayConfigGetDeviceInfo(ref sourceName) != 0)
            {
                continue;
            }

            if (!string.Equals(sourceName.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DISPLAYCONFIG_SDR_WHITE_LEVEL sdrInfo = new()
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id
                }
            };

            if (DisplayConfigGetDeviceInfo(ref sdrInfo) == 0 && sdrInfo.SDRWhiteLevel >= 500)
            {
                return sdrInfo.SDRWhiteLevel / 1000.0f;
            }
        }

        return 2.5f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        private fixed byte _payload[48];
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        SharedDesktopTexture?.Dispose();
        SharedDesktopTexture = null;
        DxgiAdapter.Dispose();
        DxgiDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
