using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace AgarIA.Core.Game;

public interface IGameHub
{
    Task GameUpdate(object data);
    Task Died(object data);
    Task Leaderboard(object data);
    Task FitnessStats(object data);
    Task ResetScores(object data);
    Task BotViewUpdate(object data);
    Task GameReset(object data);
    Task YouAre(string connectionId);
}

public class GameHub : Hub<IGameHub>
{
    private readonly PlayerRepository _playerRepository;
    private readonly ProjectileRepository _projectileRepository;
    private readonly GameEngine _gameEngine;
    private readonly GameState _gameState;
    private readonly Random _random = new();
    private static readonly Dictionary<string, long> _lastShotTime = new();

    public GameHub(PlayerRepository playerRepository, ProjectileRepository projectileRepository, GameEngine gameEngine, GameState gameState)
    {
        _playerRepository = playerRepository;
        _projectileRepository = projectileRepository;
        _gameEngine = gameEngine;
        _gameState = gameState;
    }

    public async Task Join(string username)
    {
        // Remove from spectators group if switching from spectate to play
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "spectators");
        _gameState.Spectators.TryRemove(Context.ConnectionId, out _);

        await Groups.AddToGroupAsync(Context.ConnectionId, "humans");

        var player = new Player
        {
            Id = Context.ConnectionId,
            Username = username,
            X = _random.NextDouble() * GameConfig.MapSize,
            Y = _random.NextDouble() * GameConfig.MapSize,
            Mass = GameConfig.StartMass,
            IsAI = false,
            IsAlive = true,
            ColorIndex = _random.Next(6)
        };

        _playerRepository.Add(player);
    }

    public void Move(double targetX, double targetY)
    {
        var player = _playerRepository.Get(Context.ConnectionId);
        if (player != null && player.IsAlive)
        {
            player.TargetX = targetX;
            player.TargetY = targetY;
        }

        foreach (var cell in _playerRepository.GetByOwner(Context.ConnectionId))
        {
            cell.TargetX = targetX;
            cell.TargetY = targetY;
        }
    }

    public void Split()
    {
        var connectionId = Context.ConnectionId;
        var player = _playerRepository.Get(connectionId);
        if (player == null || !player.IsAlive) return;

        var cellsToSplit = new List<Player> { player };
        cellsToSplit.AddRange(_playerRepository.GetByOwner(connectionId));
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
                OwnerId = connectionId,
                Username = cell.Username,
                ColorIndex = cell.ColorIndex,
                IsAI = cell.IsAI,
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

    public void Shoot(double targetX, double targetY)
    {
        var player = _playerRepository.Get(Context.ConnectionId);
        if (player == null || !player.IsAlive || player.Mass <= GameConfig.MinMass) return;

        // Rate limit: max 10 shots per second (100ms cooldown)
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastShot = _lastShotTime.GetValueOrDefault(Context.ConnectionId, 0);
        if (now - lastShot < 100) return;
        _lastShotTime[Context.ConnectionId] = now;

        player.Mass -= 1;

        var dx = targetX - player.X;
        var dy = targetY - player.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1) return;

        var nx = dx / dist;
        var ny = dy / dist;

        var projectile = new Projectile
        {
            Id = _projectileRepository.NextId(),
            X = player.X + nx * player.Radius,
            Y = player.Y + ny * player.Radius,
            VX = nx * GameConfig.ProjectileSpeed,
            VY = ny * GameConfig.ProjectileSpeed,
            OwnerId = Context.ConnectionId,
            OwnerMassAtFire = player.Mass,
            IsAlive = true
        };

        _projectileRepository.Add(projectile);
    }

    public async Task Spectate()
    {
        // Remove from humans group if switching from play to spectate
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "humans");

        await Groups.AddToGroupAsync(Context.ConnectionId, "spectators");
        _gameState.Spectators[Context.ConnectionId] = true;
    }

    public void EnableBotView(string botId)
    {
        _gameState.BotViewSpectators[Context.ConnectionId] = botId;
    }

    public void DisableBotView()
    {
        _gameState.BotViewSpectators.TryRemove(Context.ConnectionId, out _);
    }

    public void ResetGame()
    {
        _gameEngine.RequestReset();
    }

    public void SetResetAtScore(double score)
    {
        _gameEngine.SetResetAtScore(score);
    }

    public void SetResetSecondsRange(int min, int max)
    {
        _gameEngine.SetResetSecondsRange(min, max);
    }

    public void SetMaxSpeed(bool enabled)
    {
        _gameEngine.SetMaxSpeed(enabled);
    }

    public void Respawn()
    {
        var existing = _playerRepository.Get(Context.ConnectionId);
        if (existing != null)
        {
            existing.X = _random.NextDouble() * GameConfig.MapSize;
            existing.Y = _random.NextDouble() * GameConfig.MapSize;
            existing.Mass = GameConfig.StartMass;
            existing.IsAlive = true;
            existing.SpeedBoostUntil = 0;
            existing.SpeedBoostMultiplier = 1.0;
        }
    }

    public override Task OnDisconnectedAsync(Exception exception)
    {
        _gameState.Spectators.TryRemove(Context.ConnectionId, out _);
        _gameState.BotViewSpectators.TryRemove(Context.ConnectionId, out _);
        foreach (var cell in _playerRepository.GetByOwner(Context.ConnectionId).ToList())
        {
            _playerRepository.Remove(cell.Id);
        }
        _playerRepository.Remove(Context.ConnectionId);
        _lastShotTime.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
