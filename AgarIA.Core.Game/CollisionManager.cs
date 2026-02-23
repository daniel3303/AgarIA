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
    private readonly SharedGrids _grids;

    public CollisionManager(PlayerRepository playerRepository, FoodRepository foodRepository, GameState gameState, SharedGrids grids)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _gameState = gameState;
        _grids = grids;
    }

    public void RebuildGrids()
    {
        _grids.FoodGrid.Rebuild(_foodRepository.GetAll().ToList());
        _grids.PlayerGrid.Rebuild(_playerRepository.GetAlive().ToList());
    }

    public List<(Player eater, FoodItem food)> CheckFoodCollisions()
    {
        var eaten = new List<(Player, FoodItem)>();
        var players = _grids.PlayerGrid.AllItems;

        foreach (var player in players)
        {
            var nearby = _grids.FoodGrid.Query(player.X, player.Y, player.Radius);
            foreach (var item in nearby)
            {
                var dx = player.X - item.X;
                var dy = player.Y - item.Y;

                if (dx * dx + dy * dy < player.Radius * player.Radius)
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
        var players = _grids.PlayerGrid.AllItems;

        foreach (var player in players)
        {
            var nearby = _grids.PlayerGrid.Query(player.X, player.Y, player.Radius);
            foreach (var other in nearby)
            {
                if (other == player) continue;
                if (string.Compare(player.Id, other.Id, StringComparison.Ordinal) >= 0) continue;
                if (AreOwnedBySame(player, other)) continue;

                var dx = player.X - other.X;
                var dy = player.Y - other.Y;
                var maxR = Math.Max(player.Radius, other.Radius);

                if (dx * dx + dy * dy < maxR * maxR)
                {
                    if (player.Mass > other.Mass * GameConfig.EatSizeRatio)
                    {
                        kills.Add((player, other));
                    }
                    else if (other.Mass > player.Mass * GameConfig.EatSizeRatio)
                    {
                        kills.Add((other, player));
                    }
                }
            }
        }

        return kills;
    }

    public List<(Player keeper, Player merged)> CheckMerges()
    {
        var merges = new List<(Player, Player)>();
        var players = _grids.PlayerGrid.AllItems;
        var currentTick = _gameState.CurrentTick;

        foreach (var player in players)
        {
            var nearby = _grids.PlayerGrid.Query(player.X, player.Y, player.Radius);
            foreach (var other in nearby)
            {
                if (other == player) continue;
                if (string.Compare(player.Id, other.Id, StringComparison.Ordinal) >= 0) continue;
                if (!AreOwnedBySame(player, other)) continue;
                if (player.MergeAfterTick > currentTick || other.MergeAfterTick > currentTick) continue;

                var dx = player.X - other.X;
                var dy = player.Y - other.Y;
                var maxR = Math.Max(player.Radius, other.Radius);

                if (dx * dx + dy * dy < maxR * maxR)
                {
                    if (player.Mass >= other.Mass)
                        merges.Add((player, other));
                    else
                        merges.Add((other, player));
                }
            }
        }

        return merges;
    }

    public List<(Projectile proj, Player target, double sizeRatio)> CheckProjectileCollisions(IEnumerable<Projectile> projectiles)
    {
        var hits = new List<(Projectile, Player, double)>();

        foreach (var proj in projectiles)
        {
            if (!proj.IsAlive) continue;

            var nearby = _grids.PlayerGrid.Query(proj.X, proj.Y, 200);
            foreach (var player in nearby)
            {
                var ownerId = player.OwnerId ?? player.Id;
                if (ownerId == proj.OwnerId) continue;

                var dx = proj.X - player.X;
                var dy = proj.Y - player.Y;

                if (dx * dx + dy * dy < player.Radius * player.Radius)
                {
                    var sizeRatio = player.Mass / Math.Max(1, proj.OwnerMassAtFire);
                    hits.Add((proj, player, sizeRatio));
                    break;
                }
            }
        }

        return hits;
    }

    private static bool AreOwnedBySame(Player a, Player b)
    {
        var ownerA = a.OwnerId ?? a.Id;
        var ownerB = b.OwnerId ?? b.Id;
        return ownerA == ownerB;
    }
}
