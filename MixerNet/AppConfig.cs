using System.Text.Json;
using MixerNet.Audio;

namespace MixerNet;

/// <summary>
/// Persisted settings in Mixer.json next to Mixer.exe (portable).
/// On first run, settings from the old %APPDATA%\Mixer\config.json are imported.
/// If the exe folder is read-only, falls back to %APPDATA%\Mixer\config.json.
/// </summary>
sealed class AppConfig
{
    public string? SourceA { get; set; }
    public string? SourceB { get; set; }
    public string? Output { get; set; }
    public OutputApi OutputApi { get; set; } = OutputApi.Auto;
    public int VolA { get; set; } = 100;
    public int VolB { get; set; } = 100;
    public int PanA { get; set; }
    public int PanB { get; set; }
    public int Master { get; set; } = 100;
    public bool MuteA { get; set; }
    public bool MuteB { get; set; }
    public bool Split { get; set; }
    public bool InvertA { get; set; }
    public bool InvertB { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Mixer.json beside the exe (AppContext.BaseDirectory is the exe folder for single-file apps).</summary>
    public static string PortablePath => Path.Combine(AppContext.BaseDirectory, "Mixer.json");

    static string AppDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mixer", "config.json");

    /// <summary>Where settings were last loaded from / will be saved to.</summary>
    public static string FilePath { get; private set; } = PortablePath;

    public static AppConfig Load()
    {
        // Prefer the file beside the exe; otherwise import the old %APPDATA% one.
        foreach (var path in new[] { PortablePath, AppDataPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
                if (cfg != null) return cfg;
            }
            catch { /* corrupt or unreadable: try the next one */ }
        }
        return new AppConfig();
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, Options);
        try
        {
            WriteAtomic(PortablePath, json);
            FilePath = PortablePath;
            return;
        }
        catch { /* exe folder not writable (e.g. Program Files): use %APPDATA% */ }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppDataPath)!);
            WriteAtomic(AppDataPath, json);
            FilePath = AppDataPath;
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Write to a temp file then swap, so a crash mid-save can't leave a truncated file.</summary>
    static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
