using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;

namespace AgarIA.Core.Game;

[Service]
public class CollisionManager
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly GameState _gameState;

    public CollisionManager(PlayerRepository playerRepository, FoodRepository foodRepository, GameState gameState)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _gameState = gameState;
    }

    public List<(Player eater, FoodItem food)> CheckFoodCollisions()
    {
        var eaten = new List<(Player, FoodItem)>();
        var players = _playerRepository.GetAlive().ToList();
        var food = _foodRepository.GetAll().ToList();

        foreach (var player in players)
        {
            foreach (var item in food)
            {
                var dx = player.X - item.X;
                var dy = player.Y - item.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < player.Radius)
                {
                    eaten.Add((player, item));
                }
            }
        }

        return eaten;
    }

    public List<(Player eater, Player prey)> CheckPlayerCollisions()
    {
        var kills = new List<(Player, Player)>();
        var players = _playerRepository.GetAlive().ToList();

        for (int i = 0; i < players.Count; i++)
        {
            for (int j = i + 1; j < players.Count; j++)
            {
                var a = players[i];
                var b = players[j];

                if (AreOwnedBySame(a, b)) continue;

                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < Math.Max(a.Radius, b.Radius))
                {
                    if (a.Mass > b.Mass * GameConfig.EatSizeRatio)
                    {
                        kills.Add((a, b));
                    }
                    else if (b.Mass > a.Mass * GameConfig.EatSizeRatio)
                    {
                        kills.Add((b, a));
                    }
                }
            }
        }

        return kills;
    }

    public List<(Player keeper, Player merged)> CheckMerges()
    {
        var merges = new List<(Player, Player)>();
        var players = _playerRepository.GetAlive().ToList();
        var currentTick = _gameState.CurrentTick;

        for (int i = 0; i < players.Count; i++)
        {
            for (int j = i + 1; j < players.Count; j++)
            {
                var a = players[i];
                var b = players[j];

                if (!AreOwnedBySame(a, b)) continue;
                if (a.MergeAfterTick > currentTick || b.MergeAfterTick > currentTick) continue;

                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < Math.Max(a.Radius, b.Radius))
                {
                    if (a.Mass >= b.Mass)
                        merges.Add((a, b));
                    else
                        merges.Add((b, a));
                }
            }
        }

        return merges;
    }

    private static bool AreOwnedBySame(Player a, Player b)
    {
        var ownerA = a.OwnerId ?? a.Id;
        var ownerB = b.OwnerId ?? b.Id;
        return ownerA == ownerB;
    }
}
