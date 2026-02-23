using AgarIA.Core.Data.Models;

namespace AgarIA.Core.Repositories.Contracts;

public abstract class BaseRepository<T>
{
    protected readonly GameState GameState;

    protected BaseRepository(GameState gameState)
    {
        GameState = gameState;
    }
}
