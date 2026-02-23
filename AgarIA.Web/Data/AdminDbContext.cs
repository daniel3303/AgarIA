using AgarIA.Core.Data.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgarIA.Web.Data;

public class AdminDbContext : IdentityDbContext<IdentityUser>
{
    public DbSet<GameRound> GameRounds { get; set; }
    public DbSet<PlayerGameStat> PlayerGameStats { get; set; }
    public DbSet<AdminSetting> AdminSettings { get; set; }

    public AdminDbContext(DbContextOptions<AdminDbContext> options) : base(options) {
    }
}
