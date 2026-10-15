using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using LIMORA.Infrastructure;
using LIMORA.Models;

namespace LIMORA.Services.Settings;

public sealed class SettingsService(LocalLogger logger)
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LIMORA");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = SettingsSerialization.Deserialize(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Write("warning", "settings_load_failed", ex.GetType().Name);
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, SettingsSerialization.Serialize(settings));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
        logger.Write("info", "settings_saved", "Local display settings were saved.");
    }

    public void ApplyStartupRegistration(bool enabled, string? executablePath)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (!enabled || string.IsNullOrWhiteSpace(executablePath))
        {
            runKey.DeleteValue("LIMORA", throwOnMissingValue: false);
            return;
        }

        runKey.SetValue("LIMORA", $"\"{executablePath}\" --startup", RegistryValueKind.String);
    }
}

public static class SettingsSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    public static AppSettings Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }
}
