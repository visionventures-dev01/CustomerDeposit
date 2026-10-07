using CustomerDepositRemaining.Models;
using CustomerDepositRemaining.Services.Pdf;

namespace CustomerDepositRemaining.Services
{
    public interface IDocumentPdfService
    {
        /// <summary>คืน null ถ้าไม่พบเอกสารของลูกค้ารายนี้</summary>
        Task<byte[]?> BuildAsync(CustomerCandidate customer, string documentNo, string? kind, string? depositNo, CancellationToken ct);
    }

    /// <summary>port ของ _build_live_redemption_pdf / _live_document_kind (backend/main.py)</summary>
    public class DocumentPdfService : IDocumentPdfService
    {
        private readonly IVoucherLookupService _lookup;
        private readonly PdfRenderer _renderer;

        public DocumentPdfService(IVoucherLookupService lookup, PdfRenderer renderer)
        {
            _lookup = lookup; _renderer = renderer;
        }

        private static string T(Dictionary<string, object?> r, string k) => VoucherLookupService.Txt(r, k);
        private static double Num(Dictionary<string, object?> r, string k) => (double)VoucherLookupService.Dec(r, k);
        private static string DateText(Dictionary<string, object?> r, string k) =>
            r.TryGetValue(k, out var v) && v is DateTime d ? d.ToString("yyyy-MM-dd") : T(r, k);

        private sealed record Row(string ItemNo, string RawItemNo, string DocNo, string Description, double Qty, string Unit, double Amount, string DocDate);

        public async Task<byte[]?> BuildAsync(CustomerCandidate customer, string documentNo, string? kindIn, string? depositNo, CancellationToken ct)
        {
            documentNo = (documentNo ?? "").Trim();
            if (documentNo.Length == 0) return null;

            var flow = await _lookup.GetVoucherFlowAsync(customer.CustomerNo, depositNo, ct);
            if (flow is null) return null;
            var orders = await _lookup.GetOrdersAsync(customer.CustomerNo, ct);

            var kind = ResolveKind(flow, orders, documentNo, PdfKinds.Normalize(kindIn));
            var depositNoResolved = flow.Voucher.DepositNo;

            List<Row> rows;
            string docDate;
            string reference;

            if (kind == PdfKinds.SalesOrder)
            {
                var refs = flow.OrderReferences
                    .Where(r => T(r, "order_doc_type").Equals("so", StringComparison.OrdinalIgnoreCase) && T(r, "order_doc_no") == documentNo)
                    .ToList();
                var group = orders.FirstOrDefault(gp => gp.Documents.Any(d => d.DocNo == documentNo && d.DocType.Equals("so", StringComparison.OrdinalIgnoreCase)));
                if (group is null && refs.Count == 0) return null;

                if (group is { Items.Count: > 0 })
                    rows = group.Items.Select(i => new Row(i.ItemNo.Length > 0 ? i.ItemNo : i.DocNo, i.ItemNo, i.DocNo, i.Description, (double)i.Qty, i.UnitOfMeasure, Math.Abs((double)i.UnitPrice), "")).ToList();
                else
                    rows = refs.Select(r => new Row(T(r, "order_doc_no"), "", T(r, "order_doc_no"), "", 1, "", 0, DateText(r, "order_doc_date"))).ToList();

                docDate = refs.Count > 0 && DateText(refs[0], "order_doc_date") is { Length: > 0 } od ? od
                        : group?.DocumentDate is { Length: > 0 } gd ? gd : DateTime.Today.ToString("yyyy-MM-dd");
                reference = "";
            }
            else
            {
                var key = kind == PdfKinds.CreditNote ? "ref_no" : "doc_no";
                var lines = flow.UsageLines.Where(r => T(r, key) == documentNo).ToList();
                var docs = flow.UsageDocuments.Where(r => T(r, key) == documentNo).ToList();
                if (lines.Count == 0 && docs.Count == 0) return null;

                rows = (lines.Count > 0 ? lines : docs).Select(r =>
                {
                    var qty = Num(r, "qty") is var q && q != 0 ? q : 1;
                    var amount = Math.Abs(Num(r, "line_unit_price") is var lp && lp != 0 ? lp : Num(r, "unit_price"));
                    var itemNo = T(r, "item_no");
                    return new Row(itemNo.Length > 0 ? itemNo : T(r, "doc_no"), itemNo, T(r, "doc_no"),
                                   T(r, "description"), qty, T(r, "unit_of_measure"), amount, DateText(r, "doc_date"));
                }).ToList();

                var firstSrc = lines.Count > 0 ? lines[0] : docs[0];
                docDate = DateText(firstSrc, "doc_date") is { Length: > 0 } fd ? fd
                        : docs.Count > 0 && DateText(docs[0], "doc_date") is { Length: > 0 } dd ? dd
                        : DateTime.Today.ToString("yyyy-MM-dd");
                reference = kind == PdfKinds.CreditNote ? (docs.Count > 0 ? T(docs[0], "doc_no") : "") : depositNoResolved;
            }

            var pdfLines = new List<PdfLine>();
            double total = 0;
            foreach (var r in rows)
            {
                var qty = r.Qty == 0 ? 1 : r.Qty;
                var unitPrice = qty != 0 ? r.Amount / qty : r.Amount;
                total += r.Amount;
                var first = r.Description.Length > 0 ? r.Description : r.RawItemNo.Length > 0 ? r.RawItemNo : r.DocNo;
                var desc = string.Join("\n", new[] { first, r.RawItemNo }.Where(p => p.Length > 0));
                pdfLines.Add(new PdfLine(r.ItemNo.Length > 0 ? r.ItemNo : r.DocNo, desc, qty,
                                         r.Unit.Length > 0 ? r.Unit : "PCS", unitPrice, 0, r.Amount));
            }
            if (depositNoResolved.Length > 0 && kind != PdfKinds.SalesOrder)
            {
                pdfLines.Add(new PdfLine("2134100",
                    string.Join("\n", new[] { "รายได้รับล่วงหน้า Voucher (Deposit-Lens)", depositNoResolved }.Where(p => p.Length > 0)),
                    -1, "", total, 0, -total));
            }

            return _renderer.Build(new PdfDoc(
                kind, documentNo, docDate,
                new PdfParty(customer.CustomerNo, customer.CustomerName, customer.SamplePhone),
                pdfLines, depositNoResolved, reference, flow.Voucher.SalesPersonName));
        }

        private static string ResolveKind(VoucherFlow flow, List<OrderGroup> orders, string documentNo, string fallback)
        {
            foreach (var r in flow.UsageDocuments)
                if (T(r, "doc_no") == documentNo) return PdfKinds.FromDocType(T(r, "doc_type")) ?? fallback;
            foreach (var r in flow.OrderReferences)
            {
                if (T(r, "order_doc_no") == documentNo) return PdfKinds.FromDocType(T(r, "order_doc_type")) ?? fallback;
                if (T(r, "usage_doc_no") == documentNo) return PdfKinds.FromDocType(T(r, "usage_doc_type")) ?? fallback;
            }
            foreach (var g in orders)
                foreach (var d in g.Documents)
                    if (d.DocNo == documentNo) return PdfKinds.FromDocType(d.DocType) ?? fallback;
            return fallback;
        }
    }
}
