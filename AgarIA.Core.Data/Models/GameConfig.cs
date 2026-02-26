namespace AgarIA.Core.Data.Models;

public static class GameConfig
{
    public const int MapSize = 4000;
    public const int TickRate = 20;
    public const int MaxFood = 2000;
    public const double StartMass = 10;
    public const int MaxAI = 100;
    public const double EatSizeRatio = 1.15;
    public const int SpeedBoostDuration = 20;
    public const double SpeedBoostScaleFactor = 50.0;

    public const double BaseSpeed = 4.0;
    public const double MassDecayRate = 0.99998;
    public const double MinMass = 10;
    public const double FoodMass = 1;
    public const double MinSplitMass = 24;
    public const double SplitDistance = 160;
    public const double SplitSpeed = 12.0;
    public const int MergeCooldownTicks = 200;
    public const int MaxSplitCells = 4;
    public const int SpawnProtectionTicks = 200; // 10 seconds at 20 TPS
}
