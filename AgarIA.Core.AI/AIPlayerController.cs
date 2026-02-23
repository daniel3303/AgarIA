using System.Diagnostics;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.AI;

[Service]
public class AIPlayerController : IAIController
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly ProjectileRepository _projectileRepository;
    private readonly GeneticAlgorithm _ga;
    private readonly GameSettings _gameSettings;
    private readonly ILogger<AIPlayerController> _logger;
    private readonly Random _random = new();
    private readonly Dictionary<string, NeuralNetwork> _brains = new();
    private readonly Dictionary<string, HashSet<int>> _visitedCells = new();
    private readonly Dictionary<string, long> _lastShotTick = new();
    private readonly Dictionary<string, long> _spawnTick = new();
    private long _currentTick;
    private DateTime _lastCheckpoint = DateTime.UtcNow.AddSeconds(-15); // Offset from decay by 15s to avoid race
    private const int ShootCooldownTicks = 10;
    private const int CheckpointIntervalSeconds = 30;
    private const int ExplorationGridSize = 40; // 4000/40 = 100 cells per axis, 10000 total
    private int _currentMaxAI = new Random().Next(10, 101);
    private readonly SharedGrids _grids;
    private double _aiFeatureMs;
    private double _aiForwardMs;
    private double _aiSetupMs;
    private double _aiSeqMs;
    private double _aiCleanupMs;
    private double _aiMaintainMs;

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
        ProjectileRepository projectileRepository,
        GeneticAlgorithm ga,
        GameSettings gameSettings,
        SharedGrids grids,
        ILogger<AIPlayerController> logger)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _projectileRepository = projectileRepository;
        _ga = ga;
        _gameSettings = gameSettings;
        _grids = grids;
        _logger = logger;
    }

    public string GetTickBreakdown()
    {
        var result = $"Cleanup={_aiCleanupMs:F1}ms | Maintain={_aiMaintainMs:F1}ms | Setup={_aiSetupMs:F1}ms | Features={_aiFeatureMs:F1}ms | Forward={_aiForwardMs:F1}ms | Seq={_aiSeqMs:F1}ms";
        _aiSetupMs = _aiFeatureMs = _aiForwardMs = _aiSeqMs = _aiCleanupMs = _aiMaintainMs = 0;
        return result;
    }

    public bool Tick(long currentTick)
    {
        _currentTick = currentTick;
        var ts = Stopwatch.GetTimestamp();
        CleanupDeadBots();
        CheckpointLiveBots();
        _aiCleanupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        ts = Stopwatch.GetTimestamp();
        MaintainBotCount();
        _aiMaintainMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        UpdateBots(currentTick);
        return CheckScoreThreshold();
    }

    public void SetResetAtScore(double score) => _gameSettings.ResetAtScore = score;
    public double GetResetAtScore() => _gameSettings.ResetAtScore;

    private bool CheckScoreThreshold()
    {
        return _playerRepository.GetAll()
            .Any(p => p.IsAlive && p.OwnerId == null && p.Score >= _gameSettings.ResetAtScore);
    }

    private void MaintainBotCount()
    {
        var aiBots = _playerRepository.GetAll().Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();
        var needed = _currentMaxAI - aiBots.Count;

        for (int i = 0; i < needed; i++)
        {
            var id = $"ai_{Guid.NewGuid():N}";
            var player = new Player
            {
                Id = id,
                Username = $"[BOT] {BotNames[_random.Next(BotNames.Length)]}",
                X = _random.NextDouble() * GameConfig.MapSize,
                Y = _random.NextDouble() * GameConfig.MapSize,
                Mass = GameConfig.StartMass,
                IsAI = true,
                IsAlive = true,
                ColorIndex = _random.Next(6)
            };
            _playerRepository.Add(player);

            var brain = new NeuralNetwork();
            brain.SetGenome(_ga.GetGenome());
            _brains[id] = brain;
            _spawnTick[id] = _currentTick;
            _visitedCells[id] = new HashSet<int>();
        }
    }

    private void UpdateBots(long currentTick)
    {
        var ts = Stopwatch.GetTimestamp();

        var bots = _playerRepository.GetAll().Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();

        // Use shared grids (rebuilt by CollisionManager earlier in tick)
        var allPlayers = _grids.PlayerGrid.AllItems;
        var allProjectiles = _projectileRepository.GetAlive().ToList();

        // Pre-query nearby food per bot (single-threaded — grid query buffer not thread-safe)
        var foodGrid = _grids.FoodGrid;
        var nearbyFoodPerBot = new List<FoodItem>[bots.Count];
        for (int i = 0; i < bots.Count; i++)
        {
            var bot = bots[i];
            nearbyFoodPerBot[i] = new List<FoodItem>(foodGrid.Query(bot.X, bot.Y, 800));
        }

        _aiSetupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 1: Build feature vectors in parallel
        ts = Stopwatch.GetTimestamp();
        const int featureSize = 119;
        const int outputSize = 12;
        var batchInputs = new double[bots.Count * featureSize];
        var batchNetworks = new NeuralNetwork[bots.Count];
        var botHasBrain = new bool[bots.Count];

        Parallel.For(0, bots.Count, i =>
        {
            var bot = bots[i];
            if (!_brains.TryGetValue(bot.Id, out var brain)) return;

            var features = BuildFeatureVector(bot, nearbyFoodPerBot[i], allPlayers, allProjectiles);
            features.AsSpan().CopyTo(batchInputs.AsSpan(i * featureSize, featureSize));
            batchNetworks[i] = brain;
            botHasBrain[i] = true;
        });
        _aiFeatureMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 2: Batched forward pass
        ts = Stopwatch.GetTimestamp();
        var batchOutputs = new double[bots.Count * outputSize];
        NeuralNetwork.BatchForward(batchInputs, batchNetworks, batchOutputs, bots.Count);
        _aiForwardMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 3: Decode decisions
        var decisions = new (Player bot, double[] output, double targetX, double targetY)[bots.Count];
        for (int i = 0; i < bots.Count; i++)
        {
            if (!botHasBrain[i]) continue;
            var bot = bots[i];
            var output = new double[outputSize];
            batchOutputs.AsSpan(i * outputSize, outputSize).CopyTo(output);

            int bestDir = 0;
            double bestVal = output[0];
            for (int d = 1; d < 8; d++)
            {
                if (output[d] > bestVal)
                {
                    bestVal = output[d];
                    bestDir = d;
                }
            }

            var angle = bestDir * (2 * Math.PI / 8);
            var targetX = bot.X + Math.Cos(angle) * 200;
            var targetY = bot.Y + Math.Sin(angle) * 200;

            decisions[i] = (bot, output, Math.Clamp(targetX, 0, GameConfig.MapSize), Math.Clamp(targetY, 0, GameConfig.MapSize));
        }

        ts = Stopwatch.GetTimestamp();
        // Sequential phase: apply decisions (mutations to shared state)
        foreach (var (bot, output, targetX, targetY) in decisions)
        {
            if (output == null) continue;

            // Track exploration — record which grid cell the bot is in
            if (_visitedCells.TryGetValue(bot.Id, out var visited))
            {
                var cellsPerRow = (int)(GameConfig.MapSize / ExplorationGridSize);
                var gridX = Math.Min((int)(bot.X / ExplorationGridSize), cellsPerRow - 1);
                var gridY = Math.Min((int)(bot.Y / ExplorationGridSize), cellsPerRow - 1);
                visited.Add(gridY * cellsPerRow + gridX);
            }

            bot.TargetX = targetX;
            bot.TargetY = targetY;

            foreach (var cell in _playerRepository.GetByOwner(bot.Id))
            {
                cell.TargetX = targetX;
                cell.TargetY = targetY;
            }

            if (output[8] > 0.5 && bot.Mass >= GameConfig.MinSplitMass)
            {
                SplitBot(bot, currentTick);
            }

            var lastShot = _lastShotTick.GetValueOrDefault(bot.Id, 0);
            if (output[9] > 0.5 && bot.Mass > GameConfig.MinMass && currentTick - lastShot >= ShootCooldownTicks)
            {
                _lastShotTick[bot.Id] = currentTick;
                var shootDx = output[10];
                var shootDy = output[11];
                var shootDist = Math.Sqrt(shootDx * shootDx + shootDy * shootDy);
                if (shootDist > 0.01)
                {
                    var nx = shootDx / shootDist;
                    var ny = shootDy / shootDist;

                    bot.Mass -= 1;

                    var projectile = new Projectile
                    {
                        Id = _projectileRepository.NextId(),
                        X = bot.X + nx * bot.Radius,
                        Y = bot.Y + ny * bot.Radius,
                        VX = nx * GameConfig.ProjectileSpeed,
                        VY = ny * GameConfig.ProjectileSpeed,
                        OwnerId = bot.Id,
                        OwnerMassAtFire = bot.Mass,
                        IsAlive = true
                    };
                    _projectileRepository.Add(projectile);
                }
            }
        }

        _aiSeqMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
    }

    private void SplitBot(Player bot, long currentTick)
    {
        var cellsToSplit = new List<Player> { bot };
        cellsToSplit.AddRange(_playerRepository.GetByOwner(bot.Id));
        cellsToSplit = cellsToSplit.Where(c => c.Mass >= GameConfig.MinSplitMass).ToList();

        foreach (var cell in cellsToSplit)
        {
            var dx = cell.TargetX - cell.X;
            var dy = cell.TargetY - cell.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            double nx = 0, ny = -1;
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

            _playerRepository.Add(splitCell);
        }
    }

    private double[] BuildFeatureVector(Player bot, List<FoodItem> allFood, List<Player> allPlayers, List<Projectile> allProjectiles)
    {
        var features = new double[119];
        int idx = 0;

        var botX = bot.X;
        var botY = bot.Y;

        // Own mass (1)
        features[idx++] = Math.Clamp(bot.Mass / 100.0, 0, 1);

        // Can split (1)
        features[idx++] = bot.Mass >= GameConfig.MinSplitMass ? 1.0 : 0.0;

        // Absolute position of two largest own cells normalized 0-1 (4)
        Player secondCell = null;
        foreach (var p in allPlayers)
        {
            if (p.OwnerId == bot.Id && (secondCell == null || p.Mass > secondCell.Mass))
                secondCell = p;
        }

        features[idx++] = bot.X / GameConfig.MapSize;
        features[idx++] = bot.Y / GameConfig.MapSize;
        if (secondCell != null)
        {
            features[idx++] = secondCell.X / GameConfig.MapSize;
            features[idx++] = secondCell.Y / GameConfig.MapSize;
        }
        else
        {
            features[idx++] = 0;
            features[idx++] = 0;
        }

        // 5 nearest food (10) — partial selection instead of full sort
        const int topFoodK = 5;
        Span<(double dx, double dy, double distSq)> topFood = stackalloc (double, double, double)[topFoodK];
        int topFoodCount = 0;

        for (int i = 0; i < allFood.Count; i++)
        {
            var dx = allFood[i].X - botX;
            var dy = allFood[i].Y - botY;
            var distSq = dx * dx + dy * dy;

            if (topFoodCount < topFoodK)
            {
                topFood[topFoodCount++] = (dx, dy, distSq);
                if (topFoodCount == topFoodK)
                    SortSpan(ref topFood);
            }
            else if (distSq < topFood[topFoodK - 1].distSq)
            {
                topFood[topFoodK - 1] = (dx, dy, distSq);
                InsertSorted(ref topFood, topFoodK);
            }
        }

        if (topFoodCount < topFoodK)
            SortSpan(ref topFood, topFoodCount);

        for (int i = 0; i < topFoodK; i++)
        {
            if (i < topFoodCount)
            {
                features[idx++] = topFood[i].dx / GameConfig.MapSize;
                features[idx++] = topFood[i].dy / GameConfig.MapSize;
            }
            else
            {
                idx += 2;
            }
        }

        // 30 nearest players (90) — partial selection instead of full sort
        const int topPlayerK = 30;
        var topPlayers = new (double dx, double dy, double distSq, double mass)[topPlayerK];
        int topPlayerCount = 0;
        Player largest = null;

        foreach (var p in allPlayers)
        {
            if (p.Id == bot.Id || p.OwnerId == bot.Id) continue;

            if (largest == null || p.Mass > largest.Mass)
                largest = p;

            var dx = p.X - botX;
            var dy = p.Y - botY;
            var distSq = dx * dx + dy * dy;

            if (topPlayerCount < topPlayerK)
            {
                topPlayers[topPlayerCount++] = (dx, dy, distSq, p.Mass);
                if (topPlayerCount == topPlayerK)
                    Array.Sort(topPlayers, (a, b) => a.distSq.CompareTo(b.distSq));
            }
            else if (distSq < topPlayers[topPlayerK - 1].distSq)
            {
                topPlayers[topPlayerK - 1] = (dx, dy, distSq, p.Mass);
                InsertSortedPlayers(topPlayers, topPlayerK);
            }
        }

        if (topPlayerCount < topPlayerK)
            Array.Sort(topPlayers, 0, topPlayerCount, Comparer<(double dx, double dy, double distSq, double mass)>.Create((a, b) => a.distSq.CompareTo(b.distSq)));

        for (int i = 0; i < topPlayerK; i++)
        {
            if (i < topPlayerCount)
            {
                features[idx++] = topPlayers[i].dx / GameConfig.MapSize;
                features[idx++] = topPlayers[i].dy / GameConfig.MapSize;
                features[idx++] = topPlayers[i].mass / (bot.Mass + topPlayers[i].mass);
            }
            else
            {
                idx += 3;
            }
        }

        // Largest player position + relative mass (3)
        if (largest != null)
        {
            features[idx++] = (largest.X - botX) / GameConfig.MapSize;
            features[idx++] = (largest.Y - botY) / GameConfig.MapSize;
            features[idx++] = largest.Mass / (bot.Mass + largest.Mass);
        }
        else
        {
            idx += 3;
        }

        // 5 nearest projectiles (10) — partial selection
        const int topProjK = 5;
        Span<(double dx, double dy, double distSq)> topProj = stackalloc (double, double, double)[topProjK];
        int topProjCount = 0;

        foreach (var p in allProjectiles)
        {
            if (p.OwnerId == bot.Id) continue;
            var dx = p.X - botX;
            var dy = p.Y - botY;
            var distSq = dx * dx + dy * dy;

            if (topProjCount < topProjK)
            {
                topProj[topProjCount++] = (dx, dy, distSq);
                if (topProjCount == topProjK)
                    SortSpan(ref topProj);
            }
            else if (distSq < topProj[topProjK - 1].distSq)
            {
                topProj[topProjK - 1] = (dx, dy, distSq);
                InsertSorted(ref topProj, topProjK);
            }
        }

        if (topProjCount < topProjK)
            SortSpan(ref topProj, topProjCount);

        for (int i = 0; i < topProjK; i++)
        {
            if (i < topProjCount)
            {
                features[idx++] = topProj[i].dx / GameConfig.MapSize;
                features[idx++] = topProj[i].dy / GameConfig.MapSize;
            }
            else
            {
                idx += 2;
            }
        }

        return features;
    }

    private static void SortSpan(ref Span<(double dx, double dy, double distSq)> span, int count = -1)
    {
        var len = count < 0 ? span.Length : count;
        for (int i = 1; i < len; i++)
        {
            var key = span[i];
            int j = i - 1;
            while (j >= 0 && span[j].distSq > key.distSq)
            {
                span[j + 1] = span[j];
                j--;
            }
            span[j + 1] = key;
        }
    }

    private static void InsertSorted(ref Span<(double dx, double dy, double distSq)> span, int len)
    {
        for (int i = len - 1; i > 0; i--)
        {
            if (span[i].distSq < span[i - 1].distSq)
            {
                (span[i], span[i - 1]) = (span[i - 1], span[i]);
            }
            else break;
        }
    }

    private static void InsertSortedPlayers((double dx, double dy, double distSq, double mass)[] arr, int len)
    {
        for (int i = len - 1; i > 0; i--)
        {
            if (arr[i].distSq < arr[i - 1].distSq)
            {
                (arr[i], arr[i - 1]) = (arr[i - 1], arr[i]);
            }
            else break;
        }
    }

    private double ComputeFitness(string botId, double score, double playerMassEaten, double killerMassShare = 0)
    {
        var totalCells = (int)(GameConfig.MapSize / ExplorationGridSize) * (int)(GameConfig.MapSize / ExplorationGridSize);
        var exploredCells = _visitedCells.TryGetValue(botId, out var visited) ? visited.Count : 0;
        var explorationRatio = (double)exploredCells / totalCells;
        var monopolyPenalty = 1.0 - killerMassShare;

        // Reward aggression: player mass eaten counts double
        var adjustedScore = score + playerMassEaten;

        // Time efficiency: divide by sqrt(alive ticks) to reward faster mass gain
        var aliveTicks = _spawnTick.TryGetValue(botId, out var spawn)
            ? Math.Max(_currentTick - spawn, 1)
            : 1;
        var timeEfficiency = 1.0 / Math.Sqrt(aliveTicks);

        return adjustedScore * timeEfficiency * explorationRatio * monopolyPenalty;
    }

    public void RandomizePlayerCount()
    {
        _currentMaxAI = _random.Next(_gameSettings.MinAIPlayers, _gameSettings.MaxAIPlayers + 1);
        _logger.LogInformation("Randomized bot count to {Count}", _currentMaxAI);
    }

    public void SaveGenomes() => _ga.Save();
    public object GetFitnessStats() => _ga.GetStats();

    // Report fitness for live bots every 30s so long-surviving genomes stay relevant in the pool
    private void CheckpointLiveBots()
    {
        if ((DateTime.UtcNow - _lastCheckpoint).TotalSeconds < CheckpointIntervalSeconds)
            return;
        _lastCheckpoint = DateTime.UtcNow;

        var liveBots = _playerRepository.GetAll()
            .Where(p => p.IsAI && p.IsAlive && p.OwnerId == null)
            .ToList();

        foreach (var player in liveBots)
        {
            if (!_brains.TryGetValue(player.Id, out var brain)) continue;
            var score = (double)player.Score;
            var playerMassEaten = player.MassEatenFromPlayers;
            _ga.ReportFitness(brain.GetGenome(), ComputeFitness(player.Id, score, playerMassEaten));
        }
    }

    private void CleanupDeadBots()
    {
        var deadBots = _playerRepository.GetAll()
            .Where(p => p.IsAI && !p.IsAlive && p.OwnerId == null)
            .Select(p => p.Id)
            .ToList();

        foreach (var id in deadBots)
        {
            if (_brains.TryGetValue(id, out var brain))
            {
                var player = _playerRepository.Get(id);
                var score = player?.Score ?? 0.0;
                var playerMassEaten = player?.MassEatenFromPlayers ?? 0;
                var killerMassShare = player?.KillerMassShare ?? 0;
                _ga.ReportFitness(brain.GetGenome(), ComputeFitness(id, score, playerMassEaten, killerMassShare));
                _brains.Remove(id);
            }
            _spawnTick.Remove(id);
            _lastShotTick.Remove(id);
            _visitedCells.Remove(id);

            // Remove split cells
            foreach (var cell in _playerRepository.GetByOwner(id).ToList())
            {
                _playerRepository.Remove(cell.Id);
            }
            _playerRepository.Remove(id);
        }
    }
}
