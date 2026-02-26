using AgarIA.Core.Game;
using AgarIA.Web.Controllers.Abstract;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers;

public class TrainingController : AdminBaseController
{
    private readonly IExternalAiPlayerManager _externalAiManager;

    public TrainingController(IExternalAiPlayerManager externalAiManager) {
        _externalAiManager = externalAiManager;
    }

    public IActionResult Index() {
        ViewData["Menu"] = "Training";
        ViewData["Title"] = "Training";
        ViewData["Icon"] = "<icon name=\"academic-cap\" size=\"5\" />";
        return View();
    }

    [HttpGet]
    public IActionResult Stats() {
        var stats = _externalAiManager.LastTrainingStats;
        return Json(new {
            trainingEnabled = _externalAiManager.TrainingEnabled,
            connectedBots = _externalAiManager.ConnectedBotCount,
            stats = stats == null ? null : new {
                totalUpdates = stats.TotalUpdates,
                totalSteps = stats.TotalSteps,
                avgReward = stats.AvgReward,
                policyLoss = stats.PolicyLoss,
                valueLoss = stats.ValueLoss,
                entropy = stats.Entropy
            }
        });
    }

    [HttpPost]
    public IActionResult Toggle(bool enabled) {
        _externalAiManager.SetTrainingMode(enabled);
        return Json(new { enabled = _externalAiManager.TrainingEnabled });
    }
}
