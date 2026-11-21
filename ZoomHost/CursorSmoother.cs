namespace ZoomHost;

/// <summary>
/// Removes the visible stepping of the magnified cursor at high zoom.
/// <para>
/// Windows reports the cursor in whole desktop pixels, so at zoom Z every mouse step moves the
/// drawn cursor (and an edge-panning viewport) by Z screen pixels: at 16x that is a 16 px jump.
/// This filter lets the drawn position glide between those steps with a time constant that grows
/// with the zoom (no filtering at low zoom, ~30 ms at 11x and above). Large jumps (focus tracking,
/// caret jumps) are applied instantly so the cursor never lags behind a deliberate move.
/// </para>
/// </summary>
public sealed class CursorSmoother
{
    /// <summary>Time constant added per zoom step above 1x, in seconds.</summary>
    private const float TimeConstantPerZoom = 0.001f;

    /// <summary>Upper bound of the time constant, in seconds (keeps cursor snappy even at high zoom).</summary>
    private const float MaxTimeConstant = 0.008f;

    /// <summary>Moves larger than this many desktop pixels are applied without smoothing.</summary>
    private const float SnapDistance = 48.0f;

    /// <summary>Below this residual distance (desktop pixels) the output snaps onto the target.</summary>
    private const float SettleDistance = 0.01f;

    private bool _initialized;

    public float X { get; private set; }
    public float Y { get; private set; }

    /// <summary>Disabled so cursor movement stays 1:1 locked to OS input with zero smoothing lag.</summary>
    public static bool IsActive(float zoom) => false;

    /// <summary>Advances the filter towards the measured cursor position.</summary>
    /// <param name="targetX">Measured cursor X in desktop (world) pixels.</param>
    /// <param name="targetY">Measured cursor Y in desktop (world) pixels.</param>
    /// <param name="deltaSeconds">Time since the previous update.</param>
    /// <param name="zoom">Current magnification.</param>
    public void Update(float targetX, float targetY, float deltaSeconds, float zoom)
    {
        float dx = targetX - X;
        float dy = targetY - Y;
        float tau = Math.Clamp((zoom - 1.0f) * TimeConstantPerZoom, 0.0f, MaxTimeConstant);

        if (!_initialized || !IsActive(zoom) || tau <= 0.0f || (dx * dx + dy * dy) > SnapDistance * SnapDistance)
        {
            Reset(targetX, targetY);
            return;
        }

        float alpha = 1.0f - MathF.Exp(-Math.Max(deltaSeconds, 0.0f) / tau);
        X += dx * alpha;
        Y += dy * alpha;

        if (MathF.Abs(targetX - X) < SettleDistance && MathF.Abs(targetY - Y) < SettleDistance)
        {
            X = targetX;
            Y = targetY;
        }
    }

    public void Reset(float x, float y)
    {
        X = x;
        Y = y;
        _initialized = true;
    }
}
