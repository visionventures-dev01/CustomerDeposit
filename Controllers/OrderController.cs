using CustomerDepositRemaining.Models;
using CustomerDepositRemaining.Services;
using Microsoft.AspNetCore.Mvc;

namespace CustomerDepositRemaining.Controllers
{
    public class OrderController : LensControllerBase
    {
        public OrderController(IVoucherLookupService lookup) : base(lookup) { }

        public async Task<IActionResult> Index(string? q, bool cancelled = false, string? sort = null, CancellationToken ct = default)
        {
            var search = (q ?? "").Trim();
            var asc = sort == "asc";
            var orders = (await Lookup.GetOrdersAsync(CustomerNo, ct))
                .Where(o => ($"{o.SalesOrderNo} {o.SalesInvoiceNo} {o.CreditNoteNo}").Contains(search, StringComparison.OrdinalIgnoreCase)
                            && (o.Status == OrderStatus.Cancelled) == cancelled);
            orders = asc ? orders.OrderBy(o => o.DocumentDate, StringComparer.Ordinal) : orders.OrderByDescending(o => o.DocumentDate, StringComparer.Ordinal);

            ViewData["Active"] = "orders";
            return View(new OrderListViewModel { Search = search, Cancelled = cancelled, Ascending = asc, Items = orders.ToList() });
        }

        public async Task<IActionResult> Detail(string id, CancellationToken ct = default)
        {
            ViewData["Active"] = "orders";
            var order = (await Lookup.GetOrdersAsync(CustomerNo, ct)).FirstOrDefault(o => o.Id == id);
            if (order is null) { Response.StatusCode = 404; return View("NotFound", ("ไม่พบคำสั่งซื้อ", "คำสั่งซื้อนี้ไม่มีอยู่ หรือไม่ได้อยู่ในร้านที่เลือก", Url.Action("Index")!)); }
            return View(order);
        }
    }
}
