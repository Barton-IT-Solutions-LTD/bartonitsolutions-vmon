using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualScreenViewer;

/// <summary>
/// The virtual display's refresh rate and resolution, from VirtualScreenViewer.json next to the
/// exe. The file is created with the defaults the first time the app runs, and changes to it are
/// applied as soon as it's saved.
/// </summary>
internal sealed class ViewerSettings
{
    [JsonPropertyName("_help")]
    public string Help { get; set; } =
        "refreshRate: Hz for the virtual display (0 = leave it as Windows has it). width/height: its resolution "
        + "(0 = keep the one set in Windows' display settings). Only the modes in C:\\VirtualDisplayDriver\\vdd_settings.xml work. "
        + "fitToWindow: match the display's shape to the window. menuHotkey opens the settings menu (e.g. Ctrl+Alt+S; empty = none). Easier: change these in that menu.";

    // The driver offers up to 165 Hz, but every extra refresh costs compositor time for a screen
    // nobody's looking at directly, so 60 is the default.
    [JsonPropertyName("refreshRate")] public int RefreshRate { get; set; } = 60;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    /// <summary>Change the virtual display's resolution to match the window's shape when it's resized.</summary>
    [JsonPropertyName("fitToWindow")] public bool FitToWindow { get; set; }
    [JsonPropertyName("menuHotkey")] public string MenuHotkey { get; set; } = "Ctrl+Alt+S";
    /// <summary>Where the viewer window was last: x, y, width, height (physical pixels), and 1 if maximised.</summary>
    [JsonPropertyName("window")] public int[]? Window { get; set; }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }

    /// <summary>"Ctrl+Alt+V" as RegisterHotKey modifiers and key; null if it isn't a valid hotkey. Empty = none.</summary>
    public static (uint Mods, Keys Key)? ParseHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0; Keys key = Keys.None;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                default:
                    if (key != Keys.None) return null;
                    if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
                    if (!Enum.TryParse(part, true, out key) || key == Keys.None) return null;
                    break;
            }
        }
        return key == Keys.None || mods == 0 ? null : (mods, key);
    }

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "VirtualScreenViewer.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "Windows'" not "Windows'"
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The saved settings, or the defaults (writing them out) if there's no file yet.</summary>
    public static ViewerSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(FilePath))
        {
            var defaults = new ViewerSettings();
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, Json)); } catch { }
            return defaults;
        }
        try
        {
            var s = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(FilePath), Json) ?? new ViewerSettings();
            if (s.RefreshRate < 0 || s.Width < 0 || s.Height < 0 || (s.Width == 0) != (s.Height == 0))
            {
                problem = "Use 0 or positive numbers, and set width and height together (or both to 0). Using the defaults for now.";
                return new ViewerSettings();
            }
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            problem = $"Couldn't read it ({ex.Message.Split('.')[0]}). Using the defaults for now.";
            return new ViewerSettings();
        }
    }
}
