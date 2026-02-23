using AgarIA.Core.Data.Models;

namespace AgarIA.Web.ViewModels;

public class HistoryIndexViewModel
{
    public List<GameRound> Rounds { get; set; } = [];
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
}
