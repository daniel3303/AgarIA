using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers;

[Route("/")]
public class GameController : Controller
{
    [HttpGet]
    public IActionResult Index() {
        return View();
    }
}
