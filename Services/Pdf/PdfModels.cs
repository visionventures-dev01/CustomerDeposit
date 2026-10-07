namespace CustomerDepositRemaining.Services.Pdf
{
    public record PdfParty(string Number, string Name, string Phone = "", string Address = "", string TaxId = "");

    public record PdfLine(
        string ItemNo,
        string Description,
        double Quantity,
        string Unit,
        double UnitPrice,
        double Discount = 0,
        double? Amount = null);

    public record PdfDoc(
        string Kind,
        string DocumentNo,
        string DocumentDate,
        PdfParty Customer,
        List<PdfLine> Lines,
        string VoucherNo = "",
        string SourceDocumentNo = "",
        string Salesperson = "-",
        string Remark = "",
        string IssuedBy = "",
        string AmountText = "");

    public static class PdfKinds
    {
        public const string Invoice = "invoice";
        public const string SalesOrder = "sales-order";
        public const string CreditNote = "credit-note";

        /// <summary>_pdf_kind ใน main.py: ค่าที่ไม่รู้จักถือเป็น invoice</summary>
        public static string Normalize(string? value)
        {
            var n = (value ?? "").Trim().ToLowerInvariant();
            if (n is "credit-note" or "credit_note" or "cn") return CreditNote;
            if (n is "sales-order" or "sales_order" or "so") return SalesOrder;
            return Invoice;
        }

        /// <summary>_pdf_kind_from_doc_type: แปลง doc_type ของ DB เป็นชนิดเอกสาร (null = ไม่รู้จัก)</summary>
        public static string? FromDocType(string? value)
        {
            var n = (value ?? "").Trim().ToLowerInvariant();
            if (n is "so" or "sales-order" or "sales order") return SalesOrder;
            if (n is "cn" or "posted cn" or "credit-note" or "credit note" or "credit memo" or "sales credit memo" or "posted sales credit memo") return CreditNote;
            if (n is "si" or "posted si" or "invoice" or "sales invoice") return Invoice;
            return null;
        }
    }
}
