using AgarIA.Core.Data.Models;
using AgarIA.Web.Data;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AgarIA.Web.Services;

public static class AdminSettingsService
{
    private static readonly Dictionary<string, Action<GameSettings, string>> Loaders = new()
    {
        ["ResetType"] = (s, v) => { if (Enum.TryParse<ResetType>(v, out var r)) s.ResetType = r; },
        ["ResetAtScore"] = (s, v) => { if (double.TryParse(v, out var d)) s.ResetAtScore = d; },
        ["MinResetSeconds"] = (s, v) => { if (int.TryParse(v, out var i)) s.MinResetSeconds = i; },
        ["MaxResetSeconds"] = (s, v) => { if (int.TryParse(v, out var i)) s.MaxResetSeconds = i; },
        ["MinAIPlayers"] = (s, v) => { if (int.TryParse(v, out var i)) s.MinAIPlayers = i; },
        ["MaxAIPlayers"] = (s, v) => { if (int.TryParse(v, out var i)) s.MaxAIPlayers = i; },
        ["MaxSpeed"] = (s, v) => { if (bool.TryParse(v, out var b)) s.MaxSpeed = b; },
        ["EasyHiddenLayers"] = (s, v) => { if (JsonConvert.DeserializeObject<List<int>>(v) is { } list) s.EasyHiddenLayers = list; },
        ["MediumHiddenLayers"] = (s, v) => { if (JsonConvert.DeserializeObject<List<int>>(v) is { } list) s.MediumHiddenLayers = list; },
        ["HardHiddenLayers"] = (s, v) => { if (JsonConvert.DeserializeObject<List<int>>(v) is { } list) s.HardHiddenLayers = list; },
    };

    public static async Task Load(AdminDbContext db, GameSettings settings)
    {
        var rows = await db.AdminSettings.ToListAsync();

        foreach (var row in rows)
        {
            if (Loaders.TryGetValue(row.Key, out var apply))
                apply(settings, row.Value);
        }
    }

    public static async Task Save(AdminDbContext db, GameSettings settings)
    {
        var pairs = new Dictionary<string, string>
        {
            ["ResetType"] = settings.ResetType.ToString(),
            ["ResetAtScore"] = settings.ResetAtScore.ToString(),
            ["MinResetSeconds"] = settings.MinResetSeconds.ToString(),
            ["MaxResetSeconds"] = settings.MaxResetSeconds.ToString(),
            ["MinAIPlayers"] = settings.MinAIPlayers.ToString(),
            ["MaxAIPlayers"] = settings.MaxAIPlayers.ToString(),
            ["MaxSpeed"] = settings.MaxSpeed.ToString(),
            ["EasyHiddenLayers"] = JsonConvert.SerializeObject(settings.EasyHiddenLayers),
            ["MediumHiddenLayers"] = JsonConvert.SerializeObject(settings.MediumHiddenLayers),
            ["HardHiddenLayers"] = JsonConvert.SerializeObject(settings.HardHiddenLayers),
        };

        foreach (var (key, value) in pairs)
        {
            var existing = await db.AdminSettings.FindAsync(key);
            if (existing != null)
                existing.Value = value;
            else
                db.AdminSettings.Add(new AdminSetting { Key = key, Value = value });
        }

        await db.SaveChangesAsync();
    }
}
