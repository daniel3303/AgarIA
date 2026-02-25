using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;

namespace AgarIA.Core.Game;

public class HeuristicPlayerController
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly SharedGrids _grids;
    private readonly GameSettings _gameSettings;
    private readonly Random _random = new();
    private readonly List<string> _playerIds = new();

    private readonly List<FoodItem> _foodBuffer = new();
    private readonly List<Player> _playerBuffer = new();

    public HeuristicPlayerController(
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        SharedGrids grids,
        GameSettings gameSettings)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _grids = grids;
        _gameSettings = gameSettings;
    }

    public void Tick(long currentTick)
    {
        var targetCount = _gameSettings.HeuristicEnabled ? _gameSettings.HeuristicPlayerCount : 0;

        // Remove excess players
        while (_playerIds.Count > targetCount)
        {
            var last = _playerIds[^1];
            var player = _playerRepository.Get(last);
            if (player != null)
            {
                player.IsAlive = false;
                _playerRepository.Remove(last);
            }
            _playerIds.RemoveAt(_playerIds.Count - 1);
        }

        // Spawn up to target count
        while (_playerIds.Count < targetCount)
            _playerIds.Add(null);

        for (int idx = 0; idx < _playerIds.Count; idx++)
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
        bool cannibalism = _gameSettings.HeuristicCanEatEachOther;

        foreach (var other in nearbyPlayers)
        {
            if (other.Id == player.Id || other.OwnerId == player.Id) continue;

            bool isTeammate = IsHeuristic(other);

            // Flee from threats (bigger players that can eat us)
            if ((!isTeammate || cannibalism) && other.Mass > player.Mass * GameConfig.EatSizeRatio)
            {
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
            // Avoid heuristic teammates — gentle repulsion so they spread out (only when cannibalism is off)
            else if (isTeammate && !cannibalism)
            {
                var dx = other.X - player.X;
                var dy = other.Y - player.Y;
                var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                if (dist < 300)
                {
                    var weight = 50.0 / dist;
                    fleeX -= dx / dist * weight;
                    fleeY -= dy / dist * weight;
                }
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
            }
        }

        foreach (var other in nearbyPlayers)
        {
            if (other.Id == player.Id || other.OwnerId == player.Id) continue;
            if (IsHeuristic(other) && !cannibalism) continue; // don't chase teammates unless cannibalism is on
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
            }
        }

        player.TargetX = bestX;
        player.TargetY = bestY;
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

    private static bool IsHeuristic(Player p) => p.Id.StartsWith("heuristic_");

    public void OnGameReset()
    {
        _playerIds.Clear();
    }
}
