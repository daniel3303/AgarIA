using AgarIA.Core.Game;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers.Api;

[ApiController]
[Route("api/ai")]
public class AiApiController : ControllerBase
{
    private readonly IExternalAiPlayerManager _externalAiManager;

    public AiApiController(IExternalAiPlayerManager externalAiManager)
    {
        _externalAiManager = externalAiManager;
    }

    [HttpGet("state")]
    public IActionResult GetState()
    {
        return Ok(_externalAiManager.GetGameState());
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(_externalAiManager.GetGameConfig());
    }

    [HttpPost("players")]
    public IActionResult RegisterPlayers([FromBody] RegisterPlayersRequest request)
    {
        if (request.Count < 1 || request.Count > 200)
            return BadRequest(new { Error = "Count must be between 1 and 200" });

        var ids = _externalAiManager.RegisterBots(request.Count);
        return Ok(new { PlayerIds = ids });
    }

    [HttpDelete("players")]
    public IActionResult RemovePlayers()
    {
        _externalAiManager.RemoveAllBots();
        return Ok(new { Message = "All external AI bots removed" });
    }

    [HttpPost("actions")]
    public IActionResult PostActions([FromBody] PostActionsRequest request)
    {
        if (request.Actions == null || request.Actions.Count == 0)
            return BadRequest(new { Error = "Actions list is required" });

        var actions = request.Actions.Select(a =>
            new ExternalBotAction(a.PlayerId, a.TargetX, a.TargetY, a.Split)).ToList();

        _externalAiManager.SetActions(actions);
        return Ok(new { Applied = actions.Count });
    }
    [HttpGet("training")]
    public IActionResult GetTrainingMode() {
        return Ok(new { Enabled = _externalAiManager.TrainingEnabled });
    }

    [HttpPost("training")]
    public IActionResult SetTrainingMode([FromBody] TrainingModeRequest request) {
        _externalAiManager.SetTrainingMode(request.Enabled);
        return Ok(new { Enabled = _externalAiManager.TrainingEnabled });
    }

    [HttpPost("stats")]
    public IActionResult PostStats([FromBody] TrainingStatsRequest request) {
        _externalAiManager.ReportTrainingStats(new TrainingStats(
            request.TotalUpdates,
            request.TotalSteps,
            request.AvgReward,
            request.PolicyLoss,
            request.ValueLoss,
            request.Entropy));
        return Ok();
    }
}

public record TrainingModeRequest(bool Enabled);

public record TrainingStatsRequest(
    int TotalUpdates,
    long TotalSteps,
    double AvgReward,
    double PolicyLoss,
    double ValueLoss,
    double Entropy);

public record RegisterPlayersRequest(int Count);

public record PostActionsRequest(List<ActionRequest> Actions);

public record ActionRequest(string PlayerId, double TargetX, double TargetY, bool Split);
