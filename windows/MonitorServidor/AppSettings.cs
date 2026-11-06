using System.Text.Json;

namespace MonitorServidor;

/// <summary>Ajustes que se guardan en %AppData%\MonitorServidor\settings.json.</summary>
internal sealed class AppSettings
{
    public string ScreenName { get; set; } = "";
    public int MaxWidth { get; set; } = 1280;
    public int Quality { get; set; } = 70;
    public int Fps { get; set; } = 30;
    public bool AutoStart { get; set; } = false;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MonitorServidor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Archivo dañado: se usan los valores por defecto.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Si no se puede guardar, no es crítico.
        }
    }
}
