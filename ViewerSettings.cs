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
        + "(0 = keep the one set in Windows' display settings). Only the modes in C:\\VirtualDisplayDriver\\vdd_settings.xml work.";

    // The driver offers up to 165 Hz, but every extra refresh costs compositor time for a screen
    // nobody's looking at directly, so 60 is the default.
    [JsonPropertyName("refreshRate")] public int RefreshRate { get; set; } = 60;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

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
