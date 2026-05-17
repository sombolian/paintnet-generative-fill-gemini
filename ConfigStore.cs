using System;
using System.IO;
using System.Text.Json;

namespace GeminiFillPlugin;

internal sealed class PluginConfig
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.1-flash-image-preview";
}

internal static class ConfigStore
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GeminiFillPlugin");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public static PluginConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new PluginConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<PluginConfig>(json) ?? new PluginConfig();
        }
        catch
        {
            return new PluginConfig();
        }
    }

    public static void Save(PluginConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
