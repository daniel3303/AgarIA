using System.Collections.Concurrent;
using System.Diagnostics;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.AI;

public enum BotDifficulty { Easy, Medium }

[Service]
public class AIPlayerController : IAIController
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly ProjectileRepository _projectileRepository;
    private readonly GeneticAlgorithm _gaEasy;
    private readonly GeneticAlgorithm _gaMedium;
    private readonly GameSettings _gameSettings;
    private readonly ILogger<AIPlayerController> _logger;
    private readonly Random _random = new();
    private readonly Dictionary<string, NeuralNetwork> _brains = new();
    private readonly Dictionary<string, BotDifficulty> _botDifficulty = new();
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
    public ConcurrentDictionary<string, BotPerception> BotPerceptions { get; } = new();
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
        GameSettings gameSettings,
        SharedGrids grids,
        ILoggerFactory loggerFactory)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _projectileRepository = projectileRepository;
        _gameSettings = gameSettings;
        _grids = grids;
        _logger = loggerFactory.CreateLogger<AIPlayerController>();

        var gaLogger = loggerFactory.CreateLogger<GeneticAlgorithm>();
        _gaEasy = new GeneticAlgorithm(gaLogger, "ai_genomes_easy.json", NeuralNetwork.GenomeSizeForLayers(1));
        _gaMedium = new GeneticAlgorithm(gaLogger, "ai_genomes_medium.json", NeuralNetwork.GenomeSizeForLayers(2));
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

        // Cache bots list once — GetAll() on ConcurrentDictionary is expensive
        var allBots = _playerRepository.GetAll().ToList();

        var ts = Stopwatch.GetTimestamp();
        CleanupDeadBots(allBots);
        CheckpointLiveBots(allBots);
        _aiCleanupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        ts = Stopwatch.GetTimestamp();
        MaintainBotCount(allBots);
        _aiMaintainMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        UpdateBots(currentTick, allBots);
        return allBots.Any(p => p.IsAlive && p.OwnerId == null && p.Score >= _gameSettings.ResetAtScore);
    }

    public void SetResetAtScore(double score) => _gameSettings.ResetAtScore = score;
    public double GetResetAtScore() => _gameSettings.ResetAtScore;

    private void MaintainBotCount(List<Player> allBots)
    {
        var aiBots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();
        var needed = _currentMaxAI - aiBots.Count;

        for (int i = 0; i < needed; i++)
        {
            // Alternate 50/50 between Easy and Medium
            var difficulty = i % 2 == 0 ? BotDifficulty.Easy : BotDifficulty.Medium;
            var hiddenLayers = difficulty == BotDifficulty.Easy ? 1 : 2;
            var ga = difficulty == BotDifficulty.Easy ? _gaEasy : _gaMedium;
            var prefix = difficulty == BotDifficulty.Easy ? "(E)" : "(M)";

            var id = $"ai_{Guid.NewGuid():N}";
            var player = new Player
            {
                Id = id,
                Username = $"{prefix} {BotNames[_random.Next(BotNames.Length)]}",
                X = _random.NextDouble() * GameConfig.MapSize,
                Y = _random.NextDouble() * GameConfig.MapSize,
                Mass = GameConfig.StartMass,
                IsAI = true,
                IsAlive = true,
                ColorIndex = _random.Next(6)
            };
            _playerRepository.Add(player);

            var brain = new NeuralNetwork(hiddenLayers);
            brain.SetGenome(ga.GetGenome());
            _brains[id] = brain;
            _botDifficulty[id] = difficulty;
            _spawnTick[id] = _currentTick;
            _visitedCells[id] = new HashSet<int>();
        }
    }

    private void UpdateBots(long currentTick, List<Player> allBots)
    {
        var ts = Stopwatch.GetTimestamp();

        var bots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();

        // Use shared grids (rebuilt by CollisionManager earlier in tick)
        var allPlayers = _grids.PlayerGrid.AllItems;
        var allProjectiles = _projectileRepository.GetAlive().ToList();

        // Build owner lookup once — eliminates O(N) GetByOwner scans
        var splitCellsByOwner = new Dictionary<string, List<Player>>();
        // Also pre-compute largest split cell per owner (step 1)
        var largestSplitByOwner = new Dictionary<string, Player>();
        foreach (var p in allPlayers)
        {
            if (p.OwnerId != null)
            {
                if (!splitCellsByOwner.TryGetValue(p.OwnerId, out var list))
                    splitCellsByOwner[p.OwnerId] = list = new List<Player>();
                list.Add(p);

                if (!largestSplitByOwner.TryGetValue(p.OwnerId, out var existing) || p.Mass > existing.Mass)
                    largestSplitByOwner[p.OwnerId] = p;
            }
        }

        // Find largest player once for all bots
        Player globalLargest = null;
        foreach (var p in allPlayers)
        {
            if (globalLargest == null || p.Mass > globalLargest.Mass)
                globalLargest = p;
        }

        // Pre-query nearby food and players per bot in parallel (grid Query is now thread-safe)
        var foodGrid = _grids.FoodGrid;
        var playerGrid = _grids.PlayerGrid;
        var nearbyFoodPerBot = new List<FoodItem>[bots.Count];
        var nearbyPlayersPerBot = new List<Player>[bots.Count];

        Parallel.For(0, bots.Count, () => (
            foodBuf: new List<FoodItem>(),
            playerBuf: new List<Player>()
        ), (i, _, buffers) =>
        {
            var bot = bots[i];
            nearbyFoodPerBot[i] = new List<FoodItem>(foodGrid.Query(bot.X, bot.Y, 800, buffers.foodBuf));
            nearbyPlayersPerBot[i] = new List<Player>(playerGrid.Query(bot.X, bot.Y, 1600, buffers.playerBuf));
            return buffers;
        }, _ => { });

        _aiSetupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Populate bot perceptions for server-driven bot view
        BotPerceptions.Clear();
        for (int i = 0; i < bots.Count; i++)
        {
            var bot = bots[i];
            var nearFood = nearbyFoodPerBot[i];
            var nearPlayers = nearbyPlayersPerBot[i];

            // 10 nearest food within 800u (same logic as BuildFeatureVector)
            var foodSorted = nearFood.OrderBy(f => (f.X - bot.X) * (f.X - bot.X) + (f.Y - bot.Y) * (f.Y - bot.Y)).Take(10).Select(f => f.Id).ToList();

            // 10 nearest players within 1600u (exclude self)
            var playerSorted = nearPlayers
                .Where(p => p.Id != bot.Id && p.OwnerId != bot.Id)
                .OrderBy(p => (p.X - bot.X) * (p.X - bot.X) + (p.Y - bot.Y) * (p.Y - bot.Y))
                .Take(10).Select(p => p.Id).ToList();

            // 5 nearest projectiles (from allProjectiles, same as feature vector)
            var projSorted = allProjectiles
                .Where(p => p.OwnerId != bot.Id)
                .OrderBy(p => (p.X - bot.X) * (p.X - bot.X) + (p.Y - bot.Y) * (p.Y - bot.Y))
                .Take(5).Select(p => p.Id).ToList();

            var largestId = globalLargest != null && globalLargest.Id != bot.Id ? globalLargest.Id : null;

            BotPerceptions[bot.Id] = new BotPerception(foodSorted, playerSorted, projSorted, largestId, 800, 1600);
        }

        // Phase 1: Build feature vectors in parallel
        ts = Stopwatch.GetTimestamp();
        const int featureSize = 66;
        const int outputSize = 12;
        var batchInputs = new double[bots.Count * featureSize];
        var batchNetworks = new NeuralNetwork[bots.Count];
        var botHasBrain = new bool[bots.Count];

        Parallel.For(0, bots.Count, i =>
        {
            var bot = bots[i];
            if (!_brains.TryGetValue(bot.Id, out var brain)) return;

            largestSplitByOwner.TryGetValue(bot.Id, out var secondCell);
            var features = BuildFeatureVector(bot, nearbyFoodPerBot[i], nearbyPlayersPerBot[i], allProjectiles, secondCell, globalLargest);
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

        // Parallel phase: exploration tracking, target assignment, split-cell propagation
        // Collect split/shoot commands for serial application
        var splitCommands = new List<Player>();
        var shootCommands = new List<(Player bot, double nx, double ny)>();
        var splitLock = new object();
        var shootLock = new object();

        Parallel.For(0, decisions.Length, i =>
        {
            var (bot, output, targetX, targetY) = decisions[i];
            if (output == null) return;

            // Track exploration
            if (_visitedCells.TryGetValue(bot.Id, out var visited))
            {
                var cellsPerRow = (int)(GameConfig.MapSize / ExplorationGridSize);
                var gridX = Math.Min((int)(bot.X / ExplorationGridSize), cellsPerRow - 1);
                var gridY = Math.Min((int)(bot.Y / ExplorationGridSize), cellsPerRow - 1);
                lock (visited) visited.Add(gridY * cellsPerRow + gridX);
            }

            bot.TargetX = targetX;
            bot.TargetY = targetY;

            // Propagate target to split cells
            if (splitCellsByOwner.TryGetValue(bot.Id, out var cells))
            {
                foreach (var cell in cells)
                {
                    cell.TargetX = targetX;
                    cell.TargetY = targetY;
                }
            }

            // Collect split commands
            if (output[8] > 0.5 && bot.Mass >= GameConfig.MinSplitMass)
            {
                lock (splitLock) splitCommands.Add(bot);
            }

            // Collect shoot commands
            var lastShot = _lastShotTick.GetValueOrDefault(bot.Id, 0);
            if (output[9] > 0.5 && bot.Mass > GameConfig.MinMass && currentTick - lastShot >= ShootCooldownTicks)
            {
                var shootDx = output[10];
                var shootDy = output[11];
                var shootDist = Math.Sqrt(shootDx * shootDx + shootDy * shootDy);
                if (shootDist > 0.01)
                {
                    lock (shootLock) shootCommands.Add((bot, shootDx / shootDist, shootDy / shootDist));
                }
            }
        });

        // Serial phase: apply split and shoot commands (shared state mutations)
        foreach (var bot in splitCommands)
        {
            SplitBot(bot, currentTick, splitCellsByOwner);
        }

        foreach (var (bot, nx, ny) in shootCommands)
        {
            _lastShotTick[bot.Id] = currentTick;
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

        _aiSeqMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
    }

    private void SplitBot(Player bot, long currentTick, Dictionary<string, List<Player>> splitCellsByOwner)
    {
        var cellsToSplit = new List<Player> { bot };
        if (splitCellsByOwner.TryGetValue(bot.Id, out var owned))
            cellsToSplit.AddRange(owned);
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

    private double[] BuildFeatureVector(Player bot, List<FoodItem> allFood, List<Player> nearbyPlayers, List<Projectile> allProjectiles, Player secondCell, Player globalLargest)
    {
        var features = new double[66];
        int idx = 0;

        var botX = bot.X;
        var botY = bot.Y;
        var largestMass = globalLargest?.Mass ?? bot.Mass;

        // Own mass relative to largest (1)
        features[idx++] = largestMass > 0 ? bot.Mass / largestMass : 1.0;

        // Can split (1)
        features[idx++] = bot.Mass >= GameConfig.MinSplitMass ? 1.0 : 0.0;

        // Absolute position of two largest own cells normalized 0-1 (4)
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

        // 10 nearest food (20) — partial selection instead of full sort
        const int topFoodK = 10;
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

        // 10 nearest players (30) — partial selection from grid-queried nearby players
        const int topPlayerK = 10;
        var topPlayers = new (double dx, double dy, double distSq, double mass)[topPlayerK];
        int topPlayerCount = 0;

        foreach (var p in nearbyPlayers)
        {
            if (p.Id == bot.Id || p.OwnerId == bot.Id) continue;

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
                features[idx++] = largestMass > 0 ? topPlayers[i].mass / largestMass : 0.0;
            }
            else
            {
                idx += 3;
            }
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

    public void SaveGenomes()
    {
        _gaEasy.Save();
        _gaMedium.Save();
    }

    public object GetFitnessStats() => new
    {
        easy = _gaEasy.GetStats(),
        medium = _gaMedium.GetStats()
    };

    // Report fitness for live bots every 30s so long-surviving genomes stay relevant in the pool
    private void CheckpointLiveBots(List<Player> allBots)
    {
        if ((DateTime.UtcNow - _lastCheckpoint).TotalSeconds < CheckpointIntervalSeconds)
            return;
        _lastCheckpoint = DateTime.UtcNow;

        var liveBots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();

        foreach (var player in liveBots)
        {
            if (!_brains.TryGetValue(player.Id, out var brain)) continue;
            var ga = _botDifficulty.TryGetValue(player.Id, out var diff) && diff == BotDifficulty.Easy ? _gaEasy : _gaMedium;
            var score = (double)player.Score;
            var playerMassEaten = player.MassEatenFromPlayers;
            ga.ReportFitness(brain.GetGenome(), ComputeFitness(player.Id, score, playerMassEaten));
        }
    }

    private void CleanupDeadBots(List<Player> allBots)
    {
        var deadBots = allBots
            .Where(p => p.IsAI && !p.IsAlive && p.OwnerId == null)
            .Select(p => p.Id)
            .ToList();

        if (deadBots.Count == 0) return;

        // Build owner lookup once for all dead bot cleanup
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
            if (_brains.TryGetValue(id, out var brain))
            {
                var ga = _botDifficulty.TryGetValue(id, out var diff) && diff == BotDifficulty.Easy ? _gaEasy : _gaMedium;
                var player = _playerRepository.Get(id);
                var score = player?.Score ?? 0.0;
                var playerMassEaten = player?.MassEatenFromPlayers ?? 0;
                var killerMassShare = player?.KillerMassShare ?? 0;
                ga.ReportFitness(brain.GetGenome(), ComputeFitness(id, score, playerMassEaten, killerMassShare));
                _brains.Remove(id);
            }
            _botDifficulty.Remove(id);
            _spawnTick.Remove(id);
            _lastShotTick.Remove(id);
            _visitedCells.Remove(id);

            // Remove split cells
            if (splitCellsByOwner.TryGetValue(id, out var cells))
            {
                foreach (var cell in cells)
                    _playerRepository.Remove(cell.Id);
            }
            _playerRepository.Remove(id);
        }
    }
}
