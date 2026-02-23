using AgarIA.Core.AI;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Web.Controllers.Abstract;
using AgarIA.Web.Data;
using AgarIA.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers;

public class SettingsController : AdminBaseController
{
    private readonly GameSettings _gameSettings;
    private readonly GameEngine _gameEngine;
    private readonly AIPlayerController _aiController;
    private readonly AdminDbContext _db;

    public SettingsController(
        GameSettings gameSettings,
        GameEngine gameEngine,
        AIPlayerController aiController,
        AdminDbContext db) {
        _gameSettings = gameSettings;
        _gameEngine = gameEngine;
        _aiController = aiController;
        _db = db;
    }

    public IActionResult Index() {
        ViewData["Menu"] = "Settings";
        ViewData["Title"] = "Game Settings";
        return View(_gameSettings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int minAIPlayers, int maxAIPlayers, double resetAtScore, int autoResetSeconds, bool maxSpeed, ResetType resetType) {
        _gameSettings.MinAIPlayers = Math.Max(0, minAIPlayers);
        _gameSettings.MaxAIPlayers = Math.Max(_gameSettings.MinAIPlayers, maxAIPlayers);
        _gameSettings.ResetAtScore = Math.Max(100, resetAtScore);
        _gameSettings.AutoResetSeconds = Math.Max(0, autoResetSeconds);
        _gameSettings.MaxSpeed = maxSpeed;
        _gameSettings.ResetType = resetType;

        // Apply to running game
        _aiController.SetResetAtScore(_gameSettings.ResetAtScore);
        _gameEngine.SetAutoResetSeconds(_gameSettings.AutoResetSeconds);
        _gameEngine.SetMaxSpeed(_gameSettings.MaxSpeed);

        // Persist to database
        await AdminSettingsService.Save(_db, _gameSettings);

        TempData["Success"] = "Settings updated successfully.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResetGame() {
        _gameEngine.RequestReset();
        TempData["Success"] = "Game reset requested.";
        return RedirectToAction(nameof(Index));
    }
}
