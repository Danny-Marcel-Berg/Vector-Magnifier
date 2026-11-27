using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ZoomHost;

/// <summary>
/// GPU text magnifier (see Shaders/EdgeSmoothing.hlsl for the algorithm).
/// <list type="number">
/// <item>Pole pass (desktop resolution, visible crop only, re-run when the desktop or the
/// viewport changes): ink/background colours, local luminance extremes and text confidence.</item>
/// <item>Magnify pass (screen resolution, every frame): crisp, anti-aliased contour reconstruction.</item>
/// </list>
/// </summary>
public sealed class EdgeSmoothingShader : IDisposable
{
    private const string HlslResourceName = "ZoomHost.Shaders.EdgeSmoothing.hlsl";
    private const string HlslSourceName = "EdgeSmoothing.hlsl";

    /// <summary>At or below this zoom the shader samples bilinearly and needs no pole pass.</summary>
    private const float PassthroughZoom = 1.05f;

    /// <summary>Extra texels around the visible area: bilinear pole footprint (1) plus safety.</summary>
    private const int ViewMargin = 4;

    // Must match the cbuffer "Params" layout in EdgeSmoothing.hlsl (16-byte aligned, 80 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public float FocusX;
        public float FocusY;
        public float Zoom;
        public float Sharpness;
        public float ScreenWidth;
        public float ScreenHeight;
        public float DesktopWidth;
        public float DesktopHeight;
        public float TextGamma;
        public float IsHdr;
        public float SdrWhiteScale;
        public float ColorMode;
        public float CropMinU;
        public float CropMinV;
        public float CropMaxU;
        public float CropMaxV;
        public float MediaMode;
        public float Pad0;
        public float Pad1;
        public float Pad2;
    }

    // Must match the cbuffer "CursorParams" layout in EdgeSmoothing.hlsl (16-byte aligned, 64 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorShaderParams
    {
        public float CursorX;
        public float CursorY;
        public float CursorScale;
        public float StrokeHalfWidth;
        public float ScreenWidth;
        public float ScreenHeight;
        public float CursorShape;
        private float _pad1;
        public float FillR, FillG, FillB, FillA;
        public float StrokeR, StrokeG, StrokeB, StrokeA;
    }

    private readonly record struct ShaderBytecode(byte[] Vs, byte[] VsCrop, byte[] PsPrepColor, byte[] PsPrepMeta, byte[] PsPole, byte[] PsMediaMask, byte[] Ps, byte[] VsCursor, byte[] PsCursor);

    private readonly record struct RenderTarget(ID3D11Texture2D Texture, ID3D11RenderTargetView Rtv, ID3D11ShaderResourceView Srv) : IDisposable
    {
        public void Dispose()
        {
            Srv.Dispose();
            Rtv.Dispose();
            Texture.Dispose();
        }
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11VertexShader _vsCrop;
    private readonly ID3D11PixelShader _psPrepColor;
    private readonly ID3D11PixelShader _psPrepMeta;
    private readonly ID3D11PixelShader _psPole;
    private readonly ID3D11PixelShader _psMediaMask;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11VertexShader _vsCursor;
    private readonly ID3D11PixelShader _psCursor;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _cursorConstantBuffer;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11BlendState _opaqueBlendState;
    private readonly ID3D11BlendState _alphaBlendState;
    private readonly ID3D11RasterizerState _noCullRasterizerState;

    private ID3D11Texture2D? _trackedTexture;
    private ID3D11ShaderResourceView? _desktopSrv;

    // Pre-pass, pole pass, and half-res media mask outputs (t1..t6 in MediaMaskPS / PSMain).
    private RenderTarget? _percepTex;
    private RenderTarget? _metaTex;
    private RenderTarget? _poleDark;
    private RenderTarget? _poleLight;
    private RenderTarget? _localExt;
    private RenderTarget? _mediaMask;
    private int _poleWidth;
    private int _poleHeight;
    private Format _poleFormat;

    private bool _hasPoles;
    private bool _hasMediaMask;
    private long _polesDesktopVersion = -1;
    private int _polesX0, _polesY0, _polesX1, _polesY1;
    private bool _disposed;

    public EdgeSmoothingShader(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        ShaderBytecode bytecode = LoadOrCompileShaders();
        _vs = _device.CreateVertexShader(bytecode.Vs);
        _vsCrop = _device.CreateVertexShader(bytecode.VsCrop);
        _psPrepColor = _device.CreatePixelShader(bytecode.PsPrepColor);
        _psPrepMeta = _device.CreatePixelShader(bytecode.PsPrepMeta);
        _psPole = _device.CreatePixelShader(bytecode.PsPole);
        _psMediaMask = _device.CreatePixelShader(bytecode.PsMediaMask);
        _ps = _device.CreatePixelShader(bytecode.Ps);
        _vsCursor = _device.CreateVertexShader(bytecode.VsCursor);
        _psCursor = _device.CreatePixelShader(bytecode.PsCursor);

        _constantBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<ShaderParams>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        _cursorConstantBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<CursorShaderParams>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        _linearSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue
        });

        _opaqueBlendState = _device.CreateBlendState(BlendDescription.Opaque);
        _alphaBlendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _noCullRasterizerState = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    public void Render(
        ID3D11RenderTargetView rtv,
        ID3D11Texture2D desktopTexture,
        CameraController camera,
        int desktopWidth,
        int desktopHeight,
        float sharpness = 0.0f,
        float gammaBoost = 0.0f,
        bool isHdr = false,
        float sdrWhiteScale = 1.0f,
        long desktopVersion = -1,
        int colorMode = 0,
        int mediaMode = 0)
    {
        if (_disposed)
        {
            return; // Render loop may still tick once during shutdown.
        }

        if (!ReferenceEquals(_trackedTexture, desktopTexture) || _desktopSrv is null)
        {
            _desktopSrv?.Dispose();
            _trackedTexture = desktopTexture;
            _desktopSrv = _device.CreateShaderResourceView(desktopTexture);
            _hasPoles = false;
        }

        _context.IASetInputLayout(null);
        _context.RSSetState(_noCullRasterizerState);
        _context.OMSetBlendState(_opaqueBlendState);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(1, _linearSampler);

        if (camera.Zoom > PassthroughZoom)
        {
            EnsurePoleTargets(desktopWidth, desktopHeight, isHdr);
            UpdatePolesIfNeeded(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, desktopVersion, mediaMode);
        }

        // Magnify pass
        WriteConstants(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, colorMode, mediaMode,
            _polesX0, _polesY0, _polesX1, _polesY1);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.RSSetViewport(0, 0, camera.ScreenWidth, camera.ScreenHeight);
        _context.OMSetRenderTargets(rtv);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetShaderResource(0, _desktopSrv);
        if (camera.Zoom > PassthroughZoom)
        {
            _context.PSSetShaderResource(1, _poleDark!.Value.Srv);
            _context.PSSetShaderResource(2, _poleLight!.Value.Srv);
            _context.PSSetShaderResource(3, _localExt!.Value.Srv);
            _context.PSSetShaderResource(4, _percepTex!.Value.Srv);
            _context.PSSetShaderResource(6, _mediaMask!.Value.Srv);
        }
        _context.Draw(3, 0);

        UnbindShaderResources();
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    public unsafe void RenderCursor(
        ID3D11RenderTargetView rtv,
        float screenWidth,
        float screenHeight,
        float cursorScreenX,
        float cursorScreenY,
        float cursorScale,
        bool invertCursor,
        bool isHdr,
        float sdrWhiteScale,
        VectorCursorShape cursorShape = VectorCursorShape.Arrow)
    {
        if (_disposed)
        {
            return;
        }

        float fillSrgb = invertCursor ? 0.03f : 1.0f;
        float strokeSrgb = invertCursor ? 1.0f : 0.05f;
        float fill = isHdr ? SrgbToLinear(fillSrgb) * sdrWhiteScale : fillSrgb;
        float stroke = isHdr ? SrgbToLinear(strokeSrgb) * sdrWhiteScale : strokeSrgb;

        MappedSubresource mapped = _context.Map(_cursorConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            CursorShaderParams* p = (CursorShaderParams*)mapped.DataPointer;
            p->CursorX = cursorScreenX;
            p->CursorY = cursorScreenY;
            p->CursorScale = cursorScale;
            p->StrokeHalfWidth = 0.625f;
            p->ScreenWidth = screenWidth;
            p->ScreenHeight = screenHeight;
            p->CursorShape = (float)(int)cursorShape;
            p->FillR = fill; p->FillG = fill; p->FillB = fill; p->FillA = 1.0f;
            p->StrokeR = stroke; p->StrokeG = stroke; p->StrokeB = stroke; p->StrokeA = 1.0f;
        }
        finally
        {
            _context.Unmap(_cursorConstantBuffer, 0);
        }

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.RSSetState(_noCullRasterizerState);
        _context.RSSetViewport(0, 0, screenWidth, screenHeight);
        _context.OMSetBlendState(_alphaBlendState);
        _context.OMSetRenderTargets(rtv);
        _context.VSSetConstantBuffer(1, _cursorConstantBuffer);
        _context.PSSetConstantBuffer(1, _cursorConstantBuffer);
        _context.VSSetShader(_vsCursor);
        _context.PSSetShader(_psCursor);
        _context.Draw(4, 0);
        _context.OMSetBlendState(_opaqueBlendState);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private static float SrgbToLinear(float c)
    {
        float clamped = Math.Clamp(c, 0.0f, 1.0f);
        return clamped <= 0.04045f ? clamped / 12.92f : MathF.Pow((clamped + 0.055f) / 1.055f, 2.4f);
    }

    private void UpdatePolesIfNeeded(
        CameraController camera, int desktopWidth, int desktopHeight,
        float sharpness, float gammaBoost, bool isHdr, float sdrWhiteScale, long desktopVersion, int mediaMode)
    {
        // Visible desktop rectangle (texels) plus the footprint the magnify pass reads.
        float halfW = camera.ScreenWidth * 0.5f / camera.Zoom;
        float halfH = camera.ScreenHeight * 0.5f / camera.Zoom;
        int reqX0 = Math.Clamp((int)MathF.Floor(camera.FocusX - halfW) - ViewMargin, 0, desktopWidth);
        int reqY0 = Math.Clamp((int)MathF.Floor(camera.FocusY - halfH) - ViewMargin, 0, desktopHeight);
        int reqX1 = Math.Clamp((int)MathF.Ceiling(camera.FocusX + halfW) + ViewMargin, 0, desktopWidth);
        int reqY1 = Math.Clamp((int)MathF.Ceiling(camera.FocusY + halfH) + ViewMargin, 0, desktopHeight);

        bool upToDate = _hasPoles &&
            desktopVersion >= 0 &&
            desktopVersion == _polesDesktopVersion &&
            reqX0 >= _polesX0 && reqY0 >= _polesY0 && reqX1 <= _polesX1 && reqY1 <= _polesY1;
        if (upToDate)
        {
            if (mediaMode == 0 && !_hasMediaMask)
            {
                UnbindShaderResources();
                RenderMediaMaskPass(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, _polesX0, _polesY0, _polesX1, _polesY1);
            }
            return;
        }

        // Dynamic panSlack: during live video/typing (desktopVersion changed) keep slack compact so
        // PolePS runs on 3x fewer pixels; when panning on a static desktop keep generous slack.
        bool desktopChanged = !_hasPoles || desktopVersion != _polesDesktopVersion;
        int panSlack = desktopChanged
            ? (mediaMode == 0 ? 16 : 8)
            : (camera.Zoom >= 8.0f ? 144 : camera.Zoom >= 4.0f ? 96 : camera.Zoom >= 2.0f ? 80 : 48);
        int x0 = Math.Clamp(reqX0 - panSlack, 0, desktopWidth);
        int y0 = Math.Clamp(reqY0 - panSlack, 0, desktopHeight);
        int x1 = Math.Clamp(reqX1 + panSlack, 0, desktopWidth);
        int y1 = Math.Clamp(reqY1 + panSlack, 0, desktopHeight);

        int pcX0 = Math.Clamp(x0 - 24, 0, desktopWidth);
        int pcY0 = Math.Clamp(y0 - 24, 0, desktopHeight);
        int pcX1 = Math.Clamp(x1 + 24, 0, desktopWidth);
        int pcY1 = Math.Clamp(y1 + 24, 0, desktopHeight);

        int pmX0 = Math.Clamp(x0 - 21, 0, desktopWidth);
        int pmY0 = Math.Clamp(y0 - 21, 0, desktopHeight);
        int pmX1 = Math.Clamp(x1 + 21, 0, desktopWidth);
        int pmY1 = Math.Clamp(y1 + 21, 0, desktopHeight);

        UnbindShaderResources();
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.RSSetViewport(0, 0, desktopWidth, desktopHeight);
        _context.VSSetShader(_vsCrop);

        // Pass 0a: Convert raw desktop (HDR/SDR) -> perceptual sRGB + luma once per pixel.
        WriteConstants(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, 0, 0, pcX0, pcY0, pcX1, pcY1);
        _context.OMSetRenderTargets(_percepTex!.Value.Rtv);
        _context.PSSetShader(_psPrepColor);
        _context.PSSetShaderResource(0, _desktopSrv!);
        _context.Draw(4, 0);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);

        // Pass 0b: Precompute 4-neighbour RGB flatness + ClearType fringe flags once per pixel.
        WriteConstants(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, 0, 0, pmX0, pmY0, pmX1, pmY1);
        _context.OMSetRenderTargets(_metaTex!.Value.Rtv);
        _context.PSSetShader(_psPrepMeta);
        _context.PSSetShaderResource(4, _percepTex!.Value.Srv);
        _context.Draw(4, 0);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);

        // Pass 1: Zero-spill PolePS reading strictly from PercepTex (t4) + MetaTex (t5).
        WriteConstants(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, 0, 0, x0, y0, x1, y1);
        _context.OMSetRenderTargets([_poleDark!.Value.Rtv, _poleLight!.Value.Rtv, _localExt!.Value.Rtv]);
        _context.PSSetShader(_psPole);
        _context.PSSetShaderResource(4, _percepTex!.Value.Srv);
        _context.PSSetShaderResource(5, _metaTex!.Value.Srv);
        _context.Draw(4, 0);
        _context.OMSetRenderTargets([(ID3D11RenderTargetView)null!, null!, null!]);

        if (mediaMode == 0)
        {
            RenderMediaMaskPass(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, x0, y0, x1, y1);
        }
        else
        {
            _hasMediaMask = false;
        }

        (_polesX0, _polesY0, _polesX1, _polesY1) = (x0, y0, x1, y1);
        _polesDesktopVersion = desktopVersion;
        _hasPoles = true;
    }

    private void RenderMediaMaskPass(
        CameraController camera, int desktopWidth, int desktopHeight,
        float sharpness, float gammaBoost, bool isHdr, float sdrWhiteScale,
        int x0, int y0, int x1, int y1)
    {
        int mediaW = (int)_mediaMask!.Value.Texture.Description.Width;
        int mediaH = (int)_mediaMask!.Value.Texture.Description.Height;
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.RSSetViewport(0, 0, mediaW, mediaH);
        _context.VSSetShader(_vsCrop);
        WriteConstants(camera, desktopWidth, desktopHeight, sharpness, gammaBoost, isHdr, sdrWhiteScale, 0, 0, x0, y0, x1, y1);
        _context.OMSetRenderTargets(_mediaMask!.Value.Rtv);
        _context.PSSetShader(_psMediaMask);
        _context.PSSetShaderResource(1, _poleDark!.Value.Srv);
        _context.PSSetShaderResource(2, _poleLight!.Value.Srv);
        _context.PSSetShaderResource(3, _localExt!.Value.Srv);
        _context.PSSetShaderResource(5, _metaTex!.Value.Srv);
        _context.Draw(4, 0);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _hasMediaMask = true;
    }

    private void UnbindShaderResources()
    {
        // Literal slots compile against both int and uint overloads of the Vortice API.
        _context.PSSetShaderResource(0, null!);
        _context.PSSetShaderResource(1, null!);
        _context.PSSetShaderResource(2, null!);
        _context.PSSetShaderResource(3, null!);
        _context.PSSetShaderResource(4, null!);
        _context.PSSetShaderResource(5, null!);
        _context.PSSetShaderResource(6, null!);
    }

    private void EnsurePoleTargets(int desktopWidth, int desktopHeight, bool isHdr)
    {
        // All intermediate targets store perceptual sRGB [0, 1] or normalised metadata in 8-bit UNorm,
        // halving VRAM bandwidth and maximizing AMD RDNA 2 L0/L1 cache hit rates.
        _ = isHdr;
        Format format = Format.R8G8B8A8_UNorm;
        if (_poleDark is not null && _poleWidth == desktopWidth && _poleHeight == desktopHeight && _poleFormat == format)
        {
            return;
        }

        ReleasePoleTargets();

        Texture2DDescription desc = new()
        {
            Width = (uint)Math.Max(1, desktopWidth),
            Height = (uint)Math.Max(1, desktopHeight),
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };

        Texture2DDescription mediaDesc = desc;
        mediaDesc.Width = (uint)Math.Max(1, (desktopWidth + 1) / 2);
        mediaDesc.Height = (uint)Math.Max(1, (desktopHeight + 1) / 2);
        mediaDesc.Format = Format.R8G8_UNorm;

        _percepTex = CreateRenderTarget(desc);
        _metaTex = CreateRenderTarget(desc);
        _poleDark = CreateRenderTarget(desc);
        _poleLight = CreateRenderTarget(desc);
        _localExt = CreateRenderTarget(desc);
        _mediaMask = CreateRenderTarget(mediaDesc);
        (_poleWidth, _poleHeight, _poleFormat) = (desktopWidth, desktopHeight, format);
        _hasPoles = false;
        _hasMediaMask = false;
    }

    private RenderTarget CreateRenderTarget(Texture2DDescription desc)
    {
        ID3D11Texture2D texture = _device.CreateTexture2D(desc);
        return new RenderTarget(texture, _device.CreateRenderTargetView(texture), _device.CreateShaderResourceView(texture));
    }

    private void ReleasePoleTargets()
    {
        _percepTex?.Dispose(); _percepTex = null;
        _metaTex?.Dispose(); _metaTex = null;
        _poleDark?.Dispose(); _poleDark = null;
        _poleLight?.Dispose(); _poleLight = null;
        _localExt?.Dispose(); _localExt = null;
        _mediaMask?.Dispose(); _mediaMask = null;
        _hasPoles = false;
        _hasMediaMask = false;
    }

    private unsafe void WriteConstants(
        CameraController camera, int desktopWidth, int desktopHeight,
        float sharpness, float gammaBoost, bool isHdr, float sdrWhiteScale, int colorMode, int mediaMode,
        int cropX0, int cropY0, int cropX1, int cropY1)
    {
        float invW = 1.0f / Math.Max(1, desktopWidth);
        float invH = 1.0f / Math.Max(1, desktopHeight);

        MappedSubresource mapped = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            ShaderParams* p = (ShaderParams*)mapped.DataPointer;
            p->FocusX = camera.FocusX;
            p->FocusY = camera.FocusY;
            p->Zoom = camera.Zoom;
            p->Sharpness = Math.Clamp(sharpness, 0.0f, 1.0f);
            p->ScreenWidth = camera.ScreenWidth;
            p->ScreenHeight = camera.ScreenHeight;
            p->DesktopWidth = desktopWidth;
            p->DesktopHeight = desktopHeight;
            p->TextGamma = Math.Clamp(gammaBoost, -0.5f, 1.0f);
            p->IsHdr = isHdr ? 1.0f : 0.0f;
            p->SdrWhiteScale = MathF.Max(0.25f, sdrWhiteScale);
            p->ColorMode = colorMode;
            p->CropMinU = cropX0 * invW;
            p->CropMinV = cropY0 * invH;
            p->CropMaxU = cropX1 * invW;
            p->CropMaxV = cropY1 * invH;
            p->MediaMode = mediaMode;
            p->Pad0 = 0.0f;
            p->Pad1 = 0.0f;
            p->Pad2 = 0.0f;
        }
        finally
        {
            _context.Unmap(_constantBuffer, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        ReleasePoleTargets();
        _desktopSrv?.Dispose();
        _noCullRasterizerState.Dispose();
        _alphaBlendState.Dispose();
        _opaqueBlendState.Dispose();
        _linearSampler.Dispose();
        _cursorConstantBuffer.Dispose();
        _constantBuffer.Dispose();
        _psCursor.Dispose();
        _vsCursor.Dispose();
        _ps.Dispose();
        _psMediaMask.Dispose();
        _psPole.Dispose();
        _psPrepMeta.Dispose();
        _psPrepColor.Dispose();
        _vsCrop.Dispose();
        _vs.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Shader loading: HLSL is an embedded resource; compiled bytecode is cached per source hash
    // in %LocalAppData%\ZoomText_Clone\ShaderCache so only the first start compiles.
    // ---------------------------------------------------------------------------------------

    private static ShaderBytecode LoadOrCompileShaders()
    {
        string source = ReadEmbeddedHlsl();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("O3|" + source)));
        string portableCacheDir = Path.Combine(AppContext.BaseDirectory, "ShaderCache");
        string localCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomText_Clone",
            "ShaderCache");

        string[] names = ["vs", "vscrop", "psprepcolor", "psprepmeta", "pspole", "psmediamask", "ps", "vscursor", "pscursor"];
        string[] portablePaths = new string[names.Length];
        string[] localPaths = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            portablePaths[i] = Path.Combine(portableCacheDir, $"{names[i]}_{hash}.cso");
            localPaths[i] = Path.Combine(localCacheDir, $"{names[i]}_{hash}.cso");
        }

        // 1) Portable USB-stick cache next to ZoomHost.exe: instant 0 ms load on any guest Windows PC!
        if (TryReadCache(portablePaths, out ShaderBytecode cached))
        {
            return cached;
        }

        // 2) Local user AppData cache.
        if (TryReadCache(localPaths, out cached))
        {
            return cached;
        }

        byte[] vs = [], vsCrop = [], psPrepColor = [], psPrepMeta = [], psPole = [], psMediaMask = [], ps = [], vsCursor = [], psCursor = [];
        System.Threading.Tasks.Parallel.Invoke(
            () => vs = Compile(source, "VSMain", "vs_5_0"),
            () => vsCrop = Compile(source, "VSCropMain", "vs_5_0"),
            () => psPrepColor = Compile(source, "PrepColorPS", "ps_5_0"),
            () => psPrepMeta = Compile(source, "PrepMetaPS", "ps_5_0"),
            () => psPole = Compile(source, "PolePS", "ps_5_0"),
            () => psMediaMask = Compile(source, "MediaMaskPS", "ps_5_0"),
            () => ps = Compile(source, "PSMain", "ps_5_0"),
            () => vsCursor = Compile(source, "VSCursor", "vs_5_0"),
            () => psCursor = Compile(source, "PSCursor", "ps_5_0"));
        ShaderBytecode compiled = new(vs, vsCrop, psPrepColor, psPrepMeta, psPole, psMediaMask, ps, vsCursor, psCursor);

        foreach (string[] targetPaths in new[] { localPaths, portablePaths })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPaths[0])!);
                File.WriteAllBytes(targetPaths[0], compiled.Vs);
                File.WriteAllBytes(targetPaths[1], compiled.VsCrop);
                File.WriteAllBytes(targetPaths[2], compiled.PsPrepColor);
                File.WriteAllBytes(targetPaths[3], compiled.PsPrepMeta);
                File.WriteAllBytes(targetPaths[4], compiled.PsPole);
                File.WriteAllBytes(targetPaths[5], compiled.PsMediaMask);
                File.WriteAllBytes(targetPaths[6], compiled.Ps);
                File.WriteAllBytes(targetPaths[7], compiled.VsCursor);
                File.WriteAllBytes(targetPaths[8], compiled.PsCursor);
            }
            catch (IOException)
            {
                // Cache is an optimisation only (e.g. read-only USB stick already has portablePaths).
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return compiled;
    }

    private static bool TryReadCache(string[] paths, out ShaderBytecode bytecode)
    {
        bytecode = default;
        try
        {
            if (!paths.All(File.Exists))
            {
                return false;
            }

            byte[][] data = paths.Select(File.ReadAllBytes).ToArray();
            if (data.Any(d => d.Length == 0))
            {
                return false;
            }

            bytecode = new ShaderBytecode(data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7], data[8]);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Compile(string source, string entryPoint, string profile) =>
        Compiler.Compile(source, entryPoint, HlslSourceName, profile, ShaderFlags.OptimizationLevel3).ToArray();

    private static string ReadEmbeddedHlsl()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(HlslResourceName)
            ?? throw new InvalidOperationException($"Embedded shader resource '{HlslResourceName}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
