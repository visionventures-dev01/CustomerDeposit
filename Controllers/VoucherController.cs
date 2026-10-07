using System.Globalization;
using System.Text;
using CustomerDepositRemaining.Models;
using CustomerDepositRemaining.Services;
using Microsoft.AspNetCore.Mvc;

namespace CustomerDepositRemaining.Controllers
{
    public class VoucherController : LensControllerBase
    {
        private const int PageSize = 10;

        public VoucherController(IVoucherLookupService lookup) : base(lookup) { }

        public async Task<IActionResult> Index(string? tab, string? q, int page = 1, CancellationToken ct = default)
        {
            var all = await Lookup.GetVouchersAsync(CustomerNo, ct);
            tab = tab == "used" ? "used" : "active";
            var search = (q ?? "").Trim();

            var filtered = all
                .Where(v => (tab == "active" ? v.Status == VoucherStatus.Active : v.Status != VoucherStatus.Active)
                            && v.DepositNo.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
            page = Math.Clamp(page, 1, totalPages);

            ViewData["Active"] = "vouchers";
            return View(new VoucherListViewModel
            {
                Search = search,
                Tab = tab,
                Page = page,
                TotalPages = totalPages,
                ActiveBalance = all.Where(v => v.Status == VoucherStatus.Active).Sum(v => v.DepositBalance),
                Items = filtered.Skip((page - 1) * PageSize).Take(PageSize).ToList()
            });
        }

        public async Task<IActionResult> Detail(string id, int? year, string? expanded, CancellationToken ct = default)
        {
            ViewData["Active"] = "vouchers";
            var voucher = (await Lookup.GetVouchersAsync(CustomerNo, ct)).FirstOrDefault(v => v.DepositNo == id);
            if (voucher is null) { Response.StatusCode = 404; return View("NotFound", ("ไม่พบเวาเชอร์", "เวาเชอร์นี้ไม่มีอยู่ หรือไม่ได้อยู่ในร้านที่เลือก", Url.Action("Index")!)); }

            var all = await Lookup.GetRedemptionsAsync(CustomerNo, id, ct);
            var years = all.Select(r => r.Date.Year).Distinct().OrderByDescending(y => y).ToList();
            var activeYear = year is not null && years.Contains(year.Value) ? year : years.Cast<int?>().FirstOrDefault();

            var bills = all
                .Where(r => r.Date.Year == activeYear)
                .GroupBy(r => $"{r.Document}:{r.Date:yyyy-MM-dd}")
                .Select(g => new RedemptionBill { Id = g.Key, Items = g.ToList() })
                .ToList();

            return View(new VoucherDetailViewModel
            {
                Voucher = voucher,
                Year = activeYear,
                Years = years,
                Bills = bills,
                AllRedemptions = all,
                Expanded = expanded
            });
        }

        /// <summary>CSV ประวัติการใช้เวาเชอร์ ตามเดือนที่เลือก (months = yyyy-MM,yyyy-MM,...)</summary>
        public async Task<IActionResult> ExportCsv(string id, string? months, CancellationToken ct = default)
        {
            var all = await Lookup.GetRedemptionsAsync(CustomerNo, id, ct);
            if (all.Count == 0) return NotFound();

            var selected = (months ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            if (selected.Count == 0)
            {
                var latest = all.Max(r => r.Date);
                selected.Add(latest.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            }

            var rows = all
                .Where(r => selected.Contains(r.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
                .OrderByDescending(r => r.Date);

            var sb = new StringBuilder("date,document,credit_note,description,prescription,quantity,unit,amount\r\n");
            foreach (var r in rows)
            {
                sb.AppendJoin(',', new[]
                {
                    r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Document, r.CreditNote,
                    r.Description, r.ItemNo, r.Quantity.ToString(CultureInfo.InvariantCulture), r.Unit,
                    r.Amount.ToString(CultureInfo.InvariantCulture)
                }.Select(Csv)).Append("\r\n");
            }

            var safe = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('-');
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv;charset=utf-8", $"{(safe.Length > 0 ? safe : "export")}-redemptions.csv");
        }

        private static string Csv(string v)
        {
            v ??= "";
            // กัน CSV/Excel formula injection จากข้อมูลที่มาจาก DB
            if (v.Length > 0 && v[0] is '=' or '+' or '@' or '\t' or '\r') v = "'" + v;
            return v.IndexOfAny(new[] { '"', ',', '\n', '\r' }) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        }
    }
}
