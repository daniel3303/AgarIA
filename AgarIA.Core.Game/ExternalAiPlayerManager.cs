using System.Collections.Concurrent;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.Game;

[Service]
public class ExternalAiPlayerManager : IExternalAiPlayerManager
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly GameState _gameState;
    private readonly PlayerVelocityTracker _velocityTracker = new();
    private readonly ILogger<ExternalAiPlayerManager> _logger;
    private readonly Random _random = new();

    private readonly ConcurrentDictionary<string, DateTime> _externalBots = new();
    private readonly ConcurrentDictionary<string, ExternalBotAction> _pendingActions = new();

    private static readonly TimeSpan BotTimeout = TimeSpan.FromSeconds(30);

    public bool TrainingEnabled { get; private set; } = true;
    public TrainingStats LastTrainingStats { get; private set; }
    public int ConnectedBotCount => _externalBots.Count;

    private static readonly string[] BotNames =
    {
        "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel",
        "India", "Juliet", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa",
        "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "Xray",
        "Yankee", "Zulu", "Archer", "Bishop", "Castle", "Dragon", "Eagle", "Falcon"
    };

    public ExternalAiPlayerManager(
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        GameState gameState,
        ILoggerFactory loggerFactory)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _gameState = gameState;
        _logger = loggerFactory.CreateLogger<ExternalAiPlayerManager>();
    }

    public List<string> RegisterBots(int count)
    {
        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var id = $"ext_{Guid.NewGuid():N}";
            var player = new Player
            {
                Id = id,
                Username = $"(AI) {BotNames[_random.Next(BotNames.Length)]}",
                X = _random.NextDouble() * GameConfig.MapSize,
                Y = _random.NextDouble() * GameConfig.MapSize,
                Mass = GameConfig.StartMass,
                IsAI = true,
                IsAlive = true,
                ColorIndex = _random.Next(6),
                SpawnProtectionUntilTick = _gameState.CurrentTick + GameConfig.SpawnProtectionTicks
            };
            _playerRepository.Add(player);
            _externalBots[id] = DateTime.UtcNow;
            ids.Add(id);
        }
        _logger.LogInformation("Registered {Count} external AI bots", count);
        return ids;
    }

    public void RemoveAllBots()
    {
        foreach (var id in _externalBots.Keys.ToList())
        {
            RemoveBot(id);
        }
        _logger.LogInformation("Removed all external AI bots");
    }

    public void SetActions(List<ExternalBotAction> actions)
    {
        foreach (var action in actions)
        {
            if (_externalBots.ContainsKey(action.PlayerId))
            {
                _pendingActions[action.PlayerId] = action;
                _externalBots[action.PlayerId] = DateTime.UtcNow;
            }
        }
    }

    public void ApplyActions()
    {
        var allPlayers = _playerRepository.GetAlive().ToList();
        _velocityTracker.Update(allPlayers);

        foreach (var (id, action) in _pendingActions)
        {
            var player = _playerRepository.Get(id);
            if (player == null || !player.IsAlive) continue;

            player.TargetX = Math.Clamp(action.TargetX, 0, GameConfig.MapSize);
            player.TargetY = Math.Clamp(action.TargetY, 0, GameConfig.MapSize);

            // Update split cells too
            foreach (var cell in _playerRepository.GetByOwner(id))
            {
                cell.TargetX = player.TargetX;
                cell.TargetY = player.TargetY;
            }

            if (action.Split && player.Mass >= GameConfig.MinSplitMass)
                SplitBot(player);
        }
    }

    public void CleanupTimedOut()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, lastAction) in _externalBots)
        {
            if (now - lastAction > BotTimeout)
            {
                _logger.LogInformation("External bot {Id} timed out, removing", id);
                RemoveBot(id);
            }

            // Also clean up dead external bots
            var player = _playerRepository.Get(id);
            if (player != null && !player.IsAlive)
            {
                // Remove split cells
                foreach (var cell in _playerRepository.GetByOwner(id).ToList())
                    _playerRepository.Remove(cell.Id);
                _playerRepository.Remove(id);
                _externalBots.TryRemove(id, out _);
                _pendingActions.TryRemove(id, out _);
            }
        }
    }

    public bool IsExternalBot(string playerId) => _externalBots.ContainsKey(playerId);

    public void SetTrainingMode(bool enabled) {
        TrainingEnabled = enabled;
        _logger.LogInformation("Training mode {Status}", enabled ? "enabled" : "disabled");
    }

    public void ReportTrainingStats(TrainingStats stats) {
        LastTrainingStats = stats;
    }

    public GameStateSnapshot GetGameState()
    {
        var players = _playerRepository.GetAlive().Select(p =>
        {
            var (vx, vy) = _velocityTracker.GetVelocity(p.Id);
            var speed = GameConfig.BaseSpeed * p.SpeedBoostMultiplier;
            return new PlayerSnapshot(p.Id, p.X, p.Y, p.Mass, vx, vy, p.IsAlive, p.OwnerId, speed);
        }).ToList();

        var food = _foodRepository.GetAll().Select(f => new FoodSnapshot(f.X, f.Y)).ToList();

        return new GameStateSnapshot(_gameState.CurrentTick, GameConfig.MapSize, players, food);
    }

    public GameConfigSnapshot GetGameConfig()
    {
        return new GameConfigSnapshot(
            GameConfig.MapSize,
            GameConfig.StartMass,
            GameConfig.EatSizeRatio,
            GameConfig.BaseSpeed,
            GameConfig.TickRate,
            GameConfig.MaxFood,
            GameConfig.FoodMass,
            GameConfig.MinSplitMass,
            GameConfig.MassDecayRate,
            GameConfig.MaxSplitCells);
    }

    private void RemoveBot(string id)
    {
        var player = _playerRepository.Get(id);
        if (player != null)
        {
            player.IsAlive = false;
            foreach (var cell in _playerRepository.GetByOwner(id).ToList())
                _playerRepository.Remove(cell.Id);
            _playerRepository.Remove(id);
        }
        _externalBots.TryRemove(id, out _);
        _pendingActions.TryRemove(id, out _);
        _velocityTracker.Remove(id);
    }

    private void SplitBot(Player bot)
    {
        var owned = _playerRepository.GetByOwner(bot.Id).ToList();
        var existingCount = owned.Count;
        var maxNewSplits = GameConfig.MaxSplitCells - existingCount;
        if (maxNewSplits <= 0) return;

        var cellsToSplit = new List<Player> { bot };
        cellsToSplit.AddRange(owned);
        cellsToSplit = cellsToSplit.Where(c => c.Mass >= GameConfig.MinSplitMass).Take(maxNewSplits).ToList();

        foreach (var cell in cellsToSplit)
        {
            var dx = (float)(cell.TargetX - cell.X);
            var dy = (float)(cell.TargetY - cell.Y);
            var dist = MathF.Sqrt(dx * dx + dy * dy);

            float nx = 0, ny = -1;
            if (dist > 1)
            {
                nx = dx / dist;
                ny = dy / dist;
            }

            var halfMass = cell.Mass / 2;
            cell.Mass = halfMass;

            var splitCell = new Player
            {
                Id = Guid.NewGuid().ToString(),
                OwnerId = bot.Id,
                Username = cell.Username,
                ColorIndex = cell.ColorIndex,
                IsAI = true,
                X = Math.Clamp(cell.X + nx * GameConfig.SplitDistance, 0, GameConfig.MapSize),
                Y = Math.Clamp(cell.Y + ny * GameConfig.SplitDistance, 0, GameConfig.MapSize),
                Mass = halfMass,
                TargetX = cell.TargetX,
                TargetY = cell.TargetY,
                IsAlive = true,
                MergeAfterTick = _gameState.CurrentTick + GameConfig.MergeCooldownTicks
            };

            splitCell.SpeedBoostMultiplier = GameConfig.SplitSpeed / GameConfig.BaseSpeed;
            splitCell.SpeedBoostUntil = _gameState.CurrentTick + GameConfig.SpeedBoostDuration;

            _playerRepository.Add(splitCell);
        }
    }
}
