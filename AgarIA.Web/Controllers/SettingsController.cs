using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Web.Controllers.Abstract;
using AgarIA.Web.Data;
using AgarIA.Web.Services;
using AgarIA.Web.Services.FlashMessage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers;

public class SettingsController : AdminBaseController
{
    private readonly GameSettings _gameSettings;
    private readonly GameEngine _gameEngine;
    private readonly IAIController _aiController;
    private readonly AdminDbContext _db;
    private readonly IFlashMessage _flashMessage;

    public SettingsController(
        GameSettings gameSettings,
        GameEngine gameEngine,
        IAIController aiController,
        AdminDbContext db,
        IFlashMessage flashMessage) {
        _gameSettings = gameSettings;
        _gameEngine = gameEngine;
        _aiController = aiController;
        _db = db;
        _flashMessage = flashMessage;
    }

    public IActionResult Index() {
        ViewData["Menu"] = "Settings";
        ViewData["Title"] = "Game Settings";
        return View(_gameSettings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        double resetAtScore, int minResetSeconds, int maxResetSeconds,
        bool maxSpeed, int speedMultiplier, ResetType resetType,
        bool heuristicEnabled, int heuristicPlayerCount, bool heuristicCanEatEachOther) {

        _gameSettings.ResetAtScore = Math.Max(100, resetAtScore);
        _gameSettings.MinResetSeconds = Math.Max(0, minResetSeconds);
        _gameSettings.MaxResetSeconds = Math.Max(_gameSettings.MinResetSeconds, maxResetSeconds);
        _gameSettings.MaxSpeed = maxSpeed;
        _gameSettings.ResetType = resetType;
        _gameSettings.HeuristicEnabled = heuristicEnabled;
        _gameSettings.HeuristicPlayerCount = Math.Clamp(heuristicPlayerCount, 0, 50);
        _gameSettings.HeuristicCanEatEachOther = heuristicCanEatEachOther;

        _gameSettings.SpeedMultiplier = Math.Clamp(speedMultiplier, 1, 100);

        // Apply to running game
        _aiController.SetResetAtScore(_gameSettings.ResetAtScore);
        _gameEngine.SetResetSecondsRange(_gameSettings.MinResetSeconds, _gameSettings.MaxResetSeconds);
        _gameEngine.SetSpeedMultiplier(_gameSettings.SpeedMultiplier);
        _gameEngine.SetMaxSpeed(_gameSettings.MaxSpeed);

        // Persist to database
        await AdminSettingsService.Save(_db, _gameSettings);

        _flashMessage.Success("Settings updated successfully.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResetGame() {
        _gameEngine.RequestReset();
        _flashMessage.Success("Game reset requested.");
        return RedirectToAction(nameof(Index));
    }
}
