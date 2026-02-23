using AgarIA.Core.Data.Models;

namespace AgarIA.Web.ViewModels;

public class DashboardViewModel
{
    public long CurrentTick { get; set; }
    public int TotalPlayers { get; set; }
    public int AiPlayers { get; set; }
    public int HumanPlayers { get; set; }
    public int FoodCount { get; set; }
    public int TopScore { get; set; }
    public int Spectators { get; set; }
    public List<GameRound> RecentRounds { get; set; } = [];
}
