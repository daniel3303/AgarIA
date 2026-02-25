using System.Diagnostics;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgarIA.Core.Game;

public readonly record struct FoodDto(int id, double x, double y, int colorIndex);

public class GameEngine : IHostedService, IDisposable
{
    private readonly GameState _gameState;
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly CollisionManager _collisionManager;
    private readonly IAIController _aiController;
    private readonly HeuristicPlayerController _heuristicController;
    private readonly IHubContext<GameHub, IGameHub> _hubContext;
    private readonly ILogger<GameEngine> _logger;
    private readonly GameSettings _gameSettings;
    private readonly IGameResetHandler _resetHandler;
    private Timer _gameTimer;
    private Timer _leaderboardTimer;
    private readonly Random _random = new();
    private bool _loggedFirstBroadcast;
    private readonly ManualResetEventSlim _tickComplete = new(true);
    private volatile bool _resetRequested;
    private volatile bool _maxSpeed;
    private Thread _maxSpeedThread;
    private int _tickRunning;
    private long _tpsCount;
    private long _lastTpsMeasure = Stopwatch.GetTimestamp();
    private volatile int _currentTps;
    private readonly double[] _phaseTimesMs = new double[7];
    private readonly string[] _phaseNames = ["MovePlayers", "RebuildGrids", "Collisions", "ApplyResults", "DecayMass", "AITick", "Broadcast"];
    private double _totalTickTimeMs;
    private long _resetIntervalTicks;
    private long _lastResetTick;
    private long _roundStartTick;
    private readonly List<object> _resetScoreHistory = new();
    private HashSet<int> _previousFoodIds = new();
    private int _fullFoodSyncCounter;

    public GameEngine(
        GameState gameState,
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        CollisionManager collisionManager,
        IAIController aiController,
        HeuristicPlayerController heuristicController,
        IHubContext<GameHub, IGameHub> hubContext,
        ILogger<GameEngine> logger,
        GameSettings gameSettings,
        IGameResetHandler resetHandler = null)
    {
        _gameState = gameState;
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _collisionManager = collisionManager;
        _aiController = aiController;
        _heuristicController = heuristicController;
        _hubContext = hubContext;
        _logger = logger;
        _gameSettings = gameSettings;
        _resetHandler = resetHandler;
    }

    public int CurrentTps => _currentTps;

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
        _maxSpeed = false;
        _maxSpeedThread?.Join(TimeSpan.FromSeconds(2));
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
            _tpsCount++;
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastTpsMeasure, now).TotalSeconds >= 1.0)
            {
                _currentTps = (int)_tpsCount;
                _tpsCount = 0;
                _lastTpsMeasure = now;
            }

            if (_resetRequested)
            {
                _resetRequested = false;
                PerformReset();
            }

            var tickStart = Stopwatch.GetTimestamp();
            long ts;

            ts = Stopwatch.GetTimestamp();
            MovePlayers();
            _phaseTimesMs[0] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            ts = Stopwatch.GetTimestamp();
            _collisionManager.RebuildGrids();
            _phaseTimesMs[1] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            // Detect collisions in parallel (read-only grid queries)
            ts = Stopwatch.GetTimestamp();
            List<(Player eater, FoodItem food)> foodHits = null;
            List<(Player eater, Player prey)> playerKills = null;
            List<(Player keeper, Player merged)> merges = null;

            Parallel.Invoke(
                () => foodHits = _collisionManager.CheckFoodCollisions(),
                () => playerKills = _collisionManager.CheckPlayerCollisions(),
                () => merges = _collisionManager.CheckMerges()
            );
            _phaseTimesMs[2] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            // Apply results sequentially (mutations)
            ts = Stopwatch.GetTimestamp();
            ApplyFoodCollisions(foodHits);
            ApplyPlayerCollisions(playerKills);
            ApplyMerges(merges);
            _phaseTimesMs[3] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            ts = Stopwatch.GetTimestamp();
            DecayMass();
            SpawnFood();
            _phaseTimesMs[4] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            ts = Stopwatch.GetTimestamp();
            var scoreTriggered = _aiController.Tick(_gameState.CurrentTick);
            _heuristicController.Tick(_gameState.CurrentTick);
            _phaseTimesMs[5] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            ts = Stopwatch.GetTimestamp();
            BroadcastGameState();
            _phaseTimesMs[6] += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            _totalTickTimeMs += Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;

            if (_tpsCount == 0)
            {
                var parts = new string[7];
                double phasesSum = 0;
                for (int pi = 0; pi < 7; pi++)
                {
                    parts[pi] = $"{_phaseNames[pi]}={_phaseTimesMs[pi]:F1}ms";
                    phasesSum += _phaseTimesMs[pi];
                    _phaseTimesMs[pi] = 0;
                }
                var overhead = _totalTickTimeMs - phasesSum;
                _logger.LogInformation("Tick breakdown (last 1s): {Breakdown} | Overhead={Overhead:F1}ms | Total={Total:F1}ms | TPS={TPS} | Players={PlayerCount}", string.Join(" | ", parts), overhead, _totalTickTimeMs, _currentTps, _playerRepository.GetAlive().Count());
                _totalTickTimeMs = 0;
                _logger.LogInformation("  AI detail: {AIBreakdown}", _aiController.GetTickBreakdown());
            }

            if (_gameSettings.ResetType == ResetType.MaxScore && scoreTriggered)
                _resetRequested = true;
            if (_gameSettings.ResetType == ResetType.MaxTime && _resetIntervalTicks > 0 && _gameState.CurrentTick - _lastResetTick >= _resetIntervalTicks)
                _resetRequested = true;
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

    private void ApplyFoodCollisions(List<(Player eater, FoodItem food)> eaten)
    {
        foreach (var (eater, food) in eaten)
        {
            _foodRepository.Remove(food.Id);
            eater.Mass += GameConfig.FoodMass;
            eater.FoodEaten++;

            eater.SpeedBoostMultiplier = 1.0 + (GameConfig.FoodMass / eater.Mass) * GameConfig.SpeedBoostScaleFactor;
            eater.SpeedBoostUntil = _gameState.CurrentTick + GameConfig.SpeedBoostDuration;
        }
    }

    private void ApplyPlayerCollisions(List<(Player eater, Player prey)> kills)
    {
        if (kills.Count == 0) return;

        // Compute total mass once before the loop
        var totalMass = _playerRepository.GetAlive().Sum(p => p.Mass);

        foreach (var (eater, prey) in kills)
        {
            eater.Mass += prey.Mass;
            eater.MassEatenFromPlayers += prey.Mass;
            eater.PlayersKilled++;
            eater.SpeedBoostUntil = _gameState.CurrentTick + GameConfig.SpeedBoostDuration;
            prey.IsAlive = false;

            // Track killer info for anti-monopoly fitness penalty
            var killerId = eater.OwnerId ?? eater.Id;
            prey.KilledById = killerId;
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
                // Main cell killed — promote largest split cell if any survive
                var splitCells = _playerRepository.GetByOwner(prey.Id).ToList();

                if (splitCells.Count > 0)
                {
                    var promoted = splitCells.OrderByDescending(c => c.Mass).First();
                    _playerRepository.Remove(promoted.Id);

                    promoted.FoodEaten += prey.FoodEaten;
                    promoted.PlayersKilled += prey.PlayersKilled;
                    promoted.MassEatenFromPlayers += prey.MassEatenFromPlayers;
                    promoted.OwnerId = null;
                    promoted.Id = prey.Id;

                    _playerRepository.Add(promoted);
                }
                else
                {
                    _hubContext.Clients.Client(prey.Id).Died(new
                    {
                        killedBy = eater.Username,
                        finalScore = prey.Score
                    });
                }
            }
        }
    }

    private void ApplyMerges(List<(Player keeper, Player merged)> merges)
    {
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
        var alivePlayers = _playerRepository.GetAlive().ToList();

        // Skip entire broadcast if no humans or spectators are connected
        var hasHumans = alivePlayers.Any(p => !p.IsAI && p.OwnerId == null);
        if (!hasHumans && _gameState.Spectators.IsEmpty && _gameState.BotViewSpectators.IsEmpty)
            return;

        // Build DTOs in parallel
        object[] players = null;
        List<FoodDto> addedFood = null;
        List<int> removedFoodIds = null;
        List<FoodDto> fullFood = null;
        bool sendFullFood = false;

        var currentTick = _gameState.CurrentTick;

        // Determine if this is a full food sync tick (every 100 ticks = 5 seconds)
        _fullFoodSyncCounter++;
        if (_fullFoodSyncCounter >= 100)
        {
            _fullFoodSyncCounter = 0;
            sendFullFood = true;
        }

        Parallel.Invoke(
            () =>
            {
                players = new object[alivePlayers.Count];
                for (int i = 0; i < alivePlayers.Count; i++)
                {
                    var p = alivePlayers[i];
                    players[i] = new
                    {
                        id = p.Id,
                        x = p.X,
                        y = p.Y,
                        radius = p.Radius,
                        username = p.Username,
                        colorIndex = p.ColorIndex,
                        score = p.Score,
                        boosting = currentTick < p.SpeedBoostUntil,
                        ownerId = p.OwnerId,
                        isAI = p.IsAI
                    };
                }
            },
            () =>
            {
                var allFood = _foodRepository.GetAll().ToList();
                var currentFoodIds = new HashSet<int>(allFood.Count);

                if (sendFullFood)
                {
                    fullFood = new List<FoodDto>(allFood.Count);
                    foreach (var f in allFood)
                    {
                        fullFood.Add(new FoodDto(f.Id, f.X, f.Y, f.ColorIndex));
                        currentFoodIds.Add(f.Id);
                    }
                }
                else
                {
                    addedFood = new List<FoodDto>();
                    removedFoodIds = new List<int>();

                    foreach (var f in allFood)
                    {
                        currentFoodIds.Add(f.Id);
                        if (!_previousFoodIds.Contains(f.Id))
                            addedFood.Add(new FoodDto(f.Id, f.X, f.Y, f.ColorIndex));
                    }

                    foreach (var oldId in _previousFoodIds)
                    {
                        if (!currentFoodIds.Contains(oldId))
                            removedFoodIds.Add(oldId);
                    }
                }

                _previousFoodIds = currentFoodIds;
            }
        );

        var humanPlayers = alivePlayers.Where(p => !p.IsAI && p.OwnerId == null).ToList();
        if (humanPlayers.Any() && !_loggedFirstBroadcast)
        {
            _loggedFirstBroadcast = true;
            _logger.LogInformation("Broadcasting to {Count} human players: {Ids}",
                humanPlayers.Count, string.Join(", ", humanPlayers.Select(p => p.Id)));
        }

        // Build reset info for countdown timer
        var resetType = _gameSettings.ResetType.ToString();
        var resetAtScore = _gameSettings.ResetType == ResetType.MaxScore ? _gameSettings.ResetAtScore : 0;
        var resetTicksRemaining = _gameSettings.ResetType == ResetType.MaxTime && _resetIntervalTicks > 0
            ? Math.Max(0, _resetIntervalTicks - (_gameState.CurrentTick - _lastResetTick))
            : 0;

        // Build shared update (no "you" field — humans get YouAre separately)
        object sharedUpdate;
        if (sendFullFood)
        {
            sharedUpdate = new
            {
                players,
                food = fullFood,
                tick = _gameState.CurrentTick,
                resetType,
                resetAtScore,
                resetTicksRemaining
            };
        }
        else
        {
            sharedUpdate = new
            {
                players,
                addedFood,
                removedFoodIds,
                tick = _gameState.CurrentTick,
                resetType,
                resetAtScore,
                resetTicksRemaining
            };
        }

        // Single group broadcast for humans and spectators (same data)
        _hubContext.Clients.Group("humans").GameUpdate(sharedUpdate);
        _hubContext.Clients.Group("spectators").GameUpdate(sharedUpdate);

        // Send per-player YouAre with just their connection ID
        foreach (var player in humanPlayers)
        {
            _hubContext.Clients.Client(player.Id).YouAre(player.Id);
        }

        // Send bot view perception data to subscribed spectators
        foreach (var (spectatorId, botId) in _gameState.BotViewSpectators)
        {
            if (_aiController.BotPerceptions.TryGetValue(botId, out var perception))
            {
                _hubContext.Clients.Client(spectatorId).BotViewUpdate(new
                {
                    botId,
                    foodIds = perception.FoodIds,
                    playerIds = perception.PlayerIds,
                    largestPlayerId = perception.LargestPlayerId,
                    foodRadius = perception.FoodRadius,
                    playerRadius = perception.PlayerRadius
                });
            }
        }
    }

    private void BroadcastLeaderboard(object state)
    {
        var allAlive = _playerRepository.GetAlive().ToList();
        var leaderboard = allAlive
            .Where(p => p.OwnerId == null)
            .Select(p => new {
                username = p.Username,
                score = (int)(p.Mass + allAlive.Where(c => c.OwnerId == p.Id).Sum(c => c.Mass))
            })
            .OrderByDescending(p => p.score)
            .Take(10)
            .ToList();

        _hubContext.Clients.All.Leaderboard(leaderboard);

    }

    public void RequestReset()
    {
        _resetRequested = true;
    }

    public void SetResetAtScore(double score) => _aiController.SetResetAtScore(score);

    public void SetResetSecondsRange(int min, int max)
    {
        _gameSettings.MinResetSeconds = min;
        _gameSettings.MaxResetSeconds = max;
        var seconds = _random.Next(min, max + 1);
        _resetIntervalTicks = seconds * GameConfig.TickRate;
        _lastResetTick = _gameState.CurrentTick;
        _logger.LogInformation("Auto reset range set to {Min}-{Max}s, sampled {Seconds}s ({Ticks} ticks)", min, max, seconds, _resetIntervalTicks);
    }

    public void SetMaxSpeed(bool enabled)
    {
        _gameSettings.MaxSpeed = enabled;
        _maxSpeed = enabled;

        if (enabled)
        {
            // Stop the timer and use a dedicated spin thread instead
            _gameTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            if (_maxSpeedThread == null || !_maxSpeedThread.IsAlive)
            {
                _maxSpeedThread = new Thread(() =>
                {
                    while (_maxSpeed)
                    {
                        Tick(null);
                    }
                })
                {
                    IsBackground = true,
                    Name = "MaxSpeedTick"
                };
                _maxSpeedThread.Start();
            }
        }
        else
        {
            // Resume normal timer-based ticking
            _gameTimer?.Change(0, 1000 / GameConfig.TickRate);
        }

        _logger.LogInformation("Max speed {State}", enabled ? "enabled" : "disabled");
    }

    private void PerformReset()
    {
        var topPlayer = _playerRepository.GetAlive()
            .Where(p => p.OwnerId == null)
            .OrderByDescending(p => p.Score)
            .FirstOrDefault();

        if (topPlayer != null)
        {
            _resetScoreHistory.Add(new { username = topPlayer.Username, score = topPlayer.Score });
            if (_resetScoreHistory.Count > 10)
                _resetScoreHistory.RemoveAt(0);

            foreach (var spectatorId in _gameState.Spectators.Keys)
            {
                _hubContext.Clients.Client(spectatorId).ResetScores(_resetScoreHistory);
            }
        }

        // Capture game history before clearing players
        try
        {
            _resetHandler?.OnBeforeReset(
                _playerRepository.GetAlive().ToList(),
                _roundStartTick,
                _gameState.CurrentTick);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving game history on reset");
        }

        // Decay all genome pools on game reset
        _aiController.OnGameReset();
        _heuristicController.OnGameReset();

        // Notify all connected human players (alive or on death screen) that the game reset
        _hubContext.Clients.Group("humans").GameReset(new { message = "Game Reset" });

        foreach (var player in _playerRepository.GetAlive().ToList())
        {
            player.IsAlive = false;
        }
        foreach (var food in _foodRepository.GetAll().ToList())
        {
            _foodRepository.Remove(food.Id);
        }
        SpawnInitialFood();
        _lastResetTick = _gameState.CurrentTick;
        _roundStartTick = _gameState.CurrentTick;

        // Sample a new random duration for the next round
        var min = _gameSettings.MinResetSeconds;
        var max = _gameSettings.MaxResetSeconds;
        if (max >= min && min > 0)
        {
            var seconds = _random.Next(min, max + 1);
            _resetIntervalTicks = seconds * GameConfig.TickRate;
            _logger.LogInformation("Next round reset in {Seconds}s ({Ticks} ticks)", seconds, _resetIntervalTicks);
        }

        _aiController.RandomizePlayerCount();
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
