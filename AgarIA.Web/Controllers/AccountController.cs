using AgarIA.Web.Controllers.Abstract;
using AgarIA.Web.Dtos;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers;

public class AccountController : AdminBaseController {
    private readonly UserManager<IdentityUser> _userManager;

    public AccountController(UserManager<IdentityUser> userManager) {
        _userManager = userManager;
    }

    [HttpGet]
    public IActionResult ChangePassword() {
        ViewData["Menu"] = "";
        ViewData["Title"] = "Change Password";
        ViewData["Icon"] = "<icon name=\"key\" size=\"5\" />";
        return View(new ChangePasswordDto());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto) {
        ViewData["Menu"] = "";
        ViewData["Title"] = "Change Password";
        ViewData["Icon"] = "<icon name=\"key\" size=\"5\" />";

        if (!ModelState.IsValid)
            return View(dto);

        var user = await _userManager.GetUserAsync(User);
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, dto.NewPassword);

        if (!result.Succeeded) {
            foreach (var error in result.Errors)
                ModelState.AddModelError("", error.Description);
            return View(dto);
        }

        TempData["Success"] = "Password changed successfully.";
        return RedirectToAction(nameof(ChangePassword));
    }
}
