using AgarIA.Core.Data.Models;

namespace AgarIA.Core.Game;

public class SharedGrids
{
    public SpatialGrid<FoodItem> FoodGrid { get; } = new(f => f.X, f => f.Y);
    public SpatialGrid<Player> PlayerGrid { get; } = new(p => p.X, p => p.Y);
}
