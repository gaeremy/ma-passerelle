// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace MaPasserelle.App;

/// <summary>Réglages mémorisés entre deux lancements (%APPDATA%\MaPasserelle\reglages.json).</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaPasserelle", "reglages.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Dernier téléviseur utilisé : identifiant, nom et adresse (pour le retrouver sans recherche).</summary>
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceAddress { get; set; }
    public int DevicePort { get; set; } = Engine.Devices.DefaultPort;

    /// <summary>Hauteur maximale : 0 = automatique, 720, 1080 ou 2160.</summary>
    public int MaxHeight { get; set; }
    public int Fps { get; set; } = 30;
    public bool Audio { get; set; } = true;
    public bool Cursor { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
