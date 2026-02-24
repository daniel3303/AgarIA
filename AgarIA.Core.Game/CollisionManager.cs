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
        var foodList = _foodRepository.GetAll().ToList();
        var playerList = _playerRepository.GetAlive().ToList();
        Parallel.Invoke(
            () => _grids.FoodGrid.Rebuild(foodList),
            () => _grids.PlayerGrid.Rebuild(playerList)
        );
    }

    public List<(Player eater, FoodItem food)> CheckFoodCollisions()
    {
        var players = _grids.PlayerGrid.AllItems;

        var results = new List<(Player, FoodItem)>[players.Count];
        Parallel.For(0, players.Count, () => new List<FoodItem>(), (i, _, buffer) =>
        {
            var player = players[i];
            var rSq = player.Radius * player.Radius;
            var nearby = _grids.FoodGrid.Query(player.X, player.Y, player.Radius, buffer);
            List<(Player, FoodItem)> local = null;

            foreach (var item in nearby)
            {
                var dx = player.X - item.X;
                var dy = player.Y - item.Y;

                if (dx * dx + dy * dy < rSq)
                {
                    local ??= new List<(Player, FoodItem)>();
                    local.Add((player, item));
                }
            }

            results[i] = local;
            return buffer;
        }, _ => { });

        return MergeResults(results);
    }

    public List<(Player eater, Player prey)> CheckPlayerCollisions()
    {
        var players = _grids.PlayerGrid.AllItems;

        var results = new List<(Player, Player)>[players.Count];
        Parallel.For(0, players.Count, () => new List<Player>(), (i, _, buffer) =>
        {
            var player = players[i];
            var nearby = _grids.PlayerGrid.Query(player.X, player.Y, player.Radius, buffer);
            List<(Player, Player)> local = null;

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
                        local ??= new List<(Player, Player)>();
                        local.Add((player, other));
                    }
                    else if (other.Mass > player.Mass * GameConfig.EatSizeRatio)
                    {
                        local ??= new List<(Player, Player)>();
                        local.Add((other, player));
                    }
                }
            }

            results[i] = local;
            return buffer;
        }, _ => { });

        return MergeResults(results);
    }

    public List<(Player keeper, Player merged)> CheckMerges()
    {
        var players = _grids.PlayerGrid.AllItems;
        var currentTick = _gameState.CurrentTick;

        var results = new List<(Player, Player)>[players.Count];
        Parallel.For(0, players.Count, () => new List<Player>(), (i, _, buffer) =>
        {
            var player = players[i];
            var nearby = _grids.PlayerGrid.Query(player.X, player.Y, player.Radius, buffer);
            List<(Player, Player)> local = null;

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
                    local ??= new List<(Player, Player)>();
                    if (player.Mass >= other.Mass)
                        local.Add((player, other));
                    else
                        local.Add((other, player));
                }
            }

            results[i] = local;
            return buffer;
        }, _ => { });

        return MergeResults(results);
    }

    public List<(Projectile proj, Player target, float sizeRatio)> CheckProjectileCollisions(List<Projectile> projectiles)
    {
        var results = new (Projectile proj, Player target, float sizeRatio)?[projectiles.Count];
        Parallel.For(0, projectiles.Count, () => new List<Player>(), (i, _, buffer) =>
        {
            var proj = projectiles[i];
            if (!proj.IsAlive) return buffer;

            var nearby = _grids.PlayerGrid.Query(proj.X, proj.Y, 200, buffer);
            foreach (var player in nearby)
            {
                var ownerId = player.OwnerId ?? player.Id;
                if (ownerId == proj.OwnerId) continue;

                var dx = proj.X - player.X;
                var dy = proj.Y - player.Y;

                if (dx * dx + dy * dy < player.Radius * player.Radius)
                {
                    var sizeRatio = (float)(player.Mass / Math.Max(1, proj.OwnerMassAtFire));
                    results[i] = (proj, player, sizeRatio);
                    break;
                }
            }

            return buffer;
        }, _ => { });

        var hits = new List<(Projectile, Player, float)>();
        foreach (var r in results)
        {
            if (r.HasValue)
                hits.Add(r.Value);
        }
        return hits;
    }

    private static bool AreOwnedBySame(Player a, Player b)
    {
        var ownerA = a.OwnerId ?? a.Id;
        var ownerB = b.OwnerId ?? b.Id;
        return ownerA == ownerB;
    }

    private static List<T> MergeResults<T>(List<T>[] results)
    {
        int total = 0;
        foreach (var r in results)
        {
            if (r != null) total += r.Count;
        }

        var merged = new List<T>(total);
        foreach (var r in results)
        {
            if (r != null) merged.AddRange(r);
        }
        return merged;
    }
}
