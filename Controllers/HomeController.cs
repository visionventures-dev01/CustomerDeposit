using System.Diagnostics;
using CustomerDepositRemaining.Models;
using Microsoft.AspNetCore.Mvc;

namespace CustomerDepositRemaining.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index() =>
            User.Identity?.IsAuthenticated == true
                ? RedirectToAction("Index", "Voucher")
                : RedirectToAction("Login", "Account");

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
