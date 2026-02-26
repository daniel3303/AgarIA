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
            TrainingEnabled = _externalAiManager.TrainingEnabled,
            ConnectedBots = _externalAiManager.ConnectedBotCount,
            Stats = stats == null ? null : new {
                TotalUpdates = stats.TotalUpdates,
                TotalSteps = stats.TotalSteps,
                AvgReward = stats.AvgReward,
                PolicyLoss = stats.PolicyLoss,
                ValueLoss = stats.ValueLoss,
                Entropy = stats.Entropy
            }
        });
    }

    [HttpPost]
    public IActionResult Toggle(bool enabled) {
        _externalAiManager.SetTrainingMode(enabled);
        return Json(new { Enabled = _externalAiManager.TrainingEnabled });
    }
}
