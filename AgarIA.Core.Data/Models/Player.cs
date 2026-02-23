namespace AgarIA.Core.Data.Models;

public class Player
{
    public string Id { get; set; }
    public string Username { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Mass { get; set; } = GameConfig.StartMass;
    public double Radius => Math.Sqrt(Mass) * 4;
    public double TargetX { get; set; }
    public double TargetY { get; set; }
    public bool IsAI { get; set; }
    public bool IsAlive { get; set; } = true;
    public int Score => (int)Mass;
    public int ColorIndex { get; set; }
    public double SpeedBoostMultiplier { get; set; } = 1.0;
    public long SpeedBoostUntil { get; set; }
    public string OwnerId { get; set; }
    public long MergeAfterTick { get; set; }
    public string KilledById { get; set; }
    public double KillerMassShare { get; set; }
    public double MassEatenFromPlayers { get; set; }
    public int FoodEaten { get; set; }
    public int PlayersKilled { get; set; }
}
