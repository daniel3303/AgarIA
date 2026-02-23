namespace AgarIA.Core.Data.Models;

public class GameSettings
{
    public int MinAIPlayers { get; set; } = 10;

    public int MaxAIPlayers { get; set; } = 100;

    public double ResetAtScore { get; set; } = 5000;

    public int AutoResetSeconds { get; set; }

    public bool MaxSpeed { get; set; }

    public ResetType ResetType { get; set; }
}
