using System.ComponentModel.DataAnnotations;

namespace AgarIA.Core.Data.Models;

public class GameRound
{
    [Key]
    public int Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime EndedAt { get; set; }

    public long DurationTicks { get; set; }

    public int PlayerCount { get; set; }

    public int AiPlayerCount { get; set; }

    public double TotalMass { get; set; }

    public List<PlayerGameStat> PlayerStats { get; set; } = [];
}
