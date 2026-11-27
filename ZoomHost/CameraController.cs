using System.Numerics;

namespace ZoomHost;

public readonly record struct WorldBounds(float Left, float Top, float Right, float Bottom)
{
    public bool Intersects(float left, float top, float right, float bottom) =>
        right >= Left && left <= Right && bottom >= Top && top <= Bottom;
}

/// <summary>
/// Controls the fullscreen magnifier camera with subpixel accuracy,
/// exponential mouse-follow smoothing, and World-to-Screen matrix calculation.
/// </summary>
public sealed class CameraController
{
    private readonly object _stateLock = new();
    public const float MinZoom = 1.0f;
    public const float MaxZoom = 60.0f;

    private static readonly float[] BaseZoomSteps = [1.0f, 2.0f, 4.0f, 8.0f, 10.0f, 12.0f];

    private const float PanSmoothingSpeed = 85.0f;
    private const float ZoomSmoothingSpeed = 52.0f;
    private const float EdgePanMarginScreenPx = 56.0f;

    public float ScreenWidth { get; private set; }
    public float ScreenHeight { get; private set; }

    public float FocusX { get; private set; }
    public float FocusY { get; private set; }

    public float Zoom { get; private set; } = 1.0f;
    public float TargetZoom { get; private set; } = 1.0f;
    private bool _centerUntilSettled;
    private bool _centerExplicitTarget;
    private bool _zoomAnchoring;
    private bool _needsAnchorCapture;
    private float _anchorScreenX;
    private float _anchorScreenY;

    public bool IsAnimating
    {
        get
        {
            lock (_stateLock)
            {
                return MathF.Abs(TargetZoom - Zoom) > 0.001f || _centerUntilSettled || _zoomAnchoring;
            }
        }
    }

    /// <summary>
    /// Transforms desktop world coordinates into screen coordinates:
    /// x_screen = (x_world - FocusX) * Zoom + ScreenWidth / 2.0f
    /// y_screen = (y_world - FocusY) * Zoom + ScreenHeight / 2.0f
    /// </summary>
    public Matrix3x2 WorldToScreenMatrix { get; private set; } = Matrix3x2.Identity;

    public CameraController(float screenWidth, float screenHeight)
    {
        Resize(screenWidth, screenHeight);
        FocusX = ScreenWidth / 2.0f;
        FocusY = ScreenHeight / 2.0f;
        RecalculateMatrix();
    }

    public void Resize(float screenWidth, float screenHeight)
    {
        ScreenWidth = MathF.Max(1.0f, screenWidth);
        ScreenHeight = MathF.Max(1.0f, screenHeight);
        ClampFocus();
        RecalculateMatrix();
    }

    private static float GetZoomStepAt(int index)
    {
        if (index < BaseZoomSteps.Length)
        {
            return BaseZoomSteps[index];
        }

        return 12.0f + (index - (BaseZoomSteps.Length - 1)) * 4.0f;
    }

    private void ActivateMouseAnchor()
    {
        _centerUntilSettled = false;
        _centerExplicitTarget = false;
        if (!_zoomAnchoring)
        {
            _zoomAnchoring = true;
            _needsAnchorCapture = true;
        }
    }

    public void ZoomIn()
    {
        lock (_stateLock)
        {
            for (int i = 0; ; i++)
            {
                float step = GetZoomStepAt(i);
                if (step >= MaxZoom)
                {
                    TargetZoom = MaxZoom;
                    ActivateMouseAnchor();
                    return;
                }
                if (step > TargetZoom + 0.01f)
                {
                    TargetZoom = Math.Clamp(step, MinZoom, MaxZoom);
                    ActivateMouseAnchor();
                    return;
                }
            }
        }
    }

    public void ZoomOut()
    {
        lock (_stateLock)
        {
            float prev = MinZoom;
            for (int i = 0; ; i++)
            {
                float step = GetZoomStepAt(i);
                if (step >= TargetZoom - 0.01f)
                {
                    TargetZoom = Math.Clamp(prev, MinZoom, MaxZoom);
                    ActivateMouseAnchor();
                    return;
                }
                prev = step;
                if (step >= MaxZoom)
                {
                    TargetZoom = MaxZoom;
                    ActivateMouseAnchor();
                    return;
                }
            }
        }
    }

    public void ZoomBy(float factor)
    {
        lock (_stateLock)
        {
            TargetZoom = Math.Clamp(TargetZoom * factor, MinZoom, MaxZoom);
            ActivateMouseAnchor();
        }
    }

    /// <summary>Sets the target zoom directly (e.g. the persisted value at startup).</summary>
    public void SetTargetZoom(float zoom)
    {
        lock (_stateLock)
        {
            TargetZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            _zoomAnchoring = false;
            _needsAnchorCapture = false;
            _centerUntilSettled = true;
        }
    }

    public void RequestCenterOnTarget()
    {
        lock (_stateLock)
        {
            _zoomAnchoring = false;
            _needsAnchorCapture = false;
            _centerUntilSettled = true;
            _centerExplicitTarget = true;
        }
    }

    public void ResetZoom()
    {
        lock (_stateLock)
        {
            TargetZoom = MinZoom;
            Zoom = MinZoom;
            _zoomAnchoring = false;
            _needsAnchorCapture = false;
            _centerUntilSettled = false;
            _centerExplicitTarget = false;
            ClampFocus();
            RecalculateMatrix();
        }
    }

    public void Update(float deltaTime, float mouseWorldX, float mouseWorldY, float comfortRatio = 1.0f)
    {
        lock (_stateLock)
        {
            float dt = Math.Clamp(deltaTime, 0.0f, 0.033f);

            // Capture the cursor's current screen-space anchor BEFORE changing Zoom so zooming
            // keeps the exact world point under the mouse cursor stationary on screen.
            if (_zoomAnchoring && _needsAnchorCapture)
            {
                if (Zoom <= MinZoom + 0.001f)
                {
                    _anchorScreenX = Math.Clamp(mouseWorldX, 0.0f, ScreenWidth);
                    _anchorScreenY = Math.Clamp(mouseWorldY, 0.0f, ScreenHeight);
                }
                else
                {
                    _anchorScreenX = Math.Clamp((mouseWorldX - FocusX) * Zoom + ScreenWidth * 0.5f, 0.0f, ScreenWidth);
                    _anchorScreenY = Math.Clamp((mouseWorldY - FocusY) * Zoom + ScreenHeight * 0.5f, 0.0f, ScreenHeight);
                }
                _needsAnchorCapture = false;
            }

            // Smoothly interpolate zoom level logarithmically for perceptually uniform speed.
            float zoomAlpha = 1.0f - MathF.Exp(-ZoomSmoothingSpeed * dt);
            float logZoom = MathF.Log(MathF.Max(Zoom, MinZoom));
            float logTarget = MathF.Log(MathF.Max(TargetZoom, MinZoom));
            Zoom = Math.Clamp(MathF.Exp(logZoom + (logTarget - logZoom) * zoomAlpha), MinZoom, MaxZoom);
            if (MathF.Abs(TargetZoom - Zoom) < 0.001f)
            {
                Zoom = TargetZoom;
            }

            // Clamp target focus to visible viewport bounds so edges align cleanly
            float halfViewW = (ScreenWidth / 2.0f) / Zoom;
            float halfViewH = (ScreenHeight / 2.0f) / Zoom;

            float minFocusX = halfViewW;
            float maxFocusX = MathF.Max(halfViewW, ScreenWidth - halfViewW);
            float minFocusY = halfViewH;
            float maxFocusY = MathF.Max(halfViewH, ScreenHeight - halfViewH);

            if (_zoomAnchoring)
            {
                FocusX = Math.Clamp(mouseWorldX - (_anchorScreenX - ScreenWidth * 0.5f) / Zoom, minFocusX, maxFocusX);
                FocusY = Math.Clamp(mouseWorldY - (_anchorScreenY - ScreenHeight * 0.5f) / Zoom, minFocusY, maxFocusY);
                if (MathF.Abs(TargetZoom - Zoom) <= 0.001f)
                {
                    _zoomAnchoring = false;
                }
                ClampFocus();
                RecalculateMatrix();
                return;
            }

            // Center onto cursor ONLY while zooming (or explicit FocusTracker jump);
            // once zoom settles, switch immediately to true edge-panning so the cursor moves
            // freely inside the viewport and the screen only pans at the left/right/top/bottom border.
            float targetFocusX;
            float targetFocusY;

            if (_centerExplicitTarget && comfortRatio >= 0.99f)
            {
                _centerUntilSettled = false;
                _centerExplicitTarget = false;
            }

            if (_centerUntilSettled)
            {
                targetFocusX = Math.Clamp(mouseWorldX, minFocusX, maxFocusX);
                targetFocusY = Math.Clamp(mouseWorldY, minFocusY, maxFocusY);
                bool zoomSettled = MathF.Abs(TargetZoom - Zoom) <= 0.005f;
                bool focusSettled = MathF.Abs(FocusX - targetFocusX) <= 1.0f && MathF.Abs(FocusY - targetFocusY) <= 1.0f;
                if ((zoomSettled && !_centerExplicitTarget) || (zoomSettled && focusSettled))
                {
                    _centerUntilSettled = false;
                    _centerExplicitTarget = false;
                }
            }
            else
            {
                float innerHalfW;
                float innerHalfH;
                if (comfortRatio >= 0.99f)
                {
                    float marginWorldX = MathF.Min(halfViewW * 0.25f, EdgePanMarginScreenPx / Zoom);
                    float marginWorldY = MathF.Min(halfViewH * 0.25f, EdgePanMarginScreenPx / Zoom);
                    innerHalfW = MathF.Max(0.0f, halfViewW - marginWorldX);
                    innerHalfH = MathF.Max(0.0f, halfViewH - marginWorldY);
                }
                else
                {
                    float clampedComfort = Math.Clamp(comfortRatio, 0.0f, 0.96f);
                    innerHalfW = halfViewW * clampedComfort;
                    innerHalfH = halfViewH * clampedComfort;
                }

                targetFocusX = FocusX;
                if (mouseWorldX < FocusX - innerHalfW)
                {
                    targetFocusX = mouseWorldX + innerHalfW;
                }
                else if (mouseWorldX > FocusX + innerHalfW)
                {
                    targetFocusX = mouseWorldX - innerHalfW;
                }

                targetFocusY = FocusY;
                if (mouseWorldY < FocusY - innerHalfH)
                {
                    targetFocusY = mouseWorldY + innerHalfH;
                }
                else if (mouseWorldY > FocusY + innerHalfH)
                {
                    targetFocusY = mouseWorldY - innerHalfH;
                }

                targetFocusX = Math.Clamp(targetFocusX, minFocusX, maxFocusX);
                targetFocusY = Math.Clamp(targetFocusY, minFocusY, maxFocusY);
            }

            // Direct mouse edge-panning is 1:1 instant (zero rubber-band delay); only zoom/focus jumps smooth.
            bool directMouseFollow = !_centerUntilSettled && !_centerExplicitTarget && comfortRatio >= 0.99f;
            if (directMouseFollow)
            {
                FocusX = targetFocusX;
                FocusY = targetFocusY;
            }
            else
            {
                float panAlpha = 1.0f - MathF.Exp(-PanSmoothingSpeed * dt);
                float dx = targetFocusX - FocusX;
                float dy = targetFocusY - FocusY;
                FocusX = MathF.Abs(dx) < 0.04f ? targetFocusX : FocusX + dx * panAlpha;
                FocusY = MathF.Abs(dy) < 0.04f ? targetFocusY : FocusY + dy * panAlpha;
            }

            ClampFocus();
            RecalculateMatrix();
        }
    }

    private void ClampFocus()
    {
        float halfViewW = (ScreenWidth / 2.0f) / Zoom;
        float halfViewH = (ScreenHeight / 2.0f) / Zoom;

        FocusX = Math.Clamp(FocusX, halfViewW, MathF.Max(halfViewW, ScreenWidth - halfViewW));
        FocusY = Math.Clamp(FocusY, halfViewH, MathF.Max(halfViewH, ScreenHeight - halfViewH));
    }

    public WorldBounds VisibleWorldBounds
    {
        get
        {
            lock (_stateLock)
            {
                return ComputeVisibleBounds();
            }
        }
    }

    public void GetSnapshot(out float zoom, out WorldBounds visibleBounds)
    {
        lock (_stateLock)
        {
            zoom = Zoom;
            visibleBounds = ComputeVisibleBounds();
        }
    }

    private WorldBounds ComputeVisibleBounds()
    {
        float halfViewW = (ScreenWidth / 2.0f) / Zoom;
        float halfViewH = (ScreenHeight / 2.0f) / Zoom;
        return new WorldBounds(
            FocusX - halfViewW,
            FocusY - halfViewH,
            FocusX + halfViewW,
            FocusY + halfViewH);
    }

    private void RecalculateMatrix()
    {
        lock (_stateLock)
        {
            // Row-vector Matrix3x2:
            // x_screen = x_world * M11 + y_world * M21 + M31
            // y_screen = x_world * M12 + y_world * M22 + M32
            WorldToScreenMatrix = new Matrix3x2(
                Zoom, 0.0f,
                0.0f, Zoom,
                -FocusX * Zoom + ScreenWidth / 2.0f,
                -FocusY * Zoom + ScreenHeight / 2.0f
            );
        }
    }
}

