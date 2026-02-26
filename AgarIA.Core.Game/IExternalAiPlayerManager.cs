using AgarIA.Core.Data.Models;

namespace AgarIA.Core.Game;

public interface IExternalAiPlayerManager
{
    List<string> RegisterBots(int count);
    void RemoveAllBots();
    void SetActions(List<ExternalBotAction> actions);
    void ApplyActions();
    void CleanupTimedOut();
    bool IsExternalBot(string playerId);
    GameStateSnapshot GetGameState();
    GameConfigSnapshot GetGameConfig();
}

public record ExternalBotAction(string PlayerId, double TargetX, double TargetY, bool Split);

public record GameStateSnapshot(
    long Tick,
    int MapSize,
    List<PlayerSnapshot> Players,
    List<FoodSnapshot> Food);

public record PlayerSnapshot(
    string Id,
    double X,
    double Y,
    double Mass,
    double Vx,
    double Vy,
    bool IsAlive,
    string OwnerId,
    double Speed);

public record FoodSnapshot(double X, double Y);

public record GameConfigSnapshot(
    int MapSize,
    double StartMass,
    double EatSizeRatio,
    double BaseSpeed,
    int TickRate,
    int MaxFood,
    double FoodMass,
    double MinSplitMass,
    double MassDecayRate,
    int MaxSplitCells);
