namespace AgarIA.Core.Game;

public interface IAIController
{
    bool Tick(long currentTick);
    string GetTickBreakdown();
    object GetFitnessStats();
    void SetResetAtScore(double score);
    void SaveGenomes();
    void RandomizePlayerCount();
}
