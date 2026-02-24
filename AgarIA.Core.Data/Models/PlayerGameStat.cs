using System.ComponentModel.DataAnnotations;

namespace AgarIA.Core.Data.Models;

public class PlayerGameStat
{
    [Key]
    public int Id { get; set; }

    public int GameRoundId { get; set; }

    public string Username { get; set; }

    public bool IsAI { get; set; }

    public int FinalScore { get; set; }

    public double TotalMass { get; set; }

    public int FoodEaten { get; set; }

    public int PlayersKilled { get; set; }

    public double PlayerMassEaten { get; set; }

    public double ProjectileMassGained { get; set; }

    public GameRound GameRound { get; set; }
}
