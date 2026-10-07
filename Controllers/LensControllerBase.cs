using System.Security.Claims;
using CustomerDepositRemaining.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CustomerDepositRemaining.Controllers
{
    /// <summary>
    /// base ของหน้าที่ต้อง login: ข้อมูลทุกอย่างผูกกับเบอร์ที่ยืนยัน OTP แล้วเท่านั้น
    /// และ customer_no ที่เลือกต้องเป็นของเบอร์นั้นจริง (ตรวจทุก request)
    /// </summary>
    [Authorize]
    public abstract class LensControllerBase : Controller
    {
        protected IVoucherLookupService Lookup { get; }
        protected LensControllerBase(IVoucherLookupService lookup) => Lookup = lookup;

        protected string Phone => User.FindFirstValue("phone") ?? "";
        protected string CustomerNo => User.FindFirstValue("customer_no") ?? "";
        protected string StoreName { get; private set; } = "";

        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var mine = string.IsNullOrEmpty(CustomerNo)
                ? null
                : await Lookup.FindCustomersAsync(Phone, HttpContext.RequestAborted);
            var store = mine?.FirstOrDefault(c => c.CustomerNo == CustomerNo);
            if (store is null)
            {
                context.Result = RedirectToAction("Stores", "Account");
                return;
            }
            StoreName = store.CustomerName;
            ViewData["StoreName"] = StoreName;
            await next();
        }
    }
}
