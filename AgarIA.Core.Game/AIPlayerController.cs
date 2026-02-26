using System.Collections.Concurrent;
using System.Diagnostics;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.Game;

[Service]
public class AIPlayerController : IAIController
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly GameSettings _gameSettings;
    private readonly ILogger<AIPlayerController> _logger;
    private readonly Random _random = new();
    private readonly PlayerVelocityTracker _velocityTracker = new();
    private long _currentTick;
    private int _currentMaxAI = new Random().Next(10, 101);
    private readonly SharedGrids _grids;
    public ConcurrentDictionary<string, BotPerception> BotPerceptions { get; } = new();
    private double _aiFeatureMs;
    private double _aiForwardMs;
    private double _aiSetupMs;
    private double _aiSeqMs;
    private double _aiCleanupMs;
    private double _aiMaintainMs;

    private readonly IExternalAiPlayerManager _externalAiManager;

    private static readonly string[] BotNames =
    {
        "Nova", "Zenith", "Pulsar", "Nebula", "Quasar", "Cosmo", "Astro", "Vortex", "Photon", "Plasma",
        "Blaze", "Echo", "Frost", "Cipher", "Drift", "Ember", "Flux", "Glitch", "Havoc", "Ion",
        "Jinx", "Karma", "Luna", "Maverick", "Nyx", "Onyx", "Prism", "Quantum", "Rogue", "Sable",
        "Titan", "Umbra", "Volt", "Wraith", "Xenon", "Yonder", "Zephyr", "Apex", "Bolt", "Comet",
        "Dusk", "Edge", "Fang", "Grim", "Haze", "Inferno", "Jolt", "Kraken", "Lumen", "Mirage",
        "Nexus", "Orbit", "Phantom", "Quake", "Rift", "Specter", "Torque", "Ultra", "Venom", "Warp",
        "Xylo", "Yeti", "Zion", "Ace", "Bane", "Crux", "Dash", "Eon", "Fury", "Ghost",
        "Hex", "Iris", "Jade", "Kite", "Lynx", "Mist", "Nero", "Opal", "Pike", "Quill",
        "Raze", "Surge", "Thorn", "Unix", "Vapor", "Wisp", "Xero", "Yawn", "Zinc", "Aura",
        "Byte", "Claw", "Drone", "Etch", "Flare", "Gust", "Husk", "Ignis", "Jet", "Knox",
        "Lace", "Moxie", "Neon", "Omega", "Pyre", "Quirk", "Reed", "Shard", "Trace", "Ursa",
        "Vivid", "Wolf", "Xion", "Yield", "Zeal", "Axle", "Brisk", "Core", "Delta", "Epoch",
        "Forge", "Grain", "Helm", "Imp", "Joust", "Keel", "Latch", "Morph", "Notch", "Oxide",
        "Pulse", "Query", "Relay", "Slate", "Tempo", "Ulric", "Vista", "Wren", "Xavi", "Yoke",
        "Zest", "Arc", "Blip", "Clash", "Dune", "Evade", "Fizz", "Grip", "Halo", "Inca",
        "Jive", "Knot", "Lever", "Mako", "Nimbus", "Ochre", "Pixel", "Quartz", "Rivet", "Strafe",
        "Turbo", "Unity", "Verge", "Whirl", "Xeric", "Yarn", "Zoom", "Argon", "Basalt", "Cobalt",
        "Dynamo", "Ember", "Falcon", "Granite", "Helix", "Ivory", "Jasper", "Kelvin", "Lotus", "Meteor",
        "Nimble", "Obsidian", "Paragon", "Quest", "Raptor", "Strix", "Talon", "Umbral", "Valor", "Wyvern",
        "Xerxes", "Yakuza", "Zealot", "Alloy", "Beacon", "Catalyst", "Draco", "Enigma", "Flint", "Golem"
    };

    public AIPlayerController(
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        GameSettings gameSettings,
        SharedGrids grids,
        IExternalAiPlayerManager externalAiManager,
        ILoggerFactory loggerFactory)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _gameSettings = gameSettings;
        _grids = grids;
        _externalAiManager = externalAiManager;
        _logger = loggerFactory.CreateLogger<AIPlayerController>();
    }

    public string GetTickBreakdown()
    {
        var result = $"Cleanup={_aiCleanupMs:F1}ms | Maintain={_aiMaintainMs:F1}ms | Setup={_aiSetupMs:F1}ms | Features={_aiFeatureMs:F1}ms | Heuristic={_aiForwardMs:F1}ms | Seq={_aiSeqMs:F1}ms";
        _aiSetupMs = _aiFeatureMs = _aiForwardMs = _aiSeqMs = _aiCleanupMs = _aiMaintainMs = 0;
        return result;
    }

    public bool Tick(long currentTick)
    {
        _currentTick = currentTick;

        var allBots = _playerRepository.GetAll().ToList();

        var ts = Stopwatch.GetTimestamp();
        CleanupDeadBots(allBots);
        _aiCleanupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        ts = Stopwatch.GetTimestamp();
        MaintainBotCount(allBots);
        _aiMaintainMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        ts = Stopwatch.GetTimestamp();
        UpdateBots(currentTick, allBots);
        _aiForwardMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Apply external AI bot actions
        ts = Stopwatch.GetTimestamp();
        _externalAiManager.ApplyActions();
        _externalAiManager.CleanupTimedOut();
        _aiSeqMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        return allBots.Any(p => p.IsAlive && p.OwnerId == null && p.Score >= _gameSettings.ResetAtScore);
    }

    public void SetResetAtScore(double score) => _gameSettings.ResetAtScore = score;
    public double GetResetAtScore() => _gameSettings.ResetAtScore;

    private void MaintainBotCount(List<Player> allBots)
    {
        var aiBots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null
            && !_externalAiManager.IsExternalBot(p.Id)).ToList();
        var needed = _currentMaxAI - aiBots.Count;

        for (int i = 0; i < needed; i++)
        {
            var id = $"ai_{Guid.NewGuid():N}";
            var player = new Player
            {
                Id = id,
                Username = $"(H) {BotNames[_random.Next(BotNames.Length)]}",
                X = _random.NextDouble() * GameConfig.MapSize,
                Y = _random.NextDouble() * GameConfig.MapSize,
                Mass = GameConfig.StartMass,
                IsAI = true,
                IsAlive = true,
                ColorIndex = _random.Next(6)
            };
            _playerRepository.Add(player);
        }
    }

    private void UpdateBots(long currentTick, List<Player> allBots)
    {
        var bots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null
            && !_externalAiManager.IsExternalBot(p.Id)).ToList();

        var allPlayers = _grids.PlayerGrid.AllItems;
        _velocityTracker.Update(allPlayers);

        var splitCellsByOwner = new Dictionary<string, List<Player>>();
        foreach (var p in allPlayers)
        {
            if (p.OwnerId != null)
            {
                if (!splitCellsByOwner.TryGetValue(p.OwnerId, out var list))
                    splitCellsByOwner[p.OwnerId] = list = new List<Player>();
                list.Add(p);
            }
        }

        var foodGrid = _grids.FoodGrid;
        var playerGrid = _grids.PlayerGrid;

        var foodBuf = new List<FoodItem>();
        var playerBuf = new List<Player>();

        foreach (var bot in bots)
        {
            var nearbyFood = foodGrid.Query(bot.X, bot.Y, 800, foodBuf);
            var nearbyPlayers = playerGrid.Query(bot.X, bot.Y, 1600, playerBuf);

            var (moveX, moveY, split) = ComputeHeuristicAction(bot, nearbyFood, nearbyPlayers);

            var targetX = Math.Clamp((float)bot.X + moveX * 200f, 0f, (float)GameConfig.MapSize);
            var targetY = Math.Clamp((float)bot.Y + moveY * 200f, 0f, (float)GameConfig.MapSize);

            bot.TargetX = targetX;
            bot.TargetY = targetY;

            if (splitCellsByOwner.TryGetValue(bot.Id, out var cells))
            {
                foreach (var cell in cells)
                {
                    cell.TargetX = targetX;
                    cell.TargetY = targetY;
                }
            }

            if (split && bot.Mass >= GameConfig.MinSplitMass)
                SplitBot(bot, currentTick, splitCellsByOwner);
        }
    }

    private static (float moveX, float moveY, bool split) ComputeHeuristicAction(
        Player bot, List<FoodItem> nearbyFood, List<Player> nearbyPlayers)
    {
        double fleeX = 0, fleeY = 0;
        bool hasCloseThreat = false;

        foreach (var other in nearbyPlayers)
        {
            if (other.Id == bot.Id || other.OwnerId == bot.Id) continue;
            if (other.Mass > bot.Mass * GameConfig.EatSizeRatio)
            {
                var dx = other.X - bot.X;
                var dy = other.Y - bot.Y;
                var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                if (dist < 400)
                {
                    hasCloseThreat = true;
                    var weight = other.Mass / dist;
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
                var targetX = bot.X + fleeX / fleeDist * 400;
                var targetY = bot.Y + fleeY / fleeDist * 400;
                return (
                    Math.Clamp((float)(targetX - bot.X) / 200f, -1f, 1f),
                    Math.Clamp((float)(targetY - bot.Y) / 200f, -1f, 1f),
                    false);
            }
        }

        double bestScore = 0;
        double bestX = bot.X, bestY = bot.Y;

        foreach (var food in nearbyFood)
        {
            var dx = food.X - bot.X;
            var dy = food.Y - bot.Y;
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
            if (other.Id == bot.Id || other.OwnerId == bot.Id) continue;
            if (bot.Mass <= other.Mass * GameConfig.EatSizeRatio) continue;

            var dx = other.X - bot.X;
            var dy = other.Y - bot.Y;
            var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var score = other.Mass / dist * 2;
            if (score > bestScore)
            {
                bestScore = score;
                bestX = other.X;
                bestY = other.Y;
            }
        }

        return (
            Math.Clamp((float)(bestX - bot.X) / 200f, -1f, 1f),
            Math.Clamp((float)(bestY - bot.Y) / 200f, -1f, 1f),
            false);
    }

    private void SplitBot(Player bot, long currentTick, Dictionary<string, List<Player>> splitCellsByOwner)
    {
        var existingCount = splitCellsByOwner.TryGetValue(bot.Id, out var owned) ? owned.Count : 0;
        var maxNewSplits = GameConfig.MaxSplitCells - existingCount;
        if (maxNewSplits <= 0) return;

        var cellsToSplit = new List<Player> { bot };
        if (owned != null)
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
                MergeAfterTick = currentTick + GameConfig.MergeCooldownTicks
            };

            splitCell.SpeedBoostMultiplier = GameConfig.SplitSpeed / GameConfig.BaseSpeed;
            splitCell.SpeedBoostUntil = currentTick + GameConfig.SpeedBoostDuration;

            _playerRepository.Add(splitCell);
        }
    }

    public void RandomizePlayerCount()
    {
        _currentMaxAI = _random.Next(_gameSettings.MinAIPlayers, _gameSettings.MaxAIPlayers + 1);
        _logger.LogInformation("Randomized bot count to {Count}", _currentMaxAI);
    }

    public void OnGameReset()
    {
        // Nothing to do — bots will respawn naturally
    }

    public void SaveGenomes()
    {
        // No-op — Python handles its own model persistence
    }

    public object GetFitnessStats() => new { };

    private void CleanupDeadBots(List<Player> allBots)
    {
        var deadBots = allBots
            .Where(p => p.IsAI && !p.IsAlive && p.OwnerId == null
                && !_externalAiManager.IsExternalBot(p.Id))
            .Select(p => p.Id)
            .ToList();

        if (deadBots.Count == 0) return;

        var deadBotIds = new HashSet<string>(deadBots);
        var splitCellsByOwner = new Dictionary<string, List<Player>>();
        foreach (var p in allBots)
        {
            if (p.OwnerId != null && deadBotIds.Contains(p.OwnerId))
            {
                if (!splitCellsByOwner.TryGetValue(p.OwnerId, out var list))
                    splitCellsByOwner[p.OwnerId] = list = new List<Player>();
                list.Add(p);
            }
        }

        foreach (var id in deadBots)
        {
            _velocityTracker.Remove(id);

            if (splitCellsByOwner.TryGetValue(id, out var cells))
            {
                foreach (var cell in cells)
                    _playerRepository.Remove(cell.Id);
            }
            _playerRepository.Remove(id);
        }
    }
}
