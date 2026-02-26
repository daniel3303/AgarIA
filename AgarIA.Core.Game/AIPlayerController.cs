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
    private readonly GameSettings _gameSettings;
    private readonly PlayerRepository _playerRepository;
    private readonly ILogger<AIPlayerController> _logger;
    public ConcurrentDictionary<string, BotPerception> BotPerceptions { get; } = new();
    private double _aiSeqMs;

    private readonly IExternalAiPlayerManager _externalAiManager;

    public AIPlayerController(
        GameSettings gameSettings,
        PlayerRepository playerRepository,
        IExternalAiPlayerManager externalAiManager,
        ILoggerFactory loggerFactory)
    {
        _gameSettings = gameSettings;
        _playerRepository = playerRepository;
        _externalAiManager = externalAiManager;
        _logger = loggerFactory.CreateLogger<AIPlayerController>();
    }

    public string GetTickBreakdown()
    {
        var result = $"ExternalAI={_aiSeqMs:F1}ms";
        _aiSeqMs = 0;
        return result;
    }

    public bool Tick(long currentTick)
    {
        // Apply external AI bot actions
        var ts = Stopwatch.GetTimestamp();
        _externalAiManager.ApplyActions();
        _externalAiManager.CleanupTimedOut();
        _aiSeqMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

        // Check if any player exceeded the reset score threshold
        var resetScore = _gameSettings.ResetAtScore;
        if (resetScore > 0)
        {
            foreach (var p in _playerRepository.GetAlive())
            {
                if (p.Score >= resetScore)
                    return true;
            }
        }

        return false;
    }

    public void SetResetAtScore(double score) => _gameSettings.ResetAtScore = score;
    public double GetResetAtScore() => _gameSettings.ResetAtScore;

    public void OnGameReset()
    {
        // Nothing to do — external bots managed via REST API
    }

    public void SaveGenomes()
    {
        // No-op — Python handles its own model persistence
    }

    public object GetFitnessStats() => new { };
}
