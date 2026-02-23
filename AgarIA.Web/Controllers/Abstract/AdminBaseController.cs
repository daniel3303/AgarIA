using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgarIA.Web.Controllers.Abstract;

[Authorize]
[Route("admin/{controller=Dashboard}/{action=Index}")]
public abstract class AdminBaseController : Controller
{
}
