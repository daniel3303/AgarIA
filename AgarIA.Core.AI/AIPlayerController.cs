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
    private readonly GameSettings _gameSettings;
    private readonly ILogger<AIPlayerController> _logger;
    private readonly Random _random = new();
    private readonly Dictionary<string, BotDifficulty> _botDifficulty = new();
    private readonly Dictionary<string, long> _spawnTick = new();
    private readonly Dictionary<string, HashSet<int>> _visitedCells = new();
    private const int GridDivisions = 4;
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
    private bool _resetRequested;

    // PPO per-tier shared networks and trainers
    private ActorCriticNetwork _networkEasy;
    private ActorCriticNetwork _networkMedium;
    private ActorCriticNetwork _networkHard;
    private PPOTrainer _ppoEasy;
    private PPOTrainer _ppoMedium;
    private PPOTrainer _ppoHard;

    // Async double-buffered training: background tasks and cloned networks
    private Task? _trainingTaskEasy;
    private Task? _trainingTaskMedium;
    private Task? _trainingTaskHard;
    private ActorCriticNetwork? _trainCopyEasy;
    private ActorCriticNetwork? _trainCopyMedium;
    private ActorCriticNetwork? _trainCopyHard;

    // Per-tick reward tracking
    private readonly Dictionary<string, int> _lastFoodEaten = new();
    private readonly Dictionary<string, double> _lastMass = new();
    private readonly Dictionary<string, int> _lastPlayersKilled = new();
    // Per-bot RNG for action sampling
    private readonly Dictionary<string, Random> _botRng = new();

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
        ILoggerFactory loggerFactory)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _gameSettings = gameSettings;
        _grids = grids;
        _logger = loggerFactory.CreateLogger<AIPlayerController>();

        var trainerLogger = loggerFactory.CreateLogger<PPOTrainer>();
        var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Directory.GetCurrentDirectory();
        var hp = gameSettings.PPO;

        _networkEasy = new ActorCriticNetwork(gameSettings.EasyHiddenLayers.ToArray());
        _networkMedium = new ActorCriticNetwork(gameSettings.MediumHiddenLayers.ToArray());
        _networkHard = new ActorCriticNetwork(gameSettings.HardHiddenLayers.ToArray());

        _ppoEasy = new PPOTrainer(trainerLogger, Path.Combine(dataDir, "ppo_policy_easy.json"), hp, ActorCriticNetwork.ParameterCount(gameSettings.EasyHiddenLayers.ToArray()));
        _ppoMedium = new PPOTrainer(trainerLogger, Path.Combine(dataDir, "ppo_policy_medium.json"), hp, ActorCriticNetwork.ParameterCount(gameSettings.MediumHiddenLayers.ToArray()));
        _ppoHard = new PPOTrainer(trainerLogger, Path.Combine(dataDir, "ppo_policy_hard.json"), hp, ActorCriticNetwork.ParameterCount(gameSettings.HardHiddenLayers.ToArray()));

        // Try loading saved policies
        _ppoEasy.Load(_networkEasy);
        _ppoMedium.Load(_networkMedium);
        _ppoHard.Load(_networkHard);
    }

    private ActorCriticNetwork GetNetwork(BotDifficulty diff) => diff switch
    {
        BotDifficulty.Easy => _networkEasy,
        BotDifficulty.Medium => _networkMedium,
        BotDifficulty.Hard => _networkHard,
        _ => _networkEasy
    };

    private PPOTrainer GetTrainer(BotDifficulty diff) => diff switch
    {
        BotDifficulty.Easy => _ppoEasy,
        BotDifficulty.Medium => _ppoMedium,
        BotDifficulty.Hard => _ppoHard,
        _ => _ppoEasy
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

    public bool IsTierEnabled(BotDifficulty tier) => tier switch
    {
        BotDifficulty.Easy => _gameSettings.EasyEnabled,
        BotDifficulty.Medium => _gameSettings.MediumEnabled,
        BotDifficulty.Hard => _gameSettings.HardEnabled,
        _ => true
    };

    public void ApplyTierEnabled()
    {
        // Kill existing bots of disabled tiers
        foreach (var tier in new[] { BotDifficulty.Easy, BotDifficulty.Medium, BotDifficulty.Hard })
        {
            if (IsTierEnabled(tier)) continue;

            var toKill = _botDifficulty.Where(kv => kv.Value == tier).Select(kv => kv.Key).ToList();
            foreach (var id in toKill)
            {
                var player = _playerRepository.Get(id);
                if (player != null) player.IsAlive = false;
            }
        }
    }

    public void ReconfigureTier(BotDifficulty tier, List<int> newLayers)
    {
        var layers = newLayers.ToArray();
        var paramCount = ActorCriticNetwork.ParameterCount(layers);
        var network = new ActorCriticNetwork(layers);
        var hp = _gameSettings.PPO;
        var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Directory.GetCurrentDirectory();
        var savePath = tier switch
        {
            BotDifficulty.Easy => Path.Combine(dataDir, "ppo_policy_easy.json"),
            BotDifficulty.Medium => Path.Combine(dataDir, "ppo_policy_medium.json"),
            _ => Path.Combine(dataDir, "ppo_policy_hard.json")
        };

        var trainer = new PPOTrainer(_logger, savePath, hp, paramCount);
        trainer.Reset(paramCount);

        switch (tier)
        {
            case BotDifficulty.Easy: _networkEasy = network; _ppoEasy = trainer; _trainingTaskEasy = null; _trainCopyEasy = null; break;
            case BotDifficulty.Medium: _networkMedium = network; _ppoMedium = trainer; _trainingTaskMedium = null; _trainCopyMedium = null; break;
            case BotDifficulty.Hard: _networkHard = network; _ppoHard = trainer; _trainingTaskHard = null; _trainCopyHard = null; break;
        }

        _resetRequested = true;
        _logger.LogInformation("Reconfigured {Tier} tier to hidden layers [{Layers}], param count {Count}",
            tier, string.Join(",", newLayers), paramCount);
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

        var allBots = _playerRepository.GetAll().ToList();

        var ts = Stopwatch.GetTimestamp();
        CleanupDeadBots(allBots);
        _aiCleanupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        ts = Stopwatch.GetTimestamp();
        MaintainBotCount(allBots);
        _aiMaintainMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        UpdateBots(currentTick, allBots);

        // Run PPO training if any buffer is full
        RunTrainingIfReady();

        return allBots.Any(p => p.IsAlive && p.OwnerId == null && p.Score >= _gameSettings.ResetAtScore);
    }

    public void SetResetAtScore(double score) => _gameSettings.ResetAtScore = score;
    public double GetResetAtScore() => _gameSettings.ResetAtScore;

    private void MaintainBotCount(List<Player> allBots)
    {
        var aiBots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();
        var needed = _currentMaxAI - aiBots.Count;

        var easyEnabled = _gameSettings.EasyEnabled;
        var mediumEnabled = _gameSettings.MediumEnabled;
        var hardEnabled = _gameSettings.HardEnabled;

        var easyCount = easyEnabled ? aiBots.Count(b => _botDifficulty.TryGetValue(b.Id, out var d) && d == BotDifficulty.Easy) : int.MaxValue;
        var mediumCount = mediumEnabled ? aiBots.Count(b => _botDifficulty.TryGetValue(b.Id, out var d) && d == BotDifficulty.Medium) : int.MaxValue;
        var hardCount = hardEnabled ? aiBots.Count(b => _botDifficulty.TryGetValue(b.Id, out var d) && d == BotDifficulty.Hard) : int.MaxValue;

        for (int i = 0; i < needed; i++)
        {
            // Skip if all tiers disabled
            if (!easyEnabled && !mediumEnabled && !hardEnabled) break;

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

            _botDifficulty[id] = difficulty;
            _spawnTick[id] = _currentTick;
            _visitedCells[id] = new HashSet<int>();
            _lastFoodEaten[id] = 0;
            _lastMass[id] = GameConfig.StartMass;
            _lastPlayersKilled[id] = 0;
            _botRng[id] = new Random(_random.Next());
        }
    }

    private void UpdateBots(long currentTick, List<Player> allBots)
    {
        var ts = Stopwatch.GetTimestamp();

        var bots = allBots.Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();

        var allPlayers = _grids.PlayerGrid.AllItems;
        _velocityTracker.Update(allPlayers);

        var splitCellsByOwner = new Dictionary<string, List<Player>>();
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

        Player globalLargest = null;
        foreach (var p in allPlayers)
        {
            if (globalLargest == null || p.Mass > globalLargest.Mass)
                globalLargest = p;
        }

        _aiSetupMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 1: Build features (parallel)
        ts = Stopwatch.GetTimestamp();
        const int featureSize = 170;
        const int policySize = 3;
        var batchInputs = new float[bots.Count * featureSize];
        var botHasBrain = new bool[bots.Count];
        var perceptions = new BotPerception[bots.Count];
        var botFeatures = new float[bots.Count][];

        var foodGrid = _grids.FoodGrid;
        var playerGrid = _grids.PlayerGrid;
        BotPerceptions.Clear();

        Parallel.For(0, bots.Count, () => (
            foodBuf: new List<FoodItem>(),
            playerBuf: new List<Player>()
        ), (i, _, buffers) =>
        {
            var bot = bots[i];
            var nearbyFood = foodGrid.Query(bot.X, bot.Y, 800, buffers.foodBuf);
            var nearbyPlayers = playerGrid.Query(bot.X, bot.Y, 1600, buffers.playerBuf);

            if (!_botDifficulty.ContainsKey(bot.Id))
                return buffers;

            largestSplitByOwner.TryGetValue(bot.Id, out var secondCell);
            var features = BuildFeatureVector(
                bot, nearbyFood, nearbyPlayers, secondCell, globalLargest,
                out var foodIds, out var playerIds);
            features.AsSpan().CopyTo(batchInputs.AsSpan(i * featureSize, featureSize));
            botFeatures[i] = features;
            botHasBrain[i] = true;

            var largestId = globalLargest != null && globalLargest.Id != bot.Id ? globalLargest.Id : null;
            perceptions[i] = new BotPerception(foodIds, playerIds, largestId, 800, 1600);

            return buffers;
        }, _ => { });

        for (int i = 0; i < bots.Count; i++)
        {
            if (perceptions[i] != null)
                BotPerceptions[bots[i].Id] = perceptions[i];
        }

        // Track spatial exploration
        foreach (var bot in bots)
        {
            if (_visitedCells.TryGetValue(bot.Id, out var cells))
            {
                int cx = Math.Clamp((int)(bot.X / (GameConfig.MapSize / GridDivisions)), 0, GridDivisions - 1);
                int cy = Math.Clamp((int)(bot.Y / (GameConfig.MapSize / GridDivisions)), 0, GridDivisions - 1);
                cells.Add(cy * GridDivisions + cx);
            }
        }

        _aiFeatureMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 2: Group by tier, batched forward through shared networks
        ts = Stopwatch.GetTimestamp();

        var tierGroups = new Dictionary<BotDifficulty, List<int>>();
        for (int i = 0; i < bots.Count; i++)
        {
            if (!botHasBrain[i]) continue;
            var diff = _botDifficulty[bots[i].Id];
            if (!tierGroups.TryGetValue(diff, out var list))
                tierGroups[diff] = list = new List<int>();
            list.Add(i);
        }

        var policyOutputs = new float[bots.Count * policySize];
        var valueEstimates = new float[bots.Count];

        foreach (var (tier, indices) in tierGroups)
        {
            var network = GetNetwork(tier);
            int count = indices.Count;
            var tierInputs = new float[count * featureSize];
            var tierOutputs = new float[count * policySize];

            for (int j = 0; j < count; j++)
                batchInputs.AsSpan(indices[j] * featureSize, featureSize).CopyTo(tierInputs.AsSpan(j * featureSize, featureSize));

            network.BatchForwardPolicy(tierInputs, tierOutputs, count);

            // Also compute value estimates (sequential, needed for PPO)
            for (int j = 0; j < count; j++)
            {
                int botIdx = indices[j];
                tierOutputs.AsSpan(j * policySize, policySize).CopyTo(policyOutputs.AsSpan(botIdx * policySize, policySize));

                // Value estimate via full forward (reuses cached features)
                var (_, value, _, _) = network.ForwardFull(botFeatures[botIdx]);
                valueEstimates[botIdx] = value;
            }
        }

        _aiForwardMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Phase 3: Sample actions, compute rewards, store transitions
        ts = Stopwatch.GetTimestamp();
        var decisions = new (Player bot, float moveX, float moveY, bool split, float targetX, float targetY)[bots.Count];

        for (int i = 0; i < bots.Count; i++)
        {
            if (!botHasBrain[i]) continue;
            var bot = bots[i];
            var diff = _botDifficulty[bot.Id];
            var network = GetNetwork(diff);
            var policy = new float[policySize];
            policyOutputs.AsSpan(i * policySize, policySize).CopyTo(policy);

            // Sample actions with exploration noise
            var rng = _botRng.TryGetValue(bot.Id, out var r) ? r : _random;
            var (moveX, moveY, split) = PPOTrainer.SampleActions(policy, network.LogStd, rng);

            // Compute per-tick reward
            float reward = ComputeTickReward(bot, diff);

            // Compute log probability
            float logProb = PPOTrainer.ComputeLogProb(policy, network.LogStd, moveX, moveY, split);

            // Store transition
            var trainer = GetTrainer(diff);
            trainer.AddTransition(botFeatures[i], moveX, moveY, split, reward, valueEstimates[i], logProb);

            // Update tracking for next tick
            _lastFoodEaten[bot.Id] = bot.FoodEaten;
            _lastMass[bot.Id] = bot.Mass;
            _lastPlayersKilled[bot.Id] = bot.PlayersKilled;

            // Decode movement: moveX/moveY are in [-1, 1] range (already tanh'd by sampling from mu=tanh(raw))
            var targetX = Math.Clamp((float)bot.X + moveX * 200f, 0f, (float)GameConfig.MapSize);
            var targetY = Math.Clamp((float)bot.Y + moveY * 200f, 0f, (float)GameConfig.MapSize);

            decisions[i] = (bot, moveX, moveY, split, targetX, targetY);
        }

        // Apply decisions
        var wantsSplit = new bool[bots.Count];

        Parallel.For(0, decisions.Length, i =>
        {
            var (bot, _, _, split, targetX, targetY) = decisions[i];
            if (bot == null) return;

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
                wantsSplit[i] = true;
        });

        for (int i = 0; i < bots.Count; i++)
        {
            if (wantsSplit[i])
                SplitBot(bots[i], currentTick, splitCellsByOwner);
        }

        _aiSeqMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
    }

    private float ComputeTickReward(Player bot, BotDifficulty diff)
    {
        float reward = 0;

        // Food eaten this tick
        int prevFood = _lastFoodEaten.TryGetValue(bot.Id, out var pf) ? pf : 0;
        int foodDelta = bot.FoodEaten - prevFood;
        reward += foodDelta * 1.0f;

        // Mass from eating players (Easy: 0)
        if (diff != BotDifficulty.Easy)
        {
            int prevKills = _lastPlayersKilled.TryGetValue(bot.Id, out var pk) ? pk : 0;
            int killDelta = bot.PlayersKilled - prevKills;
            reward += killDelta * 2.0f;
        }

        // Survival bonus
        reward += 0.01f;

        // Exploration: new grid cell visited this tick
        if (_visitedCells.TryGetValue(bot.Id, out var cells))
        {
            int cx = Math.Clamp((int)(bot.X / (GameConfig.MapSize / GridDivisions)), 0, GridDivisions - 1);
            int cy = Math.Clamp((int)(bot.Y / (GameConfig.MapSize / GridDivisions)), 0, GridDivisions - 1);
            int cellId = cy * GridDivisions + cx;
            if (!cells.Contains(cellId))
                reward += 0.05f;
        }

        return reward;
    }

    private void RunTrainingIfReady()
    {
        // Check if previous training completed and swap weights back
        if (_trainingTaskEasy?.IsCompleted == true)
        {
            _networkEasy.SetParameters(_trainCopyEasy!.GetParameters());
            _trainingTaskEasy = null;
            _trainCopyEasy = null;
        }

        if (_trainingTaskMedium?.IsCompleted == true)
        {
            _networkMedium.SetParameters(_trainCopyMedium!.GetParameters());
            _trainingTaskMedium = null;
            _trainCopyMedium = null;
        }

        if (_trainingTaskHard?.IsCompleted == true)
        {
            _networkHard.SetParameters(_trainCopyHard!.GetParameters());
            _trainingTaskHard = null;
            _trainCopyHard = null;
        }

        // Launch new background training if buffer full, no training in-flight, and tier enabled
        if (_gameSettings.EasyEnabled && _trainingTaskEasy == null && _ppoEasy.ShouldTrain())
        {
            _trainCopyEasy = _networkEasy.Clone();
            _trainingTaskEasy = Task.Run(() => _ppoEasy.Train(_trainCopyEasy));
        }

        if (_gameSettings.MediumEnabled && _trainingTaskMedium == null && _ppoMedium.ShouldTrain())
        {
            _trainCopyMedium = _networkMedium.Clone();
            _trainingTaskMedium = Task.Run(() => _ppoMedium.Train(_trainCopyMedium));
        }

        if (_gameSettings.HardEnabled && _trainingTaskHard == null && _ppoHard.ShouldTrain())
        {
            _trainCopyHard = _networkHard.Clone();
            _trainingTaskHard = Task.Run(() => _ppoHard.Train(_trainCopyHard));
        }
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
        Player bot, List<FoodItem> allFood, List<Player> nearbyPlayers,
        Player secondCell, Player globalLargest,
        out List<int> outFoodIds, out List<string> outPlayerIds)
    {
        var features = new float[170];
        int idx = 0;

        var botX = (float)bot.X;
        var botY = (float)bot.Y;
        var largestMass = (float)(globalLargest?.Mass ?? bot.Mass);
        var mapSize = (float)GameConfig.MapSize;

        var botSpeed = (float)(GameConfig.BaseSpeed * bot.SpeedBoostMultiplier);
        if (botSpeed < 0.01f) botSpeed = (float)GameConfig.BaseSpeed;

        // Self state (7 features: mass ratio, inv mass, can split, pos x/y, own vx/vy, speed boost)
        features[idx++] = largestMass > 0 ? (float)bot.Mass / largestMass : 1.0f;
        features[idx++] = 1.0f / (float)bot.Mass;
        features[idx++] = bot.Mass >= GameConfig.MinSplitMass ? 1.0f : 0.0f;

        features[idx++] = botX / mapSize;
        features[idx++] = botY / mapSize;

        // Bot's own velocity
        var (botVx, botVy) = _velocityTracker.GetVelocity(bot.Id);
        features[idx++] = (float)botVx / botSpeed;
        features[idx++] = (float)botVy / botSpeed;

        // Speed boost active flag
        features[idx++] = bot.SpeedBoostMultiplier > 1.0 ? 1.0f : 0.0f;
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
        var eatThreshold = (float)(bot.Mass / GameConfig.EatSizeRatio);
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
                // Edibility: +1 = can eat, -1 = danger, 0 = neither
                features[idx++] = (float)bot.Mass > topPlayers[i].mass * (float)GameConfig.EatSizeRatio ? 1.0f
                    : topPlayers[i].mass > eatThreshold ? -1.0f : 0.0f;
                outPlayerIds.Add(topPlayers[i].id);
            }
            else
            {
                idx += 6;
            }
        }

        return features;
    }

    private static void InsertSortedFood((float dx, float dy, float distSq, int id)[] arr, int len)
    {
        for (int i = len - 1; i > 0; i--)
        {
            if (arr[i].distSq < arr[i - 1].distSq)
                (arr[i], arr[i - 1]) = (arr[i - 1], arr[i]);
            else break;
        }
    }

    private static void InsertSortedPlayers((float dx, float dy, float distSq, float mass, string id)[] arr, int len)
    {
        for (int i = len - 1; i > 0; i--)
        {
            if (arr[i].distSq < arr[i - 1].distSq)
                (arr[i], arr[i - 1]) = (arr[i - 1], arr[i]);
            else break;
        }
    }

    public void RandomizePlayerCount()
    {
        _currentMaxAI = _random.Next(_gameSettings.MinAIPlayers, _gameSettings.MaxAIPlayers + 1);
        _logger.LogInformation("Randomized bot count to {Count}", _currentMaxAI);
    }

    public void OnGameReset()
    {
        // PPO doesn't need pool decay — training continues across resets
    }

    public void SaveGenomes()
    {
        // Wait for any in-flight training to complete before saving
        var tasks = new[] { _trainingTaskEasy, _trainingTaskMedium, _trainingTaskHard }
            .Where(t => t != null).ToArray();
        if (tasks.Length > 0)
            Task.WaitAll(tasks!);

        // Swap trained weights back before saving
        if (_trainCopyEasy != null)
        {
            _networkEasy.SetParameters(_trainCopyEasy.GetParameters());
            _trainingTaskEasy = null;
            _trainCopyEasy = null;
        }

        if (_trainCopyMedium != null)
        {
            _networkMedium.SetParameters(_trainCopyMedium.GetParameters());
            _trainingTaskMedium = null;
            _trainCopyMedium = null;
        }

        if (_trainCopyHard != null)
        {
            _networkHard.SetParameters(_trainCopyHard.GetParameters());
            _trainingTaskHard = null;
            _trainCopyHard = null;
        }

        _ppoEasy.Save(_networkEasy);
        _ppoMedium.Save(_networkMedium);
        _ppoHard.Save(_networkHard);
    }

    public object GetFitnessStats() => new
    {
        easy = _ppoEasy.GetStats(),
        medium = _ppoMedium.GetStats(),
        hard = _ppoHard.GetStats()
    };

    private void CleanupDeadBots(List<Player> allBots)
    {
        var deadBots = allBots
            .Where(p => p.IsAI && !p.IsAlive && p.OwnerId == null)
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
            var player = _playerRepository.Get(id);

            // Store terminal transition with death penalty
            if (_botDifficulty.TryGetValue(id, out var diff))
            {
                var trainer = GetTrainer(diff);
                // Use a zero-state for terminal (the actual state doesn't matter much)
                var terminalState = new float[170];
                trainer.AddTerminal(terminalState, -5.0f, 0f, 0f);
            }

            _botDifficulty.Remove(id);
            _spawnTick.Remove(id);
            _visitedCells.Remove(id);
            _lastFoodEaten.Remove(id);
            _lastMass.Remove(id);
            _lastPlayersKilled.Remove(id);
            _botRng.Remove(id);
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
