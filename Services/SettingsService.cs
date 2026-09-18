using System.IO;
using System.Text.Json;
using XIslandFluent.Models;

namespace XIslandFluent.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIslandFluent");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch { }
        return new();
    }

    public void Save(AppSettings settings) => File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
}
