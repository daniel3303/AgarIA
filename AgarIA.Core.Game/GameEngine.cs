using AgarIA.Core.AI;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.Game;

public class GameEngine : IHostedService, IDisposable
{
    private readonly GameState _gameState;
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly ProjectileRepository _projectileRepository;
    private readonly CollisionManager _collisionManager;
    private readonly AIPlayerController _aiController;
    private readonly IHubContext<GameHub, IGameHub> _hubContext;
    private readonly ILogger<GameEngine> _logger;
    private Timer _gameTimer;
    private Timer _leaderboardTimer;
    private readonly Random _random = new();
    private bool _loggedFirstBroadcast;
    private readonly ManualResetEventSlim _tickComplete = new(true);
    private volatile bool _resetRequested;
    private volatile bool _maxSpeed;
    private int _tickRunning;

    public GameEngine(
        GameState gameState,
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        ProjectileRepository projectileRepository,
        CollisionManager collisionManager,
        AIPlayerController aiController,
        IHubContext<GameHub, IGameHub> hubContext,
        ILogger<GameEngine> logger)
    {
        _gameState = gameState;
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _projectileRepository = projectileRepository;
        _collisionManager = collisionManager;
        _aiController = aiController;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Game engine starting");
        SpawnInitialFood();
        _gameTimer = new Timer(Tick, null, 0, 1000 / GameConfig.TickRate);
        _leaderboardTimer = new Timer(BroadcastLeaderboard, null, 0, 1000);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Game engine stopping");
        _gameTimer?.Change(Timeout.Infinite, 0);
        _leaderboardTimer?.Change(Timeout.Infinite, 0);
        _tickComplete.Wait(TimeSpan.FromSeconds(2));
        _aiController.SaveGenomes();
        return Task.CompletedTask;
    }

    private void Tick(object state)
    {
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
            return; // previous tick still running, skip

        _tickComplete.Reset();
        try
        {
            Interlocked.Increment(ref _gameState.CurrentTick);

            if (_resetRequested)
            {
                _resetRequested = false;
                PerformReset();
            }

            MovePlayers();
            MoveProjectiles();
            HandleProjectileCollisions();
            HandleFoodCollisions();
            HandlePlayerCollisions();
            HandleMerges();
            DecayMass();
            SpawnFood();
            var shouldReset = _aiController.Tick(_gameState.CurrentTick);
            BroadcastGameState();
            if (shouldReset) _resetRequested = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in game tick");
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
            _tickComplete.Set();
        }
    }

    private void MovePlayers()
    {
        var players = _playerRepository.GetAlive().ToList();
        var currentTick = _gameState.CurrentTick;

        Parallel.ForEach(players, player =>
        {
            var dx = player.TargetX - player.X;
            var dy = player.TargetY - player.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < 1) return;

            var speed = GameConfig.BaseSpeed;

            if (currentTick < player.SpeedBoostUntil)
            {
                speed *= player.SpeedBoostMultiplier;
            }

            var nx = dx / dist;
            var ny = dy / dist;
            var move = Math.Min(dist, speed);

            player.X = Math.Clamp(player.X + nx * move, 0, GameConfig.MapSize);
            player.Y = Math.Clamp(player.Y + ny * move, 0, GameConfig.MapSize);
        });
    }

    private void HandleFoodCollisions()
    {
        var eaten = _collisionManager.CheckFoodCollisions();
        foreach (var (eater, food) in eaten)
        {
            _foodRepository.Remove(food.Id);
            eater.Mass += GameConfig.FoodMass;

            eater.SpeedBoostMultiplier = 1.0 + (GameConfig.FoodMass / eater.Mass) * GameConfig.SpeedBoostScaleFactor;
            eater.SpeedBoostUntil = _gameState.CurrentTick + GameConfig.SpeedBoostDuration;
        }
    }

    private void HandlePlayerCollisions()
    {
        var kills = _collisionManager.CheckPlayerCollisions();
        foreach (var (eater, prey) in kills)
        {
            eater.Mass += prey.Mass;
            eater.SpeedBoostUntil = _gameState.CurrentTick + GameConfig.SpeedBoostDuration;
            prey.IsAlive = false;

            // Track killer info for anti-monopoly fitness penalty
            var killerId = eater.OwnerId ?? eater.Id;
            prey.KilledById = killerId;
            var totalMass = _playerRepository.GetAlive().Sum(p => p.Mass) + prey.Mass;
            prey.KillerMassShare = totalMass > 0 ? eater.Mass / totalMass : 0;

            if (prey.OwnerId != null)
            {
                // Split cell killed — check if owner has any cells left
                var ownerAlive = _playerRepository.Get(prey.OwnerId);
                var siblingsAlive = _playerRepository.GetByOwner(prey.OwnerId).Any(c => c.Id != prey.Id);
                if (ownerAlive != null && ownerAlive.IsAlive || siblingsAlive) continue;

                _hubContext.Clients.Client(prey.OwnerId).Died(new
                {
                    killedBy = eater.Username,
                    finalScore = ownerAlive?.Score ?? 0
                });
            }
            else
            {
                // Main cell killed — also kill all split cells
                var splitCells = _playerRepository.GetByOwner(prey.Id).ToList();
                foreach (var cell in splitCells)
                {
                    cell.IsAlive = false;
                }

                _hubContext.Clients.Client(prey.Id).Died(new
                {
                    killedBy = eater.Username,
                    finalScore = prey.Score
                });
            }
        }
    }

    private void HandleMerges()
    {
        var merges = _collisionManager.CheckMerges();
        foreach (var (k, m) in merges)
        {
            if (!m.IsAlive) continue;

            // Always keep the main cell (no OwnerId) as the keeper
            var keeper = k;
            var merged = m;
            if (keeper.OwnerId != null && merged.OwnerId == null)
            {
                keeper = m;
                merged = k;
            }

            keeper.Mass += merged.Mass;
            merged.IsAlive = false;
            _playerRepository.Remove(merged.Id);
        }
    }

    private void MoveProjectiles()
    {
        foreach (var proj in _projectileRepository.GetAlive().ToList())
        {
            proj.X += proj.VX;
            proj.Y += proj.VY;

            if (proj.X < 0 || proj.X > GameConfig.MapSize || proj.Y < 0 || proj.Y > GameConfig.MapSize)
            {
                proj.IsAlive = false;
                var food = new FoodItem
                {
                    Id = _foodRepository.NextId(),
                    X = Math.Clamp(proj.X, 0, GameConfig.MapSize),
                    Y = Math.Clamp(proj.Y, 0, GameConfig.MapSize),
                    ColorIndex = new Random().Next(6)
                };
                _foodRepository.Add(food);
                _projectileRepository.Remove(proj.Id);
            }
        }
    }

    private void HandleProjectileCollisions()
    {
        var projectiles = _projectileRepository.GetAlive().ToList();
        var players = _playerRepository.GetAlive().ToList();

        foreach (var proj in projectiles)
        {
            if (!proj.IsAlive) continue;

            // Check player collisions
            foreach (var player in players)
            {
                var ownerId = player.OwnerId ?? player.Id;
                if (ownerId == proj.OwnerId) continue;

                var dx = proj.X - player.X;
                var dy = proj.Y - player.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < player.Radius)
                {
                    proj.IsAlive = false;
                    _projectileRepository.Remove(proj.Id);

                    // Damage target: always 1 mass
                    var sizeRatio = player.Mass / Math.Max(1, proj.OwnerMassAtFire);
                    if (player.Mass > GameConfig.MinMass)
                    {
                        player.Mass = Math.Max(GameConfig.MinMass, player.Mass - 1);
                    }

                    // Reward shooter: refund based on size ratio, must be at least 2x to gain extra
                    var reward = Math.Min((int)Math.Floor(sizeRatio), 20);
                    reward = Math.Max(reward, 1);
                    {
                        var shooter = _playerRepository.Get(proj.OwnerId);
                        if (shooter != null && shooter.IsAlive)
                        {
                            shooter.Mass += reward;
                        }
                    }

                    var food = new FoodItem
                    {
                        Id = _foodRepository.NextId(),
                        X = proj.X,
                        Y = proj.Y,
                        ColorIndex = new Random().Next(6)
                    };
                    _foodRepository.Add(food);
                    break;
                }
            }

        }
    }

    private void DecayMass()
    {
        var players = _playerRepository.GetAlive().ToList();
        Parallel.ForEach(players, player =>
        {
            if (player.Mass > GameConfig.MinMass)
            {
                player.Mass *= GameConfig.MassDecayRate;
                if (player.Mass < GameConfig.MinMass)
                    player.Mass = GameConfig.MinMass;
            }
        });
    }

    private void SpawnFood()
    {
        while (_foodRepository.GetCount() < GameConfig.MaxFood)
        {
            var food = new FoodItem
            {
                Id = _foodRepository.NextId(),
                X = _random.NextDouble() * GameConfig.MapSize,
                Y = _random.NextDouble() * GameConfig.MapSize,
                ColorIndex = _random.Next(6)
            };
            _foodRepository.Add(food);
        }
    }

    private void SpawnInitialFood()
    {
        for (int i = 0; i < GameConfig.MaxFood; i++)
        {
            var food = new FoodItem
            {
                Id = _foodRepository.NextId(),
                X = _random.NextDouble() * GameConfig.MapSize,
                Y = _random.NextDouble() * GameConfig.MapSize,
                ColorIndex = _random.Next(6)
            };
            _foodRepository.Add(food);
        }
    }

    private void BroadcastGameState()
    {
        var players = _playerRepository.GetAlive().Select(p => new
        {
            id = p.Id,
            x = p.X,
            y = p.Y,
            radius = p.Radius,
            username = p.Username,
            colorIndex = p.ColorIndex,
            score = p.Score,
            boosting = _gameState.CurrentTick < p.SpeedBoostUntil,
            ownerId = p.OwnerId
        }).ToList();

        var food = _foodRepository.GetAll().Select(f => new
        {
            id = f.Id,
            x = f.X,
            y = f.Y,
            colorIndex = f.ColorIndex
        }).ToList();

        var projectiles = _projectileRepository.GetAlive().Select(p => new
        {
            id = p.Id,
            x = p.X,
            y = p.Y,
            ownerId = p.OwnerId
        }).ToList();

        var humanPlayers = _playerRepository.GetAlive().Where(p => !p.IsAI && p.OwnerId == null).ToList();
        if (humanPlayers.Any() && !_loggedFirstBroadcast)
        {
            _loggedFirstBroadcast = true;
            _logger.LogInformation("Broadcasting to {Count} human players: {Ids}",
                humanPlayers.Count, string.Join(", ", humanPlayers.Select(p => p.Id)));
        }

        var spectatorUpdate = new
        {
            players,
            food,
            projectiles,
            you = (string)null,
            tick = _gameState.CurrentTick
        };

        foreach (var player in humanPlayers)
        {
            _hubContext.Clients.Client(player.Id).GameUpdate(new
            {
                players,
                food,
                projectiles,
                you = player.Id,
                tick = _gameState.CurrentTick
            });
        }

        foreach (var spectatorId in _gameState.Spectators.Keys)
        {
            _hubContext.Clients.Client(spectatorId).GameUpdate(spectatorUpdate);
        }
    }

    private void BroadcastLeaderboard(object state)
    {
        var leaderboard = _playerRepository.GetAlive()
            .Where(p => p.OwnerId == null)
            .OrderByDescending(p => p.Score)
            .Take(10)
            .Select(p => new { username = p.Username, score = p.Score })
            .ToList();

        _hubContext.Clients.All.Leaderboard(leaderboard);

        var fitnessStats = _aiController.GetFitnessStats();
        if (fitnessStats != null)
        {
            foreach (var spectatorId in _gameState.Spectators.Keys)
            {
                _hubContext.Clients.Client(spectatorId).FitnessStats(fitnessStats);
            }
        }
    }

    public void RequestReset()
    {
        _resetRequested = true;
    }

    public void SetResetAtScore(double score) => _aiController.SetResetAtScore(score);

    public void SetMaxSpeed(bool enabled)
    {
        _maxSpeed = enabled;
        var interval = enabled ? 1 : 1000 / GameConfig.TickRate;
        _gameTimer?.Change(0, interval);
        _logger.LogInformation("Max speed {State}", enabled ? "enabled" : "disabled");
    }

    private void PerformReset()
    {
        foreach (var player in _playerRepository.GetAlive().ToList())
        {
            player.IsAlive = false;
        }
        foreach (var proj in _projectileRepository.GetAlive().ToList())
        {
            proj.IsAlive = false;
            _projectileRepository.Remove(proj.Id);
        }
        foreach (var food in _foodRepository.GetAll().ToList())
        {
            _foodRepository.Remove(food.Id);
        }
        SpawnInitialFood();
        _aiController.SaveGenomes();
        _logger.LogInformation("Game reset performed");
    }

    public void Dispose()
    {
        _gameTimer?.Dispose();
        _leaderboardTimer?.Dispose();
        _tickComplete?.Dispose();
    }
}
