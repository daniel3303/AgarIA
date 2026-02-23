using System.Collections.Concurrent;

namespace AgarIA.Core.Data.Models;

public class GameState
{
    public ConcurrentDictionary<string, Player> Players { get; set; } = new();
    public ConcurrentDictionary<int, FoodItem> Food { get; set; } = new();
    public ConcurrentDictionary<int, Projectile> Projectiles { get; set; } = new();
    public int NextFoodId;
    public int NextProjectileId;
    public long CurrentTick;
    public ConcurrentDictionary<string, bool> Spectators { get; set; } = new();
}
