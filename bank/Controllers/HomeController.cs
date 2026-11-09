using Microsoft.AspNetCore.Mvc;

namespace SimpleBankMvc.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View();
    public IActionResult Error() => View();
    [Route("Home/Status/{code:int}")]
    public IActionResult Status(int code) { ViewBag.Code = code; return View(); }
}
