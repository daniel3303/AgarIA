using System.Collections.Concurrent;

namespace AgarIA.Core.Game;

public record BotPerception(
    List<int> FoodIds,
    List<string> PlayerIds,
    List<int> ProjectileIds,
    string LargestPlayerId,
    double FoodRadius,
    double PlayerRadius);

public interface IAIController
{
    bool Tick(long currentTick);
    string GetTickBreakdown();
    object GetFitnessStats();
    void SetResetAtScore(double score);
    void SaveGenomes();
    void RandomizePlayerCount();
    ConcurrentDictionary<string, BotPerception> BotPerceptions { get; }
}
