using System.Globalization;
using CustomerDepositRemaining.Models;

namespace CustomerDepositRemaining.Services
{
    /// <summary>
    /// รวมแถวจาก vvd_voucher_order_detail ให้เป็นออเดอร์ระดับบิล (port จาก build_order_groups ใน Python)
    /// เชื่อมเอกสารด้วย doc_no/ref_no (union-find); ref_no ว่างไม่นับเป็นเส้นเชื่อม
    /// </summary>
    public static class OrderGrouping
    {
        private static string T(Dictionary<string, object?> r, string k) => VoucherLookupService.Txt(r, k);

        private static string DateKey(Dictionary<string, object?> r) =>
            r.TryGetValue("doc_date", out var v) && v is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : T(r, "doc_date");

        private static int Rank(string docType) => docType.Trim().ToLowerInvariant() switch
        {
            "posted si" => 0, "si" => 1, "so" => 2, "cn" => 3, _ => 9
        };

        public static List<OrderGroup> Build(List<Dictionary<string, object?>> recent, List<Dictionary<string, object?>> docLines)
        {
            var parent = new Dictionary<string, string>();
            string Find(string x)
            {
                if (!parent.ContainsKey(x)) parent[x] = x;
                if (parent[x] != x) parent[x] = Find(parent[x]);
                return parent[x];
            }
            void Union(string a, string b)
            {
                var ra = Find(a); var rb = Find(b);
                if (ra != rb) parent[rb] = ra;
            }

            foreach (var r in recent)
            {
                var doc = T(r, "doc_no"); var rf = T(r, "ref_no");
                if (doc.Length == 0) continue;
                Find(doc);
                if (rf.Length > 0) Union(doc, rf);
            }

            var grouped = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var r in recent)
            {
                var doc = T(r, "doc_no");
                if (doc.Length == 0) continue;
                var root = Find(doc);
                if (!grouped.TryGetValue(root, out var list)) grouped[root] = list = new();
                list.Add(r);
            }

            var linesByKey = docLines
                .Where(l => T(l, "doc_no").Length > 0)
                .GroupBy(l => (T(l, "doc_type").ToLowerInvariant(), T(l, "doc_no")))
                .ToDictionary(g => g.Key, g => g.OrderBy(l => (int)VoucherLookupService.Lng(l, "line_no")).ToList());

            var result = new List<OrderGroup>();
            foreach (var (groupId, rows0) in grouped)
            {
                var rows = rows0.OrderByDescending(r => DateKey(r)).ThenByDescending(r => T(r, "doc_no"), StringComparer.Ordinal).ToList();
                var amountType = new[] { "Posted SI", "SI", "SO", "CN" }
                    .FirstOrDefault(dt => rows.Any(r => T(r, "doc_type").Equals(dt, StringComparison.OrdinalIgnoreCase)));
                var amountRows = amountType is null
                    ? rows
                    : rows.Where(r => T(r, "doc_type").Equals(amountType, StringComparison.OrdinalIgnoreCase)).ToList();
                var amountDocNos = amountRows.Select(r => T(r, "doc_no")).Where(x => x.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

                var itemRows = new List<Dictionary<string, object?>>();
                foreach (var no in amountDocNos)
                    if (linesByKey.TryGetValue(((amountType ?? "").ToLowerInvariant(), no), out var ls)) itemRows.AddRange(ls);

                var items = itemRows.Select(l => new DocumentLine
                {
                    Id = $"{T(l, "doc_type")}:{T(l, "doc_no")}:{T(l, "line_no")}",
                    DocType = T(l, "doc_type"),
                    DocNo = T(l, "doc_no"),
                    LineNo = (int)VoucherLookupService.Lng(l, "line_no"),
                    ItemNo = T(l, "item_no"),
                    Description = T(l, "description") is { Length: > 0 } d ? d : T(l, "item_no"),
                    LocationCode = T(l, "location_code"),
                    Qty = VoucherLookupService.Dec(l, "qty") is var q && q != 0 ? q : 1,
                    UnitOfMeasure = T(l, "unit_of_measure") is { Length: > 0 } u ? u : "PCS",
                    UnitPrice = VoucherLookupService.Dec(l, "unit_price")
                }).ToList();

                if (items.Count == 0)
                {
                    int idx = 1;
                    items = amountRows.Select(r => new DocumentLine
                    {
                        Id = $"{T(r, "doc_type")}:{T(r, "doc_no")}:{idx}",
                        DocType = T(r, "doc_type"),
                        DocNo = T(r, "doc_no"),
                        LineNo = idx++,
                        Description = T(r, "sale_order_type") is { Length: > 0 } s ? s : T(r, "doc_no"),
                        Qty = 1,
                        UnitOfMeasure = "PCS",
                        UnitPrice = VoucherLookupService.Dec(r, "unit_price")
                    }).ToList();
                }

                string Pick(params string[] types)
                {
                    var wanted = types.Select(t => t.ToLowerInvariant()).ToHashSet();
                    return rows.Where(r => wanted.Contains(T(r, "doc_type").ToLowerInvariant()) && T(r, "doc_no").Length > 0)
                               .OrderBy(r => Rank(T(r, "doc_type"))).ThenBy(r => T(r, "doc_no"), StringComparer.Ordinal)
                               .Select(r => T(r, "doc_no")).FirstOrDefault() ?? "";
                }

                var statusRow = amountType is null
                    ? rows.FirstOrDefault()
                    : rows.FirstOrDefault(r => T(r, "doc_type").Equals(amountType, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();

                result.Add(new OrderGroup
                {
                    Id = groupId,
                    CustomerNo = T(rows[0], "customer_no"),
                    DocumentDate = rows.Max(DateKey) ?? "",
                    SalesOrderNo = Pick("SO"),
                    SalesInvoiceNo = Pick("Posted SI", "SI"),
                    CreditNoteNo = Pick("CN"),
                    StatusRaw = statusRow is null ? "" : T(statusRow, "status"),
                    PaymentMethod = amountRows.Count > 0 ? T(amountRows[0], "sale_order_type") : "",
                    TotalAmount = items.Sum(i => i.UnitPrice),
                    AmountDocType = amountType,
                    Items = items,
                    Documents = rows
                        .Where(r => T(r, "doc_no").Length > 0)
                        .Select(r => new OrderGroupDocument { DocType = T(r, "doc_type"), DocNo = T(r, "doc_no"), RefNo = T(r, "ref_no"), DocDate = DateKey(r) })
                        .DistinctBy(d => (d.DocType, d.DocNo, d.RefNo, d.DocDate))
                        .OrderBy(d => Rank(d.DocType)).ThenBy(d => d.DocNo, StringComparer.Ordinal)
                        .ToList()
                });
            }
            return result.OrderByDescending(g => g.DocumentDate, StringComparer.Ordinal).ToList();
        }
    }
}
