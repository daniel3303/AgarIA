namespace AgarIA.Core.Data.Models;

public class GameSettings
{
    public int MinAIPlayers { get; set; } = 5;

    public int MaxAIPlayers { get; set; } = 50;

    public double ResetAtScore { get; set; } = 5000;

    public int MinResetSeconds { get; set; } = 600;

    public int MaxResetSeconds { get; set; } = 1200;

    public bool MaxSpeed { get; set; }

    public ResetType ResetType { get; set; }

    public bool EasyEnabled { get; set; } = true;

    public bool MediumEnabled { get; set; } = true;

    public bool HardEnabled { get; set; } = true;

    public List<int> EasyHiddenLayers { get; set; } = [512];

    public List<int> MediumHiddenLayers { get; set; } = [1024];

    public List<int> HardHiddenLayers { get; set; } = [1024, 1024];

    public bool HeuristicEnabled { get; set; } = true;

    public int HeuristicPlayerCount { get; set; } = 5;

    public bool HeuristicCanEatEachOther { get; set; } = true;

    public PPOSettings PPO { get; set; } = new();
}

public class PPOSettings
{
    public int BufferSize { get; set; } = 2048;
    public int MinibatchSize { get; set; } = 256;
    public int Epochs { get; set; } = 4;
    public float Gamma { get; set; } = 0.99f;
    public float Lambda { get; set; } = 0.95f;
    public float ClipEpsilon { get; set; } = 0.2f;
    public float LearningRate { get; set; } = 3e-4f;
    public float EntropyCoeff { get; set; } = 0.001f;
    public float ValueCoeff { get; set; } = 0.5f;
    public float MaxGradNorm { get; set; } = 0.5f;
}
