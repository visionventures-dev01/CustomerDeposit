using System.Security.Claims;
using CustomerDepositRemaining.Models;
using CustomerDepositRemaining.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CustomerDepositRemaining.Controllers
{
    /// <summary>หน้าผู้ดูแล: login ด้วย AD แล้วค้นหาลูกค้าจากเบอร์ เพื่อเข้าดูข้อมูลแทนลูกค้า (ไม่ต้องใช้ OTP)</summary>
    public class AdminController : Controller
    {
        public const string Scheme = "AdminCookie";

        private readonly IAdminAuthService _auth;
        private readonly IVoucherLookupService _lookup;
        private readonly AdminOptions _opt;

        public AdminController(IAdminAuthService auth, IVoucherLookupService lookup, IOptions<AdminOptions> opt)
        {
            _auth = auth; _lookup = lookup; _opt = opt.Value;
        }

        private async Task<bool> IsAdminAsync() =>
            (await HttpContext.AuthenticateAsync(Scheme)).Succeeded;

        [HttpGet]
        public async Task<IActionResult> Index() =>
            await IsAdminAsync() ? View("Lookup", new AdminLookupViewModel()) : View("Login", new AdminLoginViewModel());

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string? username, string? password, bool remember, CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (username.Length == 0 || username.Length > 150 || string.IsNullOrEmpty(password) || password.Length > 500)
                return View("Login", new AdminLoginViewModel { Username = username, Error = "ไม่สามารถเข้าสู่ระบบได้" });

            var result = await _auth.AuthenticateAsync(username, password, ct);
            if (!result.Ok)
            {
                var msg = result.Failure switch
                {
                    AdminAuthFailure.Rejected => "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง",
                    AdminAuthFailure.Throttled => "ลองผิดหลายครั้งเกินกำหนด กรุณารอสักครู่แล้วลองใหม่",
                    _ => "ระบบยืนยันตัวตนไม่พร้อมใช้งานชั่วคราว"
                };
                Response.StatusCode = result.Failure == AdminAuthFailure.Rejected ? 401 : result.Failure == AdminAuthFailure.Throttled ? 429 : 503;
                return View("Login", new AdminLoginViewModel { Username = username, Error = msg });
            }

            var id = result.Identity!;
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, id.Username),
                new("full_name", id.DisplayName),
                new("auth_provider", "ad")
            };
            if (id.Email is not null) claims.Add(new Claim(ClaimTypes.Email, id.Email));
            claims.AddRange(id.Groups.Select(g => new Claim("group", g)));

            await HttpContext.SignInAsync(Scheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)),
                new AuthenticationProperties
                {
                    IsPersistent = remember,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(remember ? _opt.RememberDays : 0).AddHours(remember ? 0 : _opt.AccessHours)
                });
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Lookup(string? phone, CancellationToken ct)
        {
            if (!await IsAdminAsync()) return RedirectToAction(nameof(Index));

            phone = (phone ?? "").Trim();
            if (phone.Length == 0)
                return View("Lookup", new AdminLookupViewModel { Error = "กรอกเบอร์โทรศัพท์ก่อนเข้าสู่ระบบ" });
            if (phone.Length > 100)
                return View("Lookup", new AdminLookupViewModel { Phone = phone[..100], Error = "ไม่สามารถค้นหาข้อมูลได้" });

            List<CustomerCandidate> customers;
            try { customers = await _lookup.FindCustomersAsync(phone, ct); }
            catch (Exception)
            {
                return View("Lookup", new AdminLookupViewModel { Phone = phone, Error = "ไม่สามารถค้นหาข้อมูลได้" });
            }
            if (customers.Count == 0)
                return View("Lookup", new AdminLookupViewModel { Phone = phone, Error = "ไม่พบข้อมูลลูกค้าจากเบอร์นี้" });

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("phone", phone),
                new Claim("admin", "1")
            }, CookieAuthenticationDefaults.AuthenticationScheme));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
            return RedirectToAction("Stores", "Account");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(Scheme);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Index));
        }
    }
}
