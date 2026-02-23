using AgarIA.Web.Controllers.Abstract;
using AgarIA.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgarIA.Web.Controllers;

public class HistoryController : AdminBaseController {
    private readonly AdminDbContext _db;

    public HistoryController(AdminDbContext db) {
        _db = db;
    }

    public IActionResult Index() {
        ViewData["Menu"] = "History";
        ViewData["Title"] = "Game History";
        ViewData["Icon"] = "<icon name=\"clock\" size=\"5\" />";

        return View(_db.GameRounds.OrderByDescending(r => r.EndedAt));
    }

    public async Task<IActionResult> Details(int id) {
        ViewData["Menu"] = "History";
        ViewData["Title"] = "Round Details";

        var round = await _db.GameRounds
            .Include(r => r.PlayerStats)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (round == null) return NotFound();

        return View(round);
    }
}
