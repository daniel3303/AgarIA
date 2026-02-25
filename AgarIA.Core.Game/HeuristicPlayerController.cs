using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;

namespace AgarIA.Core.Game;

public class HeuristicPlayerController
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly SharedGrids _grids;
    private readonly Random _random = new();
    private const int PlayerCount = 4;
    private readonly string[] _playerIds = new string[PlayerCount];

    private readonly List<FoodItem> _foodBuffer = new();
    private readonly List<Player> _playerBuffer = new();

    public HeuristicPlayerController(
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        SharedGrids grids)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _grids = grids;
    }

    public void Tick(long currentTick)
    {
        for (int idx = 0; idx < PlayerCount; idx++)
            TickOne(idx, currentTick);
    }

    private void TickOne(int idx, long currentTick)
    {
        var player = _playerIds[idx] != null ? _playerRepository.Get(_playerIds[idx]) : null;
        if (player == null || !player.IsAlive)
        {
            SpawnIfNeeded(idx);
            return;
        }

        var nearbyFood = _grids.FoodGrid.Query(player.X, player.Y, 800, _foodBuffer);
        var nearbyPlayers = _grids.PlayerGrid.Query(player.X, player.Y, 1600, _playerBuffer);

        // Compute flee vector from threats
        double fleeX = 0, fleeY = 0;
        bool hasCloseThreat = false;

        foreach (var other in nearbyPlayers)
        {
            if (other.Id == player.Id || other.OwnerId == player.Id) continue;
            if (other.Mass <= player.Mass * GameConfig.EatSizeRatio) continue; // not a threat

            var dx = other.X - player.X;
            var dy = other.Y - player.Y;
            var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

            if (dist < 400)
            {
                hasCloseThreat = true;
                var weight = other.Mass / dist;
                fleeX -= dx / dist * weight;
                fleeY -= dy / dist * weight;
            }
        }

        if (hasCloseThreat)
        {
            var fleeDist = Math.Sqrt(fleeX * fleeX + fleeY * fleeY);
            if (fleeDist > 0)
            {
                player.TargetX = Math.Clamp(player.X + fleeX / fleeDist * 400, 0, GameConfig.MapSize);
                player.TargetY = Math.Clamp(player.Y + fleeY / fleeDist * 400, 0, GameConfig.MapSize);
            }
            return;
        }

        // Score food and prey, pick best target
        double bestScore = 0;
        double bestX = player.X, bestY = player.Y;
        bool bestIsPrey = false;
        double bestPreyMass = 0;
        double bestPreyDist = 0;

        foreach (var food in nearbyFood)
        {
            var dx = food.X - player.X;
            var dy = food.Y - player.Y;
            var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var score = GameConfig.FoodMass / dist;
            if (score > bestScore)
            {
                bestScore = score;
                bestX = food.X;
                bestY = food.Y;
                bestIsPrey = false;
            }
        }

        foreach (var other in nearbyPlayers)
        {
            if (other.Id == player.Id || other.OwnerId == player.Id) continue;
            if (player.Mass <= other.Mass * GameConfig.EatSizeRatio) continue; // can't eat

            var dx = other.X - player.X;
            var dy = other.Y - player.Y;
            var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var score = other.Mass / dist * 2;
            if (score > bestScore)
            {
                bestScore = score;
                bestX = other.X;
                bestY = other.Y;
                bestIsPrey = true;
                bestPreyMass = other.Mass;
                bestPreyDist = dist;
            }
        }

        player.TargetX = bestX;
        player.TargetY = bestY;

        // Split to catch prey
        if (bestIsPrey
            && player.Mass >= GameConfig.MinSplitMass
            && bestPreyDist < 200
            && player.Mass / 2 > bestPreyMass * GameConfig.EatSizeRatio)
        {
            SplitBot(player, currentTick);
        }
    }

    private void SpawnIfNeeded(int idx)
    {
        var id = $"heuristic_{Guid.NewGuid():N}";
        var player = new Player
        {
            Id = id,
            Username = $"Heuristic {idx + 1}",
            X = _random.NextDouble() * GameConfig.MapSize,
            Y = _random.NextDouble() * GameConfig.MapSize,
            Mass = GameConfig.StartMass,
            IsAI = false,
            IsAlive = true,
            ColorIndex = _random.Next(6)
        };
        player.TargetX = player.X;
        player.TargetY = player.Y;
        _playerRepository.Add(player);
        _playerIds[idx] = id;
    }

    private void SplitBot(Player bot, long currentTick)
    {
        var existingSplits = _playerRepository.GetByOwner(bot.Id).Count();
        var maxNewSplits = GameConfig.MaxSplitCells - existingSplits;
        if (maxNewSplits <= 0) return;
        if (bot.Mass < GameConfig.MinSplitMass) return;

        var dx = (float)(bot.TargetX - bot.X);
        var dy = (float)(bot.TargetY - bot.Y);
        var dist = MathF.Sqrt(dx * dx + dy * dy);

        float nx = 0, ny = -1;
        if (dist > 1)
        {
            nx = dx / dist;
            ny = dy / dist;
        }

        var halfMass = bot.Mass / 2;
        bot.Mass = halfMass;

        var splitCell = new Player
        {
            Id = Guid.NewGuid().ToString(),
            OwnerId = bot.Id,
            Username = bot.Username,
            ColorIndex = bot.ColorIndex,
            IsAI = false,
            X = Math.Clamp(bot.X + nx * GameConfig.SplitDistance, 0, GameConfig.MapSize),
            Y = Math.Clamp(bot.Y + ny * GameConfig.SplitDistance, 0, GameConfig.MapSize),
            Mass = halfMass,
            TargetX = bot.TargetX,
            TargetY = bot.TargetY,
            IsAlive = true,
            MergeAfterTick = currentTick + GameConfig.MergeCooldownTicks
        };

        splitCell.SpeedBoostMultiplier = GameConfig.SplitSpeed / GameConfig.BaseSpeed;
        splitCell.SpeedBoostUntil = currentTick + GameConfig.SpeedBoostDuration;

        _playerRepository.Add(splitCell);
    }

    public void OnGameReset()
    {
        Array.Clear(_playerIds);
    }
}
