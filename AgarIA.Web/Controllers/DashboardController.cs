using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
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
    private readonly GameEngine _gameEngine;
    private readonly GameSettings _gameSettings;
    private readonly IAIController _aiController;

    public DashboardController(
        GameState gameState,
        PlayerRepository playerRepository,
        FoodRepository foodRepository,
        AdminDbContext db,
        GameEngine gameEngine,
        GameSettings gameSettings,
        IAIController aiController) {
        _gameState = gameState;
        _playerRepository = playerRepository;
        _foodRepository = foodRepository;
        _db = db;
        _gameEngine = gameEngine;
        _gameSettings = gameSettings;
        _aiController = aiController;
    }

    public async Task<IActionResult> Index() {
        ViewData["Menu"] = "Dashboard";
        ViewData["Title"] = "Dashboard";

        var alivePlayers = _playerRepository.GetAlive().Where(p => p.OwnerId == null).ToList();
        var tps = _gameEngine.CurrentTps;

        var model = new DashboardViewModel {
            CurrentTick = _gameState.CurrentTick,
            TotalPlayers = alivePlayers.Count,
            AiPlayers = alivePlayers.Count(p => p.IsAI),
            HumanPlayers = alivePlayers.Count(p => !p.IsAI),
            FoodCount = _foodRepository.GetCount(),
            TopScore = alivePlayers.Any() ? alivePlayers.Max(p => p.Score) : 0,
            Spectators = _gameState.Spectators.Count,
            TicksPerSecond = tps,
            SpeedMultiplier = tps / 20.0,
            MaxSpeed = _gameSettings.MaxSpeed,
            RecentRounds = await _db.GameRounds
                .OrderByDescending(r => r.EndedAt)
                .Take(5)
                .ToListAsync()
        };

        return View(model);
    }

    [HttpGet]
    public IActionResult Stats() {
        var alivePlayers = _playerRepository.GetAlive().Where(p => p.OwnerId == null).ToList();
        var tps = _gameEngine.CurrentTps;

        return Json(new {
            currentTick = _gameState.CurrentTick,
            totalPlayers = alivePlayers.Count,
            aiPlayers = alivePlayers.Count(p => p.IsAI),
            humanPlayers = alivePlayers.Count(p => !p.IsAI),
            foodCount = _foodRepository.GetCount(),
            topScore = alivePlayers.Any() ? alivePlayers.Max(p => p.Score) : 0,
            spectators = _gameState.Spectators.Count,
            ticksPerSecond = tps,
            speedMultiplier = Math.Round(tps / 20.0, 1),
            maxSpeed = _gameSettings.MaxSpeed,
            fitnessStats = _aiController.GetFitnessStats()
        });
    }
}
