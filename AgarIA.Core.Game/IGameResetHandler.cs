using AgarIA.Core.Data.Models;

namespace AgarIA.Core.Game;

public interface IGameResetHandler
{
    void OnBeforeReset(IEnumerable<Player> players, long startTick, long endTick);
}
