using System.Globalization;

namespace CustomerDepositRemaining.Models
{
    public class CustomerCandidate
    {
        public string CustomerNo { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string SamplePhone { get; set; } = "";
        public long OrderCount { get; set; }
        public DateTime? LatestOrderDate { get; set; }
    }

    public enum VoucherStatus { Active, Inactive, Expired }

    public class VoucherSummary
    {
        public string DepositNo { get; set; } = "";
        public string CustomerNo { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string VoucherStatusRaw { get; set; } = "";
        public decimal DepositAmount { get; set; }
        public decimal DepositUsed { get; set; }
        public decimal DepositPending { get; set; }
        public decimal DepositBalance { get; set; }
        public string SalesPersonName { get; set; } = "";
        public string SalesPersonPhone { get; set; } = "";
        public DateTime? LatestUsageDate { get; set; }

        public VoucherStatus Status => VoucherStatusRaw.Trim().ToLowerInvariant() switch
        {
            "active" => VoucherStatus.Active,
            "expired" => VoucherStatus.Expired,
            _ => VoucherStatus.Inactive
        };
    }

    /// <summary>หนึ่งบรรทัดสินค้าในประวัติการใช้เวาเชอร์ (Redemption)</summary>
    public class Redemption
    {
        public string Id { get; set; } = "";
        public DateTime Date { get; set; }
        public string SalesOrder { get; set; } = "";
        public string Document { get; set; } = "";
        public string DocType { get; set; } = "";
        public string CreditNote { get; set; } = "";
        public string PdfKind => Services.Pdf.PdfKinds.FromDocType(DocType) ?? Services.Pdf.PdfKinds.Invoice;
        public string Description { get; set; } = "";
        public string ItemNo { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
        public string Unit { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class RedemptionBill
    {
        public string Id { get; set; } = "";
        public Redemption First => Items[0];
        public List<Redemption> Items { get; set; } = new();
        public decimal Amount => Items.Sum(i => i.Amount);
        public IEnumerable<KeyValuePair<string, decimal>> QuantitiesByUnit =>
            Items.GroupBy(i => i.Unit).Select(g => new KeyValuePair<string, decimal>(g.Key, g.Sum(x => x.Quantity)));
    }

    public class DocumentLine
    {
        public string Id { get; set; } = "";
        public string DocType { get; set; } = "";
        public string DocNo { get; set; } = "";
        public int LineNo { get; set; }
        public string ItemNo { get; set; } = "";
        public string Description { get; set; } = "";
        public string LocationCode { get; set; } = "";
        public decimal Qty { get; set; } = 1;
        public string UnitOfMeasure { get; set; } = "PCS";
        public decimal UnitPrice { get; set; }
    }

    public class OrderGroupDocument
    {
        public string DocType { get; set; } = "";
        public string DocNo { get; set; } = "";
        public string RefNo { get; set; } = "";
        public string DocDate { get; set; } = "";
    }

    public class OrderGroup
    {
        public string Id { get; set; } = "";
        public string CustomerNo { get; set; } = "";
        public string DocumentDate { get; set; } = "";
        public string SalesOrderNo { get; set; } = "";
        public string SalesInvoiceNo { get; set; } = "";
        public string CreditNoteNo { get; set; } = "";
        public string StatusRaw { get; set; } = "";
        public string PaymentMethod { get; set; } = "";
        public decimal TotalAmount { get; set; }
        public string? AmountDocType { get; set; }
        public List<OrderGroupDocument> Documents { get; set; } = new();
        public List<DocumentLine> Items { get; set; } = new();

        public OrderStatus Status => OrderStatusMap.FromRaw(StatusRaw);
        public DateTime Date => DateTime.TryParse(DocumentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
        public string DisplayNo =>
            new[] { SalesOrderNo, SalesInvoiceNo, CreditNoteNo }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    }

    public enum OrderStatus
    {
        PendingInspection, InProduction, WaitingReproduction, Assembling, Shipping,
        Delivered, DeliveryFailed, RefundPending, Cancelled
    }

    public static class OrderStatusMap
    {
        public static OrderStatus FromRaw(string? value)
        {
            var n = (value ?? "").Trim().ToLowerInvariant();
            if (n.Contains("fail")) return OrderStatus.DeliveryFailed;
            if (n.Contains("cancel")) return OrderStatus.Cancelled;
            if (n.Contains("pending approval")) return OrderStatus.RefundPending;
            if (n.Contains("open")) return OrderStatus.PendingInspection;
            if (n.Contains("delivered") || n.Contains("closed")) return OrderStatus.Delivered;
            if (n.Contains("release")) return OrderStatus.InProduction;
            return OrderStatus.PendingInspection;
        }

        public static string Label(OrderStatus s) => s switch
        {
            OrderStatus.PendingInspection => "รอตรวจสินค้า",
            OrderStatus.InProduction => "กำลังผลิต",
            OrderStatus.WaitingReproduction => "รอผลิตใหม่",
            OrderStatus.Assembling => "กำลังส่งประกอบ",
            OrderStatus.Shipping => "กำลังจัดส่ง",
            OrderStatus.Delivered => "จัดส่งสำเร็จ",
            OrderStatus.DeliveryFailed => "จัดส่งไม่สำเร็จ",
            OrderStatus.RefundPending => "รอคืนเงิน",
            OrderStatus.Cancelled => "ยกเลิกคำสั่งซื้อ",
            _ => ""
        };

        /// <summary>class ของ .status-badge ในระบบเดิม: success / danger / neutral / warning</summary>
        public static string Kind(OrderStatus s) => s switch
        {
            OrderStatus.Delivered => "success",
            OrderStatus.Cancelled or OrderStatus.DeliveryFailed => "danger",
            _ => "warning"
        };

        public static int Progress(OrderStatus s) => s switch
        {
            OrderStatus.Delivered => 3,
            OrderStatus.InProduction => 1,
            _ => 0
        };

        public static string VoucherLabel(VoucherStatus s) => s switch
        {
            VoucherStatus.Active => "ใช้งานอยู่",
            VoucherStatus.Expired => "หมดอายุ",
            _ => "ใช้หมดแล้ว"
        };

        public static string VoucherKind(VoucherStatus s) => s switch
        {
            VoucherStatus.Active => "success",
            VoucherStatus.Expired => "danger",
            _ => "neutral"
        };
    }

    public static class Fmt
    {
        private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        private static readonly CultureInfo Th = CultureInfo.GetCultureInfo("th-TH");
        private static readonly string[] ThMonths = { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

        public static string Number(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero).ToString("#,##0", En);
        public static string Thb(decimal v) => $"{Number(v)} THB";
        public static string DateNumeric(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        public static string DateThai(DateTime d) => $"{d.Day:00} {ThMonths[d.Month - 1]} {d.Year + 543}";
        public static string ThaiMonth(int m) => ThMonths[m - 1];
        public static string Quantity(decimal v, string unit) => $"{Number(v)} {(string.IsNullOrWhiteSpace(unit) ? "ไม่ระบุ" : unit.Trim())}";
        public static string MaskPhone(string p) => p.Length < 6 ? p : $"{p[..3]}****{p[^3..]}";
    }

    // ---------- View models ----------

    public class AdminLoginViewModel { public string? Username { get; set; } public string? Error { get; set; } }
    public class AdminLookupViewModel { public string? Phone { get; set; } public string? Error { get; set; } }

    public class LoginViewModel { public string? Phone { get; set; } public string? Error { get; set; } }

    public class VerifyViewModel
    {
        public string RequestId { get; set; } = "";
        public string Phone { get; set; } = "";
        public string? Error { get; set; }
        public int ResendAfterSeconds { get; set; } = 59;
        public int ExpiresInSeconds { get; set; }
        public string ReferenceCode { get; set; } = "";
        public bool DemoMode { get; set; }
    }

    public class StoreListViewModel
    {
        public List<(CustomerCandidate Store, int ActiveVouchers)> Stores { get; set; } = new();
    }

    public class ShellModel
    {
        public string Active { get; set; } = "vouchers";
        public string StoreName { get; set; } = "";
    }

    public class VoucherListViewModel
    {
        public string? Search { get; set; }
        public string Tab { get; set; } = "active";
        public int Page { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public decimal ActiveBalance { get; set; }
        public List<VoucherSummary> Items { get; set; } = new();
    }

    public class VoucherDetailViewModel
    {
        public VoucherSummary Voucher { get; set; } = new();
        public int? Year { get; set; }
        public List<int> Years { get; set; } = new();
        public List<RedemptionBill> Bills { get; set; } = new();
        public List<Redemption> AllRedemptions { get; set; } = new();
        public string? Expanded { get; set; }
    }

    public class OrderListViewModel
    {
        public string? Search { get; set; }
        public bool Cancelled { get; set; }
        public bool Ascending { get; set; }
        public List<OrderGroup> Items { get; set; } = new();
    }
}
