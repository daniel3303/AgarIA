using AgarIA.Core.Data.Models;
using AgarIA.Core.Repositories;
using AgarIA.Web.Controllers.Abstract;
using AgarIA.Web.Data;
using AgarIA.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgarIA.Web.Controllers;

public class DashboardController : AdminBaseController
{
    private readonly GameState _gameState;
    private readonly PlayerRepository _playerRepository;
    private readonly FoodRepository _foodRepository;
    private readonly AdminDbContext _db;

    public DashboardController(
        GameState gameState,
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        AdminDbContext db) {
        _gameState = gameState;
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _db = db;
    }

    public async Task<IActionResult> Index() {
        ViewData["Menu"] = "Dashboard";
        ViewData["Title"] = "Dashboard";

        var alivePlayers = _playerRepository.GetAlive().Where(p => p.OwnerId == null).ToList();

        var model = new DashboardViewModel {
            CurrentTick = _gameState.CurrentTick,
            TotalPlayers = alivePlayers.Count,
            AiPlayers = alivePlayers.Count(p => p.IsAI),
            HumanPlayers = alivePlayers.Count(p => !p.IsAI),
            FoodCount = _foodRepository.GetCount(),
            TopScore = alivePlayers.Any() ? alivePlayers.Max(p => p.Score) : 0,
            Spectators = _gameState.Spectators.Count,
            RecentRounds = await _db.GameRounds
                .OrderByDescending(r => r.EndedAt)
                .Take(5)
                .ToListAsync()
        };

        return View(model);
    }
}
