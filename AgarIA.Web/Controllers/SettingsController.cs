using AgarIA.Core.AI;
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
    private readonly AIPlayerController _aiController;
    private readonly AdminDbContext _db;
    private readonly IFlashMessage _flashMessage;

    public SettingsController(
        GameSettings gameSettings,
        GameEngine gameEngine,
        AIPlayerController aiController,
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
        int minAIPlayers, int maxAIPlayers, double resetAtScore, int minResetSeconds, int maxResetSeconds,
        bool maxSpeed, ResetType resetType,
        string easyHiddenLayers, string mediumHiddenLayers, string hardHiddenLayers,
        bool easyEnabled, bool mediumEnabled, bool hardEnabled,
        string ppoLearningRate, int ppoBufferSize, int ppoMinibatchSize, int ppoEpochs,
        float ppoEntropyCoeff, float ppoClipEpsilon) {

        _gameSettings.MinAIPlayers = Math.Max(0, minAIPlayers);
        _gameSettings.MaxAIPlayers = Math.Max(_gameSettings.MinAIPlayers, maxAIPlayers);
        _gameSettings.ResetAtScore = Math.Max(100, resetAtScore);
        _gameSettings.MinResetSeconds = Math.Max(0, minResetSeconds);
        _gameSettings.MaxResetSeconds = Math.Max(_gameSettings.MinResetSeconds, maxResetSeconds);
        _gameSettings.MaxSpeed = maxSpeed;
        _gameSettings.ResetType = resetType;

        // Apply tier enable/disable
        _gameSettings.EasyEnabled = easyEnabled;
        _gameSettings.MediumEnabled = mediumEnabled;
        _gameSettings.HardEnabled = hardEnabled;
        _aiController.ApplyTierEnabled();

        // Apply to running game
        _aiController.SetResetAtScore(_gameSettings.ResetAtScore);
        _gameEngine.SetResetSecondsRange(_gameSettings.MinResetSeconds, _gameSettings.MaxResetSeconds);
        _gameEngine.SetMaxSpeed(_gameSettings.MaxSpeed);

        // PPO hyperparameters
        if (!string.IsNullOrWhiteSpace(ppoLearningRate) &&
            float.TryParse(ppoLearningRate, System.Globalization.CultureInfo.InvariantCulture, out var lr) && lr > 0)
            _gameSettings.PPO.LearningRate = lr;
        if (ppoBufferSize >= 256) _gameSettings.PPO.BufferSize = ppoBufferSize;
        if (ppoMinibatchSize >= 32) _gameSettings.PPO.MinibatchSize = Math.Min(ppoMinibatchSize, _gameSettings.PPO.BufferSize);
        if (ppoEpochs >= 1) _gameSettings.PPO.Epochs = ppoEpochs;
        if (ppoEntropyCoeff >= 0) _gameSettings.PPO.EntropyCoeff = ppoEntropyCoeff;
        if (ppoClipEpsilon > 0) _gameSettings.PPO.ClipEpsilon = ppoClipEpsilon;

        // Process architecture changes
        ApplyArchitecture(BotDifficulty.Easy, easyHiddenLayers, _gameSettings.EasyHiddenLayers, layers => _gameSettings.EasyHiddenLayers = layers);
        ApplyArchitecture(BotDifficulty.Medium, mediumHiddenLayers, _gameSettings.MediumHiddenLayers, layers => _gameSettings.MediumHiddenLayers = layers);
        ApplyArchitecture(BotDifficulty.Hard, hardHiddenLayers, _gameSettings.HardHiddenLayers, layers => _gameSettings.HardHiddenLayers = layers);

        // If architecture changed, request game reset
        if (_aiController.ConsumeResetRequest())
            _gameEngine.RequestReset();

        // Persist to database
        await AdminSettingsService.Save(_db, _gameSettings);

        _flashMessage.Success("Settings updated successfully.");
        return RedirectToAction(nameof(Index));
    }

    private void ApplyArchitecture(BotDifficulty tier, string input, List<int> current, Action<List<int>> setter)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var parsed = ParseHiddenLayers(input);
        if (parsed == null)
        {
            _flashMessage.Error($"Invalid {tier} hidden layers format. Use comma-separated positive integers (e.g. 128 or 128,64).");
            return;
        }

        if (!parsed.SequenceEqual(current))
        {
            setter(parsed);
            _aiController.ReconfigureTier(tier, parsed);
        }
    }

    private static List<int> ParseHiddenLayers(string input)
    {
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var result = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var val) || val < 1 || val > 1024)
                return null;
            result.Add(val);
        }
        return result;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResetGame() {
        _gameEngine.RequestReset();
        _flashMessage.Success("Game reset requested.");
        return RedirectToAction(nameof(Index));
    }
}
