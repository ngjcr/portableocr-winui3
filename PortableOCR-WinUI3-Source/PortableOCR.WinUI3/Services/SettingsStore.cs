using System.Text.Json;

namespace PortableOCR.WinUI3.Services;

public sealed record UiSettings(string Quality = "balanced", bool Text = true, bool Pdf = true, bool AutoClear = false, string Theme = "system");

public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortableOCR");
    private static readonly string FilePath = Path.Combine(Folder, "winui-settings.json");

    public static UiSettings Load()
    {
        try { return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public static void Save(UiSettings settings)
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
