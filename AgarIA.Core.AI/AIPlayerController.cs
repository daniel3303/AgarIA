using System.Collections.Concurrent;
using System.Diagnostics;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.AI;

public enum BotDifficulty { Easy, Medium, Hard }

[Service]
public class AIPlayerController : IAIController
{
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly ProjectileRepository _projectileRepository;
    private readonly GeneticAlgorithm _gaEasy;
    private readonly GeneticAlgorithm _gaMedium;
    private readonly GeneticAlgorithm _gaHard;
    private readonly GameSettings _gameSettings;
    private readonly ILogger<AIPlayerController> _logger;
    private readonly Random _random = new();
    private readonly Dictionary<string, NeuralNetwork> _brains = new();
    private readonly Dictionary<string, BotDifficulty> _botDifficulty = new();
    private readonly Dictionary<string, long> _lastShotTick = new();
    private readonly Dictionary<string, long> _spawnTick = new();
    private readonly PlayerVelocityTracker _velocityTracker = new();
    private long _currentTick;
    private DateTime _lastCheckpoint = DateTime.UtcNow.AddSeconds(-15); // Offset from decay by 15s to avoid race
    private const int ShootCooldownTicks = 10;
    private const int CheckpointIntervalSeconds = 30;
    private int _currentMaxAI = new Random().Next(10, 101);
    private readonly SharedGrids _grids;
    public ConcurrentDictionary<string, BotPerception> BotPerceptions { get; } = new();
    private double _aiFeatureMs;
    private double _aiForwardMs;
    private double _aiSetupMs;
    private double _aiSeqMs;
    private double _aiCleanupMs;
    private double _aiMaintainMs;
    private bool _resetRequested;

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
        _gaEasy = new GeneticAlgorithm(gaLogger, "ai_genomes_easy.json", NeuralNetwork.GenomeSizeForLayers(gameSettings.EasyHiddenLayers.ToArray()));
        _gaMedium = new GeneticAlgorithm(gaLogger, "ai_genomes_medium.json", NeuralNetwork.GenomeSizeForLayers(gameSettings.MediumHiddenLayers.ToArray()));
        _gaHard = new GeneticAlgorithm(gaLogger, "ai_genomes_hard.json", NeuralNetwork.GenomeSizeForLayers(gameSettings.HardHiddenLayers.ToArray()));
    }

    private GeneticAlgorithm GetGA(BotDifficulty diff) => diff switch
    {
        BotDifficulty.Easy => _gaEasy,
        BotDifficulty.Medium => _gaMedium,
        BotDifficulty.Hard => _gaHard,
        _ => _gaEasy
    };

    private int[] GetHiddenLayers(BotDifficulty diff) => diff switch
    {
        BotDifficulty.Easy => _gameSettings.EasyHiddenLayers.ToArray(),
        BotDifficulty.Medium => _gameSettings.MediumHiddenLayers.ToArray(),
        BotDifficulty.Hard => _gameSettings.HardHiddenLayers.ToArray(),
        _ => _gameSettings.EasyHiddenLayers.ToArray()
    };

    private static string GetPrefix(BotDifficulty diff) => diff switch
    {
        BotDifficulty.Easy => "(E)",
        BotDifficulty.Medium => "(M)",
        BotDifficulty.Hard => "(H)",
        _ => "(E)"
    };

    public void ReconfigureTier(BotDifficulty tier, List<int> newLayers)
    {
        var newSize = NeuralNetwork.GenomeSizeForLayers(newLayers.ToArray());
        var ga = GetGA(tier);
        ga.ResetPool(newSize);
        _resetRequested = true;
        _logger.LogInformation("Reconfigured {Tier} tier to hidden layers [{Layers}], genome size {Size}",
            tier, string.Join(",", newLayers), newSize);
    }

    public bool ConsumeResetRequest()
    {
        if (!_resetRequested) return false;
        _resetRequested = false;
        return true;
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

        var easyCount = aiBots.Count(b => _botDifficulty.TryGetValue(b.Id, out var d) && d == BotDifficulty.Easy);
        var mediumCount = aiBots.Count(b => _botDifficulty.TryGetValue(b.Id, out var d) && d == BotDifficulty.Medium);
        var hardCount = aiBots.Count(b => _botDifficulty.TryGetValue(b.Id, out var d) && d == BotDifficulty.Hard);

        for (int i = 0; i < needed; i++)
        {
            // Spawn whichever tier has the lowest count to maintain balanced distribution
            BotDifficulty difficulty;
            if (easyCount <= mediumCount && easyCount <= hardCount)
                difficulty = BotDifficulty.Easy;
            else if (mediumCount <= hardCount)
                difficulty = BotDifficulty.Medium;
            else
                difficulty = BotDifficulty.Hard;

            if (difficulty == BotDifficulty.Easy) easyCount++;
            else if (difficulty == BotDifficulty.Medium) mediumCount++;
            else hardCount++;

            var hiddenLayers = GetHiddenLayers(difficulty);
            var ga = GetGA(difficulty);
            var prefix = GetPrefix(difficulty);

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
        }
    }

    private void UpdateBots(long currentTick, List<Player> allBots)
    {
        var ts = Stopwatch.GetTimestamp();

        var bots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();

        // Use shared grids (rebuilt by CollisionManager earlier in tick)
        var allPlayers = _grids.PlayerGrid.AllItems;
        var allProjectiles = _projectileRepository.GetAlive().ToList();

        // Update velocity tracker before parallel phase
        _velocityTracker.Update(allPlayers);

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

        _aiSetupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 1: Combined grid query + feature build + BotPerception in single Parallel.For
        ts = Stopwatch.GetTimestamp();
        const int featureSize = 161;
        const int outputSize = 6;
        var batchInputs = new float[bots.Count * featureSize];
        var batchNetworks = new NeuralNetwork[bots.Count];
        var botHasBrain = new bool[bots.Count];
        var perceptions = new BotPerception[bots.Count];

        var foodGrid = _grids.FoodGrid;
        var playerGrid = _grids.PlayerGrid;

        BotPerceptions.Clear();

        Parallel.For(0, bots.Count, () => (
            foodBuf: new List<FoodItem>(),
            playerBuf: new List<Player>()
        ), (i, _, buffers) =>
        {
            var bot = bots[i];

            // Grid queries (formerly Setup phase)
            var nearbyFood = foodGrid.Query(bot.X, bot.Y, 800, buffers.foodBuf);
            var nearbyPlayers = playerGrid.Query(bot.X, bot.Y, 1600, buffers.playerBuf);

            if (!_brains.TryGetValue(bot.Id, out var brain))
                return buffers;

            // Build features and collect sorted IDs for BotPerception
            largestSplitByOwner.TryGetValue(bot.Id, out var secondCell);
            var features = BuildFeatureVector(
                bot, nearbyFood, nearbyPlayers, allProjectiles, secondCell, globalLargest,
                out var foodIds, out var playerIds, out var projIds);
            features.AsSpan().CopyTo(batchInputs.AsSpan(i * featureSize, featureSize));
            batchNetworks[i] = brain;
            botHasBrain[i] = true;

            var largestId = globalLargest != null && globalLargest.Id != bot.Id ? globalLargest.Id : null;
            perceptions[i] = new BotPerception(foodIds, playerIds, projIds, largestId, 800, 1600);

            return buffers;
        }, _ => { });

        // Populate ConcurrentDictionary from array (fast sequential scan)
        for (int i = 0; i < bots.Count; i++)
        {
            if (perceptions[i] != null)
                BotPerceptions[bots[i].Id] = perceptions[i];
        }

        _aiFeatureMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 2: Batched forward pass
        ts = Stopwatch.GetTimestamp();
        var batchOutputs = new float[bots.Count * outputSize];
        NeuralNetwork.BatchForward(batchInputs, batchNetworks, batchOutputs, bots.Count);
        _aiForwardMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 3: Decode decisions — continuous movement regression
        var decisions = new (Player bot, float[] output, float targetX, float targetY)[bots.Count];
        for (int i = 0; i < bots.Count; i++)
        {
            if (!botHasBrain[i]) continue;
            var bot = bots[i];
            var output = new float[outputSize];
            batchOutputs.AsSpan(i * outputSize, outputSize).CopyTo(output);

            // output[0..1] = moveX, moveY direction (tanh -> -1..1, scale to 200px offset)
            var moveX = MathF.Tanh(output[0]) * 200f;
            var moveY = MathF.Tanh(output[1]) * 200f;
            var targetX = Math.Clamp((float)bot.X + moveX, 0f, (float)GameConfig.MapSize);
            var targetY = Math.Clamp((float)bot.Y + moveY, 0f, (float)GameConfig.MapSize);

            decisions[i] = (bot, output, targetX, targetY);
        }

        ts = Stopwatch.GetTimestamp();

        // Per-bot command slots (lock-free)
        var wantsSplit = new bool[bots.Count];
        var shootDir = new (float nx, float ny)?[bots.Count];

        Parallel.For(0, decisions.Length, i =>
        {
            var (bot, output, targetX, targetY) = decisions[i];
            if (output == null) return;

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

            // Flag split
            if (output[2] > 0.5f && bot.Mass >= GameConfig.MinSplitMass)
                wantsSplit[i] = true;

            // Compute shoot direction
            var lastShot = _lastShotTick.GetValueOrDefault(bot.Id, 0);
            if (output[3] > 0.5f && bot.Mass > GameConfig.MinMass && currentTick - lastShot >= ShootCooldownTicks)
            {
                var shootDx = output[4];
                var shootDy = output[5];
                var shootDist = MathF.Sqrt(shootDx * shootDx + shootDy * shootDy);
                if (shootDist > 0.01f)
                    shootDir[i] = (shootDx / shootDist, shootDy / shootDist);
            }
        });

        // Serial phase: apply split and shoot commands (shared state mutations)
        for (int i = 0; i < bots.Count; i++)
        {
            if (wantsSplit[i])
                SplitBot(bots[i], currentTick, splitCellsByOwner);

            if (shootDir[i] is var (nx, ny))
            {
                var bot = bots[i];
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
        }

        _aiSeqMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
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

    private float[] BuildFeatureVector(
        Player bot, List<FoodItem> allFood, List<Player> nearbyPlayers, List<Projectile> allProjectiles,
        Player secondCell, Player globalLargest,
        out List<int> outFoodIds, out List<string> outPlayerIds, out List<int> outProjIds)
    {
        var features = new float[161];
        int idx = 0;

        var botX = (float)bot.X;
        var botY = (float)bot.Y;
        var largestMass = (float)(globalLargest?.Mass ?? bot.Mass);
        var mapSize = (float)GameConfig.MapSize;

        // Velocity normalization factor: bot's current speed
        var botSpeed = (float)(GameConfig.BaseSpeed * bot.SpeedBoostMultiplier);
        if (botSpeed < 0.01f) botSpeed = (float)GameConfig.BaseSpeed;

        // Own mass relative to largest (1)
        features[idx++] = largestMass > 0 ? (float)bot.Mass / largestMass : 1.0f;

        // Absolute mass indicator (1) — 1.0 when tiny, ~0 when huge
        features[idx++] = 1.0f / (float)bot.Mass;

        // Can split (1)
        features[idx++] = bot.Mass >= GameConfig.MinSplitMass ? 1.0f : 0.0f;

        // Absolute position of two largest own cells normalized 0-1 (4)
        features[idx++] = botX / mapSize;
        features[idx++] = botY / mapSize;
        if (secondCell != null)
        {
            features[idx++] = (float)secondCell.X / mapSize;
            features[idx++] = (float)secondCell.Y / mapSize;
        }
        else
        {
            features[idx++] = 0;
            features[idx++] = 0;
        }

        // 32 nearest food (64) — partial selection instead of full sort
        const int topFoodK = 32;
        var topFood = new (float dx, float dy, float distSq, int id)[topFoodK];
        int topFoodCount = 0;

        for (int i = 0; i < allFood.Count; i++)
        {
            var dx = (float)allFood[i].X - botX;
            var dy = (float)allFood[i].Y - botY;
            var distSq = dx * dx + dy * dy;

            if (topFoodCount < topFoodK)
            {
                topFood[topFoodCount++] = (dx, dy, distSq, allFood[i].Id);
                if (topFoodCount == topFoodK)
                    Array.Sort(topFood, (a, b) => a.distSq.CompareTo(b.distSq));
            }
            else if (distSq < topFood[topFoodK - 1].distSq)
            {
                topFood[topFoodK - 1] = (dx, dy, distSq, allFood[i].Id);
                InsertSortedFood(topFood, topFoodK);
            }
        }

        if (topFoodCount < topFoodK)
            Array.Sort(topFood, 0, topFoodCount, Comparer<(float dx, float dy, float distSq, int id)>.Create((a, b) => a.distSq.CompareTo(b.distSq)));

        outFoodIds = new List<int>(topFoodCount);
        for (int i = 0; i < topFoodK; i++)
        {
            if (i < topFoodCount)
            {
                features[idx++] = topFood[i].dx / mapSize;
                features[idx++] = topFood[i].dy / mapSize;
                outFoodIds.Add(topFood[i].id);
            }
            else
            {
                idx += 2;
            }
        }

        // 16 nearest players (80) — dx, dy, mass, vx, vy
        const int topPlayerK = 16;
        var topPlayers = new (float dx, float dy, float distSq, float mass, string id)[topPlayerK];
        int topPlayerCount = 0;

        foreach (var p in nearbyPlayers)
        {
            if (p.Id == bot.Id || p.OwnerId == bot.Id) continue;

            var dx = (float)p.X - botX;
            var dy = (float)p.Y - botY;
            var distSq = dx * dx + dy * dy;

            if (topPlayerCount < topPlayerK)
            {
                topPlayers[topPlayerCount++] = (dx, dy, distSq, (float)p.Mass, p.Id);
                if (topPlayerCount == topPlayerK)
                    Array.Sort(topPlayers, (a, b) => a.distSq.CompareTo(b.distSq));
            }
            else if (distSq < topPlayers[topPlayerK - 1].distSq)
            {
                topPlayers[topPlayerK - 1] = (dx, dy, distSq, (float)p.Mass, p.Id);
                InsertSortedPlayers(topPlayers, topPlayerK);
            }
        }

        if (topPlayerCount < topPlayerK)
            Array.Sort(topPlayers, 0, topPlayerCount, Comparer<(float dx, float dy, float distSq, float mass, string id)>.Create((a, b) => a.distSq.CompareTo(b.distSq)));

        outPlayerIds = new List<string>(topPlayerCount);
        for (int i = 0; i < topPlayerK; i++)
        {
            if (i < topPlayerCount)
            {
                features[idx++] = topPlayers[i].dx / mapSize;
                features[idx++] = topPlayers[i].dy / mapSize;
                features[idx++] = largestMass > 0 ? topPlayers[i].mass / largestMass : 0.0f;
                var (vx, vy) = _velocityTracker.GetVelocity(topPlayers[i].id);
                features[idx++] = (float)vx / botSpeed;
                features[idx++] = (float)vy / botSpeed;
                outPlayerIds.Add(topPlayers[i].id);
            }
            else
            {
                idx += 5;
            }
        }

        // 5 nearest projectiles (10) — partial selection
        const int topProjK = 5;
        Span<(float dx, float dy, float distSq, int id)> topProj = stackalloc (float, float, float, int)[topProjK];
        int topProjCount = 0;

        foreach (var p in allProjectiles)
        {
            if (p.OwnerId == bot.Id) continue;
            var dx = (float)p.X - botX;
            var dy = (float)p.Y - botY;
            var distSq = dx * dx + dy * dy;

            if (topProjCount < topProjK)
            {
                topProj[topProjCount++] = (dx, dy, distSq, p.Id);
                if (topProjCount == topProjK)
                    SortSpan(ref topProj);
            }
            else if (distSq < topProj[topProjK - 1].distSq)
            {
                topProj[topProjK - 1] = (dx, dy, distSq, p.Id);
                InsertSorted(ref topProj, topProjK);
            }
        }

        if (topProjCount < topProjK)
            SortSpan(ref topProj, topProjCount);

        outProjIds = new List<int>(topProjCount);
        for (int i = 0; i < topProjK; i++)
        {
            if (i < topProjCount)
            {
                features[idx++] = topProj[i].dx / mapSize;
                features[idx++] = topProj[i].dy / mapSize;
                outProjIds.Add(topProj[i].id);
            }
            else
            {
                idx += 2;
            }
        }

        return features;
    }

    private static void SortSpan(ref Span<(float dx, float dy, float distSq, int id)> span, int count = -1)
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

    private static void InsertSorted(ref Span<(float dx, float dy, float distSq, int id)> span, int len)
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

    private static void InsertSortedFood((float dx, float dy, float distSq, int id)[] arr, int len)
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

    private static void InsertSortedPlayers((float dx, float dy, float distSq, float mass, string id)[] arr, int len)
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

    private float ComputeFitness(string botId, float score, float playerMassEaten, float killerMassShare = 0)
    {
        var monopolyPenalty = 1.0f - killerMassShare;

        // Reward aggression: player mass eaten counts double
        var adjustedScore = score + playerMassEaten;

        // Time efficiency: divide by sqrt(alive ticks) to reward faster mass gain
        var aliveTicks = _spawnTick.TryGetValue(botId, out var spawn)
            ? Math.Max(_currentTick - spawn, 1)
            : 1;
        var timeEfficiency = 1.0f / MathF.Sqrt(aliveTicks);

        return adjustedScore * timeEfficiency * monopolyPenalty;
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
        _gaHard.Save();
    }

    public object GetFitnessStats() => new
    {
        easy = _gaEasy.GetStats(),
        medium = _gaMedium.GetStats(),
        hard = _gaHard.GetStats()
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
            var ga = _botDifficulty.TryGetValue(player.Id, out var diff) ? GetGA(diff) : _gaEasy;
            var score = (float)player.Score;
            var playerMassEaten = (float)player.MassEatenFromPlayers;
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
                var ga = _botDifficulty.TryGetValue(id, out var diff) ? GetGA(diff) : _gaEasy;
                var player = _playerRepository.Get(id);
                var score = (float)(player?.Score ?? 0.0);
                var playerMassEaten = (float)(player?.MassEatenFromPlayers ?? 0);
                var killerMassShare = (float)(player?.KillerMassShare ?? 0);
                ga.ReportFitness(brain.GetGenome(), ComputeFitness(id, score, playerMassEaten, killerMassShare));
                _brains.Remove(id);
            }
            _botDifficulty.Remove(id);
            _spawnTick.Remove(id);
            _lastShotTick.Remove(id);
            _velocityTracker.Remove(id);

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
