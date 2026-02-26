namespace AgarIA.Core.Data.Models;

public class GameSettings
{
    public double ResetAtScore { get; set; } = 5000;

    public int MinResetSeconds { get; set; } = 600;

    public int MaxResetSeconds { get; set; } = 1200;

    public bool MaxSpeed { get; set; }

    public int SpeedMultiplier { get; set; } = 1;

    public ResetType ResetType { get; set; }

    public bool HeuristicEnabled { get; set; } = true;

    public int HeuristicPlayerCount { get; set; } = 0;

    public bool HeuristicCanEatEachOther { get; set; } = true;
}
