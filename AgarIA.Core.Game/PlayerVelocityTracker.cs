using System.Collections.Concurrent;
using AgarIA.Core.Data.Models;

namespace AgarIA.Core.Game;

public class PlayerVelocityTracker
{
    private readonly ConcurrentDictionary<string, PlayerRecord> _records = new();

    public void Update(List<Player> allPlayers)
    {
        foreach (var p in allPlayers)
        {
            if (_records.TryGetValue(p.Id, out var rec))
            {
                rec.Vx = p.X - rec.PrevX;
                rec.Vy = p.Y - rec.PrevY;
                rec.PrevX = p.X;
                rec.PrevY = p.Y;
                rec.HasPrev = true;
            }
            else
            {
                _records[p.Id] = new PlayerRecord { PrevX = p.X, PrevY = p.Y };
            }
        }
    }

    public (double vx, double vy) GetVelocity(string id)
    {
        if (_records.TryGetValue(id, out var rec) && rec.HasPrev)
            return (rec.Vx, rec.Vy);
        return (0, 0);
    }

    public void Remove(string id) => _records.TryRemove(id, out _);

    private class PlayerRecord
    {
        public double PrevX;
        public double PrevY;
        public double Vx;
        public double Vy;
        public bool HasPrev;
    }
}
