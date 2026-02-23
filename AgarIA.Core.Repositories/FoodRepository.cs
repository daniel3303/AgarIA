using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories.Contracts;

namespace AgarIA.Core.Repositories;

public class FoodRepository : BaseRepository<FoodItem>
{
    public FoodRepository(GameState gameState) : base(gameState)
    {
    }

    public void Add(FoodItem food)
    {
        GameState.Food[food.Id] = food;
    }

    public void Remove(int id)
    {
        GameState.Food.TryRemove(id, out _);
    }

    public FoodItem Get(int id)
    {
        GameState.Food.TryGetValue(id, out var food);
        return food;
    }

    public IEnumerable<FoodItem> GetAll()
    {
        return GameState.Food.Values;
    }

    public int GetCount()
    {
        return GameState.Food.Count;
    }

    public int NextId()
    {
        return Interlocked.Increment(ref GameState.NextFoodId);
    }
}
