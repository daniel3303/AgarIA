using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Web.Data;

namespace AgarIA.Web.Services;

public class GameHistoryService : IGameResetHandler
{
    private readonly IServiceScopeFactory _scopeFactory;

    public GameHistoryService(IServiceScopeFactory scopeFactory) {
        _scopeFactory = scopeFactory;
    }

    public void OnBeforeReset(IEnumerable<Player> players, long startTick, long endTick) {
        // Run in a separate scope since this is called from a singleton game loop
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdminDbContext>();

        var playerList = players.Where(p => p.OwnerId == null).ToList();
        if (playerList.Count == 0) return;

        var durationTicks = endTick - startTick;
        var ticksPerSecond = GameConfig.TickRate;
        var durationSeconds = durationTicks / (double)ticksPerSecond;

        var round = new GameRound {
            StartedAt = DateTime.UtcNow.AddSeconds(-durationSeconds),
            EndedAt = DateTime.UtcNow,
            DurationTicks = durationTicks,
            PlayerCount = playerList.Count,
            AiPlayerCount = playerList.Count(p => p.IsAI),
            TotalMass = playerList.Sum(p => p.Mass)
        };

        foreach (var player in playerList) {
            round.PlayerStats.Add(new PlayerGameStat {
                Username = player.Username,
                IsAI = player.IsAI,
                FinalScore = player.Score,
                TotalMass = player.Mass,
                FoodEaten = player.FoodEaten,
                PlayersKilled = player.PlayersKilled,
                PlayerMassEaten = player.MassEatenFromPlayers,
                ProjectileMassGained = player.ProjectileMassGained
            });
        }

        db.GameRounds.Add(round);
        db.SaveChanges();
    }
}
