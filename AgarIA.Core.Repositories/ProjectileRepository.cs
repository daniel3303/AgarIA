using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories.Contracts;

namespace AgarIA.Core.Repositories;

public class ProjectileRepository : BaseRepository<Projectile>
{
    public ProjectileRepository(GameState gameState) : base(gameState)
    {
    }

    public void Add(Projectile projectile)
    {
        GameState.Projectiles[projectile.Id] = projectile;
    }

    public void Remove(int id)
    {
        GameState.Projectiles.TryRemove(id, out _);
    }

    public IEnumerable<Projectile> GetAlive()
    {
        return GameState.Projectiles.Values.Where(p => p.IsAlive);
    }

    public IEnumerable<Projectile> GetAll()
    {
        return GameState.Projectiles.Values;
    }

    public int NextId()
    {
        return Interlocked.Increment(ref GameState.NextProjectileId);
    }
}
