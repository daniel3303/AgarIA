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
    private readonly ILogger<AIPlayerController> _logger;
    public ConcurrentDictionary<string, BotPerception> BotPerceptions { get; } = new();
    private double _aiSeqMs;

    private readonly IExternalAiPlayerManager _externalAiManager;

    public AIPlayerController(
        GameSettings gameSettings,
        IExternalAiPlayerManager externalAiManager,
        ILoggerFactory loggerFactory)
    {
        _gameSettings = gameSettings;
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
