using System.Security.Claims;
using System.Text.RegularExpressions;
using CustomerDepositRemaining.Models;
using CustomerDepositRemaining.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CustomerDepositRemaining.Controllers
{
    public class AccountController : Controller
    {
        private static readonly Regex PhonePattern = new("^[0-9]{10}$", RegexOptions.Compiled);

        private readonly IOtpService _otp;
        private readonly IVoucherLookupService _lookup;

        public AccountController(IOtpService otp, IVoucherLookupService lookup)
        {
            _otp = otp; _lookup = lookup;
        }

        [HttpGet]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToAction(nameof(Stores));
            return View(new LoginViewModel());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestOtp(string? phone, CancellationToken ct)
        {
            phone = (phone ?? "").Trim();
            if (!PhonePattern.IsMatch(phone))
                return View("Login", new LoginViewModel { Phone = phone, Error = "กรุณากรอกเบอร์โทรศัพท์เป็นตัวเลข 10 หลัก" });

            List<CustomerCandidate> customers;
            try { customers = await _lookup.FindCustomersAsync(phone, ct); }
            catch (Exception)
            {
                return View("Login", new LoginViewModel { Phone = phone, Error = "ไม่สามารถเชื่อมต่อฐานข้อมูลได้ กรุณาลองใหม่อีกครั้ง" });
            }
            if (customers.Count == 0) return View("PhoneNotFound");

            var start = await _otp.StartAsync(phone, ct);
            if (!start.Ok)
            {
                var msg = start.Failure switch
                {
                    OtpFailure.TooManyAttempts => "ขอ OTP บ่อยเกินไป กรุณารอสักครู่แล้วลองใหม่",
                    OtpFailure.Unavailable => "ระบบส่ง OTP ชั่วคราวไม่ตอบสนอง กรุณาลองใหม่อีกครั้ง",
                    _ => "ไม่สามารถส่ง OTP ได้ กรุณาลองใหม่อีกครั้ง"
                };
                return View("Login", new LoginViewModel { Phone = phone, Error = msg });
            }
            return View("Verify", new VerifyViewModel
            {
                RequestId = start.RequestId,
                Phone = phone,
                ReferenceCode = start.ReferenceCode,
                ExpiresInSeconds = start.ExpiresInSeconds,
                ResendAfterSeconds = start.ResendAfterSeconds,
                DemoMode = _otp.DemoMode
            });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Verify(string? requestId, string? phone, string? code, CancellationToken ct)
        {
            var vm = new VerifyViewModel { RequestId = requestId ?? "", Phone = phone ?? "", DemoMode = _otp.DemoMode };
            code = (code ?? "").Trim();
            if (!Regex.IsMatch(code, "^[0-9]{6}$") || string.IsNullOrEmpty(requestId))
            {
                vm.Error = "กรุณากรอกรหัส OTP 6 หลัก";
                return View(vm);
            }

            var result = await _otp.VerifyAsync(requestId, code, ct);
            if (!result.Ok)
            {
                vm.Error = result.Failure switch
                {
                    OtpFailure.RequestInvalid => "กรุณาขอรหัส OTP ใหม่อีกครั้ง",
                    OtpFailure.Expired => "รหัส OTP หมดอายุแล้ว กรุณาขอรหัสใหม่",
                    OtpFailure.TooManyAttempts => "กรอกรหัสผิดเกินกำหนด กรุณาขอรหัสใหม่",
                    OtpFailure.Unavailable => "ระบบตรวจสอบ OTP ชั่วคราวไม่ตอบสนอง กรุณากดเข้าสู่ระบบอีกครั้ง",
                    _ => "รหัส OTP ไม่ถูกต้อง"
                };
                if (result.Failure is OtpFailure.Expired or OtpFailure.RequestInvalid or OtpFailure.TooManyAttempts)
                {
                    vm.ResendAfterSeconds = 0;
                    vm.ExpiresInSeconds = 0;
                }
                else
                {
                    vm.ExpiresInSeconds = Math.Max(0, int.TryParse(Request.Form["expires"], out var left) ? left : 0);
                    vm.ReferenceCode = Request.Form["ref"].ToString();
                }
                return View(vm);
            }

            await SignInAsync(result.Phone, null);
            TempData["Toast"] = "เข้าสู่ระบบสำเร็จ";
            return RedirectToAction(nameof(Stores));
        }

        [Authorize, HttpGet]
        public async Task<IActionResult> Stores(CancellationToken ct)
        {
            var phone = User.FindFirstValue("phone") ?? "";
            var stores = await _lookup.FindCustomersAsync(phone, ct);
            var vm = new StoreListViewModel();
            foreach (var s in stores)
            {
                var vouchers = await _lookup.GetVouchersAsync(s.CustomerNo, ct);
                vm.Stores.Add((s, vouchers.Count(v => v.Status == VoucherStatus.Active)));
            }
            return View(vm);
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SelectStore(string customerNo, CancellationToken ct)
        {
            var phone = User.FindFirstValue("phone") ?? "";
            var stores = await _lookup.FindCustomersAsync(phone, ct);
            if (stores.All(s => s.CustomerNo != customerNo)) return RedirectToAction(nameof(Stores));
            await SignInAsync(phone, customerNo);
            TempData["Toast"] = "เลือกร้านค้าเรียบร้อยแล้ว";
            return RedirectToAction("Index", "Voucher");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            var wasAdmin = User.FindFirstValue("admin") == "1";
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            TempData["Toast"] = "ออกจากระบบเรียบร้อยแล้ว";
            return wasAdmin ? RedirectToAction("Index", "Admin") : RedirectToAction(nameof(Login));
        }

        private Task SignInAsync(string phone, string? customerNo)
        {
            var claims = new List<Claim> { new("phone", phone) };
            if (User.FindFirstValue("admin") == "1") claims.Add(new Claim("admin", "1"));
            if (!string.IsNullOrEmpty(customerNo)) claims.Add(new Claim("customer_no", customerNo));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        }
    }
}
