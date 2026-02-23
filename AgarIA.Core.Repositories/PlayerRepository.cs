using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories.Contracts;

namespace AgarIA.Core.Repositories;

public class PlayerRepository : BaseRepository<Player>
{
    public PlayerRepository(GameState gameState) : base(gameState)
    {
    }

    public void Add(Player player)
    {
        GameState.Players[player.Id] = player;
    }

    public void Remove(string id)
    {
        GameState.Players.TryRemove(id, out _);
    }

    public Player Get(string id)
    {
        GameState.Players.TryGetValue(id, out var player);
        return player;
    }

    public IEnumerable<Player> GetAll()
    {
        return GameState.Players.Values;
    }

    public IEnumerable<Player> GetAlive()
    {
        return GameState.Players.Values.Where(p => p.IsAlive);
    }

    public IEnumerable<Player> GetByOwner(string ownerId)
    {
        return GameState.Players.Values.Where(p => p.IsAlive && p.OwnerId == ownerId);
    }
}
