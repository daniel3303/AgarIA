using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.AI;

[Service]
public class AIPlayerController
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
        ILogger<AIPlayerController> logger)
    {
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _projectileRepository = projectileRepository;
        _ga = ga;
        _gameSettings = gameSettings;
        _logger = logger;
    }

    public bool Tick(long currentTick)
    {
        _currentTick = currentTick;
        CleanupDeadBots();
        CheckpointLiveBots();
        MaintainBotCount();
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
        var bots = _playerRepository.GetAll().Where(p => p.IsAI && p.IsAlive && p.OwnerId == null).ToList();

        // Snapshot food/players once for all bots to avoid repeated enumeration
        var allFood = _foodRepository.GetAll().ToList();
        var allPlayers = _playerRepository.GetAlive().ToList();
        var allProjectiles = _projectileRepository.GetAlive().ToList();

        // Parallel phase: compute features + neural net forward pass
        var decisions = new (Player bot, double[] output, double targetX, double targetY)[bots.Count];

        Parallel.For(0, bots.Count, i =>
        {
            var bot = bots[i];
            if (!_brains.TryGetValue(bot.Id, out var brain))
            {
                decisions[i] = (bot, null, 0, 0);
                return;
            }

            var features = BuildFeatureVector(bot, allFood, allPlayers, allProjectiles);
            var output = brain.Forward(features);

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
        });

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
        var ownCells = new List<Player> { bot };
        ownCells.AddRange(allPlayers.Where(p => p.OwnerId == bot.Id));
        ownCells.Sort((a, b) => b.Mass.CompareTo(a.Mass));

        features[idx++] = ownCells[0].X / GameConfig.MapSize;
        features[idx++] = ownCells[0].Y / GameConfig.MapSize;
        if (ownCells.Count > 1)
        {
            features[idx++] = ownCells[1].X / GameConfig.MapSize;
            features[idx++] = ownCells[1].Y / GameConfig.MapSize;
        }
        else
        {
            features[idx++] = 0;
            features[idx++] = 0;
        }

        // 5 nearest food (10)
        var foodDistances = new (double dx, double dy, double distSq)[allFood.Count];
        for (int i = 0; i < allFood.Count; i++)
        {
            var dx = allFood[i].X - botX;
            var dy = allFood[i].Y - botY;
            foodDistances[i] = (dx, dy, dx * dx + dy * dy);
        }
        Array.Sort(foodDistances, (a, b) => a.distSq.CompareTo(b.distSq));

        var foodCount = Math.Min(5, foodDistances.Length);
        for (int i = 0; i < 5; i++)
        {
            if (i < foodCount)
            {
                features[idx++] = foodDistances[i].dx / GameConfig.MapSize;
                features[idx++] = foodDistances[i].dy / GameConfig.MapSize;
            }
            else
            {
                idx += 2;
            }
        }

        // 30 nearest players (90)
        var playerDistances = new List<(double dx, double dy, double distSq, double mass)>();
        foreach (var p in allPlayers)
        {
            if (p.Id == bot.Id || p.OwnerId == bot.Id) continue;
            var dx = p.X - botX;
            var dy = p.Y - botY;
            playerDistances.Add((dx, dy, dx * dx + dy * dy, p.Mass));
        }
        playerDistances.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        var playerCount = Math.Min(30, playerDistances.Count);
        for (int i = 0; i < 30; i++)
        {
            if (i < playerCount)
            {
                features[idx++] = playerDistances[i].dx / GameConfig.MapSize;
                features[idx++] = playerDistances[i].dy / GameConfig.MapSize;
                features[idx++] = playerDistances[i].mass / (bot.Mass + playerDistances[i].mass);
            }
            else
            {
                idx += 3;
            }
        }

        // Largest player position + relative mass (3)
        Player largest = null;
        foreach (var p in allPlayers)
        {
            if (p.Id == bot.Id || p.OwnerId == bot.Id) continue;
            if (largest == null || p.Mass > largest.Mass)
                largest = p;
        }
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

        // 5 nearest projectiles (10)
        var projDistances = new List<(double dx, double dy, double distSq)>();
        foreach (var p in allProjectiles)
        {
            if (p.OwnerId == bot.Id) continue;
            var dx = p.X - botX;
            var dy = p.Y - botY;
            projDistances.Add((dx, dy, dx * dx + dy * dy));
        }
        projDistances.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        var projCount = Math.Min(5, projDistances.Count);
        for (int i = 0; i < 5; i++)
        {
            if (i < projCount)
            {
                features[idx++] = projDistances[i].dx / GameConfig.MapSize;
                features[idx++] = projDistances[i].dy / GameConfig.MapSize;
            }
            else
            {
                idx += 2;
            }
        }

        return features;
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
