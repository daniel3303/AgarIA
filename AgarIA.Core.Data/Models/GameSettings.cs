namespace AgarIA.Core.Data.Models;

public class GameSettings
{
    public int MinAIPlayers { get; set; } = 5;

    public int MaxAIPlayers { get; set; } = 20;

    public double ResetAtScore { get; set; } = 5000;

    public int AutoResetSeconds { get; set; } = 1200;

    public bool MaxSpeed { get; set; }

    public ResetType ResetType { get; set; }

    public List<int> EasyHiddenLayers { get; set; } = [64];

    public List<int> MediumHiddenLayers { get; set; } = [128];

    public List<int> HardHiddenLayers { get; set; } = [256];
}
