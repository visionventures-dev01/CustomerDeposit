using CustomerDepositRemaining.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace CustomerDepositRemaining.Services
{
    /// <summary>ข้อมูลดิบของเวาเชอร์หนึ่งเล่ม (ใช้สร้างประวัติการใช้และ PDF)</summary>
    public record VoucherFlow(
        VoucherSummary Voucher,
        List<Dictionary<string, object?>> UsageDocuments,
        List<Dictionary<string, object?>> UsageLines,
        List<Dictionary<string, object?>> OrderReferences);

    public interface IVoucherLookupService
    {
        Task<List<CustomerCandidate>> FindCustomersAsync(string phone, CancellationToken ct);
        Task<List<VoucherSummary>> GetVouchersAsync(string customerNo, CancellationToken ct);
        Task<List<Redemption>> GetRedemptionsAsync(string customerNo, string depositNo, CancellationToken ct);
        Task<VoucherFlow?> GetVoucherFlowAsync(string customerNo, string? depositNo, CancellationToken ct);
        Task<List<OrderGroup>> GetOrdersAsync(string customerNo, CancellationToken ct);
    }

    /// <summary>
    /// ดึงข้อมูลจาก view vvd_voucher_* (read-only) ทุก query ใช้ parameter ไม่ต่อ string
    /// ตรรกะ grouping/mapping port มาจาก backend/app/voucher_mssql.py ของระบบ Python เดิม
    /// </summary>
    public class VoucherLookupService : IVoucherLookupService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
        private readonly string _cs;
        private readonly IMemoryCache _cache;

        public VoucherLookupService(IConfiguration config, IMemoryCache cache)
        {
            _cs = config.GetConnectionString("VOUCHER_ConnectionString")
                  ?? throw new InvalidOperationException("Missing ConnectionStrings:VOUCHER_ConnectionString");
            _cache = cache;
        }

        public static string NormalizePhone(string value)
        {
            var n = new string((value ?? "").Where(char.IsDigit).ToArray());
            return n.StartsWith("66") ? "0" + n[2..] : n;
        }

        // ---------------------------------------------------------------- customers

        public Task<List<CustomerCandidate>> FindCustomersAsync(string phone, CancellationToken ct) =>
            Cached($"cust:{phone}", async () =>
            {
                var rows = await QueryAsync(@"
                    SELECT c.customer_no,
                           MAX(c.customer_name)     AS customer_name,
                           COUNT_BIG(*)             AS order_count,
                           MAX(c.loaded_at)         AS latest_order_date,
                           MAX(c.customer_phone_no) AS sample_phone_no
                    FROM [dbo].[vvd_voucher_customer_detail] AS c
                    WHERE c.customer_phone_no = @phone
                    GROUP BY c.customer_no
                    ORDER BY latest_order_date DESC, order_count DESC, c.customer_no",
                    new() { ["@phone"] = phone.Trim() }, ct);
                return rows.Select(r => new CustomerCandidate
                {
                    CustomerNo = Txt(r, "customer_no"),
                    CustomerName = Txt(r, "customer_name"),
                    SamplePhone = Txt(r, "sample_phone_no"),
                    OrderCount = Lng(r, "order_count"),
                    LatestOrderDate = Dt(r, "latest_order_date")
                }).ToList();
            });

        // ---------------------------------------------------------------- vouchers

        public Task<List<VoucherSummary>> GetVouchersAsync(string customerNo, CancellationToken ct) =>
            Cached($"vch:{customerNo}", async () =>
            {
                var rows = await QueryAsync(@"
                    SELECT c.deposit_no, c.customer_no, c.customer_name, c.voucher_status,
                           c.deposit_amount, c.deposit_used, c.deposit_pending, c.deposit_balance,
                           c.sales_person_name, c.sales_person_phone,
                           MAX(u.doc_date) AS latest_usage_date
                    FROM [dbo].[vvd_voucher_customer_detail] AS c
                    LEFT JOIN [dbo].[vvd_voucher_usage_detail] AS u ON u.deposit_no = c.deposit_no
                    WHERE c.customer_no = @customer_no
                    GROUP BY c.deposit_no, c.customer_no, c.customer_name, c.voucher_status,
                             c.deposit_amount, c.deposit_used, c.deposit_pending, c.deposit_balance,
                             c.sales_person_name, c.sales_person_phone
                    ORDER BY CASE WHEN c.voucher_status = N'Active' THEN 0 ELSE 1 END,
                             latest_usage_date DESC, c.deposit_no",
                    new() { ["@customer_no"] = customerNo }, ct);
                return rows.Select(r => new VoucherSummary
                {
                    DepositNo = Txt(r, "deposit_no"),
                    CustomerNo = Txt(r, "customer_no"),
                    CustomerName = Txt(r, "customer_name"),
                    VoucherStatusRaw = Txt(r, "voucher_status"),
                    DepositAmount = Dec(r, "deposit_amount"),
                    DepositUsed = Dec(r, "deposit_used"),
                    DepositPending = Dec(r, "deposit_pending"),
                    DepositBalance = Dec(r, "deposit_balance"),
                    SalesPersonName = Txt(r, "sales_person_name"),
                    SalesPersonPhone = Txt(r, "sales_person_phone"),
                    LatestUsageDate = Dt(r, "latest_usage_date")
                }).ToList();
            });

        public async Task<VoucherFlow?> GetVoucherFlowAsync(string customerNo, string? depositNo, CancellationToken ct)
        {
            // เวาเชอร์ต้องเป็นของลูกค้ารายนี้เท่านั้น (ไม่ระบุ = เล่มแรก เหมือนระบบเดิม)
            var vouchers = await GetVouchersAsync(customerNo, ct);
            var voucher = string.IsNullOrWhiteSpace(depositNo)
                ? vouchers.FirstOrDefault()
                : vouchers.FirstOrDefault(v => v.DepositNo == depositNo);
            if (voucher is null) return null;

            return await CachedOne($"flow:{customerNo}:{voucher.DepositNo}", async () =>
            {
                var p = new Dictionary<string, object?> { ["@deposit_no"] = voucher.DepositNo };
                var docs = await QueryAsync(@"
                    SELECT u.deposit_no, u.doc_type, u.doc_no, u.doc_date, u.ref_no, u.status, u.unit_price
                    FROM [dbo].[vvd_voucher_usage_detail] AS u
                    WHERE u.deposit_no = @deposit_no
                    ORDER BY u.doc_date DESC, u.doc_no", p, ct);

                var lines = await QueryAsync(@"
                    SELECT u.deposit_no, u.doc_type, u.doc_no, u.doc_date, u.ref_no, u.status,
                           d.line_no, d.item_no, d.description, d.location_code, d.qty, d.unit_of_measure,
                           d.unit_price AS line_unit_price
                    FROM [dbo].[vvd_voucher_usage_detail] AS u
                    LEFT JOIN [dbo].[vvd_voucher_document_detail] AS d
                        ON d.doc_no = u.doc_no AND d.doc_type = u.doc_type
                    WHERE u.deposit_no = @deposit_no
                    ORDER BY u.doc_date DESC, u.doc_no, d.line_no", p, ct);

                var refs = await QueryAsync(@"
                    SELECT TOP (100)
                           u.deposit_no, u.doc_type AS usage_doc_type, u.doc_no AS usage_doc_no,
                           u.doc_date AS usage_doc_date, u.ref_no AS usage_ref_no,
                           o.doc_type AS order_doc_type, o.doc_no AS order_doc_no,
                           o.doc_date AS order_doc_date, o.ref_no AS order_ref_no
                    FROM [dbo].[vvd_voucher_usage_detail] AS u
                    LEFT JOIN [dbo].[vvd_voucher_order_detail] AS o
                        ON o.customer_no = @customer_no
                       AND ( (NULLIF(LTRIM(RTRIM(u.ref_no)), '') IS NOT NULL AND o.doc_no = u.ref_no)
                          OR (NULLIF(LTRIM(RTRIM(o.ref_no)), '') IS NOT NULL AND o.ref_no = u.doc_no)
                          OR o.doc_no = u.doc_no
                          OR (NULLIF(LTRIM(RTRIM(o.ref_no)), '') IS NOT NULL
                              AND NULLIF(LTRIM(RTRIM(u.ref_no)), '') IS NOT NULL
                              AND o.ref_no = u.ref_no) )
                    WHERE u.deposit_no = @deposit_no
                    ORDER BY u.doc_date DESC, u.doc_no, o.doc_date DESC, o.doc_no",
                    new() { ["@customer_no"] = customerNo, ["@deposit_no"] = voucher.DepositNo }, ct);

                return new VoucherFlow(voucher, docs, lines, refs);
            });
        }

        public async Task<List<Redemption>> GetRedemptionsAsync(string customerNo, string depositNo, CancellationToken ct)
        {
            var flow = await GetVoucherFlowAsync(customerNo, depositNo, ct);
            if (flow is null || flow.Voucher.DepositNo != depositNo) return new();

            var soByUsage = new Dictionary<string, string>();
            foreach (var r in flow.OrderReferences)
            {
                if (!string.Equals(Txt(r, "order_doc_type"), "so", StringComparison.OrdinalIgnoreCase)) continue;
                var so = Txt(r, "order_doc_no");
                if (so.Length == 0) continue;
                foreach (var key in new[] { Txt(r, "usage_doc_no"), Txt(r, "usage_ref_no") })
                    if (key.Length > 0) soByUsage.TryAdd(key, so);
            }

            var result = new List<Redemption>();
            int i = 0;
            foreach (var r in flow.UsageLines.Where(r => Txt(r, "deposit_no") == depositNo))
            {
                var docNo = Txt(r, "doc_no");
                var refNo = Txt(r, "ref_no");
                var isSo = Txt(r, "doc_type").Equals("so", StringComparison.OrdinalIgnoreCase);
                var so = isSo ? "" : soByUsage.GetValueOrDefault(docNo) ?? soByUsage.GetValueOrDefault(refNo) ?? "";
                var qty = Dec(r, "qty");
                result.Add(new Redemption
                {
                    Id = $"{depositNo}-{(docNo.Length > 0 ? docNo : i.ToString())}-{Txt(r, "line_no")}-{i}",
                    Date = Dt(r, "doc_date") ?? DateTime.Today,
                    SalesOrder = so,
                    Document = docNo,
                    DocType = Txt(r, "doc_type"),
                    CreditNote = refNo,
                    Description = Txt(r, "description") is { Length: > 0 } d ? d : Txt(r, "item_no") is { Length: > 0 } n ? n : docNo,
                    ItemNo = Txt(r, "item_no"),
                    Quantity = qty == 0 ? 1 : qty,
                    Unit = Txt(r, "unit_of_measure"),
                    Amount = Math.Abs(Dec(r, "line_unit_price"))
                });
                i++;
            }
            return result;
        }

        private async Task<T> CachedOne<T>(string key, Func<Task<T>> factory)
        {
            if (_cache.TryGetValue(key, out T? hit) && hit is not null) return hit;
            var value = await factory();
            _cache.Set(key, value, CacheTtl);
            return value;
        }

        // ---------------------------------------------------------------- orders

        public Task<List<OrderGroup>> GetOrdersAsync(string customerNo, CancellationToken ct) =>
            Cached($"ord:{customerNo}", async () =>
            {
                var recent = await QueryAsync(@"
                    SELECT TOP (100) o.customer_no, o.customer_name, o.phone_no, o.doc_type, o.doc_no,
                           o.doc_date, o.ref_no, o.status, o.sale_order_type, o.unit_price
                    FROM [dbo].[vvd_voucher_order_detail] AS o
                    WHERE o.customer_no = @customer_no
                    ORDER BY o.doc_date DESC, o.doc_no",
                    new() { ["@customer_no"] = customerNo }, ct);

                var docNos = recent.Select(r => Txt(r, "doc_no")).Where(x => x.Length > 0).Distinct().ToList();
                var lines = new List<Dictionary<string, object?>>();
                if (docNos.Count > 0)
                {
                    var ps = new Dictionary<string, object?>();
                    var names = docNos.Select((d, idx) => { ps[$"@d{idx}"] = d; return $"@d{idx}"; });
                    lines = await QueryAsync($@"
                        SELECT d.doc_type, d.doc_no, d.doc_date, d.line_no, d.item_no, d.description,
                               d.location_code, d.qty, d.unit_of_measure, d.unit_price
                        FROM [dbo].[vvd_voucher_document_detail] AS d
                        WHERE d.doc_no IN ({string.Join(", ", names)})
                        ORDER BY d.doc_date DESC, d.doc_no, d.line_no", ps, ct);
                }
                return OrderGrouping.Build(recent, lines);
            });

        // ---------------------------------------------------------------- helpers

        private async Task<List<T>> Cached<T>(string key, Func<Task<List<T>>> factory)
        {
            if (_cache.TryGetValue(key, out List<T>? hit) && hit is not null) return hit;
            var value = await factory();
            _cache.Set(key, value, CacheTtl);
            return value;
        }

        private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, Dictionary<string, object?> parameters, CancellationToken ct)
        {
            var rows = new List<Dictionary<string, object?>>();
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rd.FieldCount; i++) row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }

        internal static string Txt(Dictionary<string, object?> r, string k) =>
            r.TryGetValue(k, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!.Trim() : "";
        internal static decimal Dec(Dictionary<string, object?> r, string k) =>
            r.TryGetValue(k, out var v) && v is not null ? Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture) : 0m;
        internal static long Lng(Dictionary<string, object?> r, string k) =>
            r.TryGetValue(k, out var v) && v is not null ? Convert.ToInt64(v) : 0;
        internal static DateTime? Dt(Dictionary<string, object?> r, string k) =>
            r.TryGetValue(k, out var v) && v is DateTime d ? d : null;
    }
}
