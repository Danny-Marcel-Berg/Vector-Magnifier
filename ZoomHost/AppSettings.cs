using System.IO;
using System.Text.Json;

namespace ZoomHost;

public enum ColorFilterMode
{
    Normal = 0,
    Invert = 1,
    YellowOnBlack = 2,
    WhiteOnBlue = 3,
    Sepia = 4
}

public enum MediaRenderingMode
{
    Auto = 0,
    AlwaysNative = 1,
    Off = 2
}

public enum VectorCursorShape
{
    Arrow = 0,
    IBeam = 1,
    Hand = 2,
    SizeWE = 3,
    SizeNS = 4,
    SizeAll = 5
}

/// <summary>
/// Persistent user settings, stored as JSON in %AppData%\ZoomHost\settings.json.
/// Values are validated on load, so a corrupt or hand-edited file can never produce invalid state.
/// </summary>
public sealed class AppSettings
{
    public double Zoom { get; set; } = 1.0;
    public double UiScale { get; set; } = 1.0;
    public double EdgeSmoothing { get; set; } = 0.0;   // slider 0..100
    public double Gamma { get; set; } = 0.0;           // slider -50..100
    public double MouseSize { get; set; } = 100.0;     // slider 50..300 (%)
    public bool VectorText { get; set; } = true;
    public bool InvertColors { get; set; } = false;
    public int ColorMode { get; set; } = 0;            // 0=Normal, 1=Invert, 2=YellowOnBlack, 3=WhiteOnBlue, 4=Sepia
    public int MediaMode { get; set; } = 0;            // 0=Auto, 1=AlwaysNative, 2=Off
    public bool InvertCursor { get; set; } = false;
    public bool CursorFocus { get; set; } = true;
    public double? PanelLeft { get; set; }
    public double? PanelTop { get; set; }
    public bool PanelCollapsed { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZoomHost",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable or corrupt file: start with defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true); // atomic replace: a crash never leaves a half-written file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting settings is best effort.
        }
    }

    private void Sanitize()
    {
        Zoom = Clamp(Zoom, CameraController.MinZoom, CameraController.MaxZoom, 1.0);
        UiScale = Clamp(UiScale, 1.0, 2.5, 1.0);
        EdgeSmoothing = Clamp(EdgeSmoothing, 0.0, 100.0, 0.0);
        Gamma = Clamp(Gamma, -50.0, 100.0, 0.0);
        MouseSize = Clamp(MouseSize, 50.0, 300.0, 100.0);
        ColorMode = Math.Clamp(ColorMode, 0, 4);
        if (ColorMode == 0 && InvertColors)
        {
            ColorMode = 1;
        }
        InvertColors = ColorMode == 1;
        MediaMode = Math.Clamp(MediaMode, 0, 2);
        if (PanelLeft is double l && !double.IsFinite(l)) PanelLeft = null;
        if (PanelTop is double t && !double.IsFinite(t)) PanelTop = null;
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
