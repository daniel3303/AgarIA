using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers;

[Route("admin/auth/{action}")]
public class AuthController : Controller
{
    private readonly SignInManager<IdentityUser> _signInManager;

    public AuthController(SignInManager<IdentityUser> signInManager) {
        _signInManager = signInManager;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login() {
        return View();
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password) {
        var result = await _signInManager.PasswordSignInAsync(username, password, true, false);
        if (result.Succeeded) {
            return RedirectToAction("Index", "Dashboard");
        }

        ViewData["Error"] = "Invalid username or password.";
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Logout() {
        await _signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }
}
