using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace CustomerDepositRemaining.Services.Pdf
{
    public class PdfOptions
    {
        public string? FontPath { get; set; }
        public string? BoldFontPath { get; set; }
    }

    /// <summary>
    /// สร้าง PDF (Letter) ตามเลย์เอาต์ของ backend/app/pdf_documents.py (ReportLab) ด้วย PDFsharp
    /// หน่วยเป็น point, origin มุมซ้ายบน
    /// </summary>
    public sealed class PdfRenderer
    {
        private const double PageW = 612, PageH = 792;
        private const double Left = 50.5, Right = 49.5, Top = 19;
        private const double ContentW = PageW - Left - Right; // 512
        private const double Mm = 72.0 / 25.4;
        private const double BottomMargin = 78 * Mm;

        private readonly IWebHostEnvironment _env;
        private readonly PdfOptions _opt;

        public PdfRenderer(IWebHostEnvironment env, Microsoft.Extensions.Options.IOptions<PdfOptions> opt)
        {
            _env = env; _opt = opt.Value;
            ThaiFontResolver.Configure(_opt);
        }

        // ---- fonts & text helpers --------------------------------------------------
        private sealed record Style(XFont Font, double Leading);

        private static XFont F(double size, bool bold) =>
            new("LensThai", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

        private static string Money(double v, bool paren = false) =>
            paren && v < 0 ? $"({Money(Math.Abs(v))})" : v.ToString("#,##0.00", CultureInfo.InvariantCulture);

        private static string Qty(double v, bool paren = false) =>
            paren && v < 0 ? $"({Math.Abs(v).ToString("0.######", CultureInfo.InvariantCulture)})" : v.ToString("0.######", CultureInfo.InvariantCulture);

        private static string DocDate(string value)
        {
            if (value.Length >= 10 && DateTime.TryParse(value[..10], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            return value;
        }

        private static (string Thai, string English, string Ref) Labels(string kind) => kind switch
        {
            PdfKinds.CreditNote => ("ตัวอย่างใบลดหนี้", "Sample - Credit Note", "Invoice No."),
            PdfKinds.SalesOrder => ("ตัวอย่างใบยืนยันการสั่งซื้อ", "Sample - Sales Order", "Document No."),
            _ => ("ตัวอย่างใบแจ้งหนี้", "Sample - Invoice", "Document No.")
        };

        private static List<string> Wrap(XGraphics g, string text, XFont font, double width)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
            {
                if (para.Length == 0) { lines.Add(""); continue; }
                var cur = "";
                foreach (var ch in para)
                {
                    var next = cur + ch;
                    if (cur.Length > 0 && g.MeasureString(next, font).Width > width)
                    {
                        // ตัดที่ช่องว่างล่าสุดถ้ามี (ภาษาไทยไม่มีช่องว่าง จึงตัดที่ตัวอักษร)
                        var sp = cur.LastIndexOf(' ');
                        if (sp > 0 && ch != ' ') { lines.Add(cur[..sp]); cur = cur[(sp + 1)..] + ch; }
                        else
                        {
                            // ไม่ตัดวรรณยุกต์/สระบนล่างออกจากพยัญชนะ
                            if (IsCombining(ch)) { cur = next; continue; }
                            lines.Add(cur); cur = ch.ToString().TrimStart();
                        }
                    }
                    else cur = next;
                }
                lines.Add(cur);
            }
            return lines;
        }

        private static bool IsCombining(char c) =>
            c == '\u0E31' || (c >= '\u0E34' && c <= '\u0E3A') || (c >= '\u0E47' && c <= '\u0E4E');

        private static double Draw(XGraphics g, string text, XFont font, double x, double y, double width, double leading,
                                   XStringAlignment align = XStringAlignment.Near)
        {
            var lines = Wrap(g, text, font, width);
            double cy = y;
            foreach (var line in lines)
            {
                var w = g.MeasureString(line, font).Width;
                var px = align switch
                {
                    XStringAlignment.Center => x + (width - w) / 2,
                    XStringAlignment.Far => x + width - w,
                    _ => x
                };
                g.DrawString(line, font, XBrushes.Black, new XPoint(px, cy), XStringFormats.TopLeft);
                cy += leading;
            }
            return lines.Count * leading;
        }

        private static double Height(XGraphics g, string text, XFont font, double width, double leading) =>
            Wrap(g, text, font, width).Count * leading;

        // ---- document --------------------------------------------------------------
        // ตัวเลขทั้งหมดวัดจาก PDF ที่ ReportLab สร้างจริง: SimpleDocTemplate มี padding ของ frame 6pt,
        // Table ที่กว้างกว่า frame ถูกจัดกึ่งกลาง (เริ่มที่ x=50.5), cell ปกติมี padding ซ้ายขวา 6 / บนล่าง 3
        private const double FramePad = 6;

        public byte[] Build(PdfDoc doc)
        {
            var kind = doc.Kind;
            bool isInvoice = kind == PdfKinds.Invoice;
            bool isSo = kind == PdfKinds.SalesOrder;
            bool isCn = kind == PdfKinds.CreditNote;

            var pdf = new PdfDocument();
            pdf.Info.Title = doc.DocumentNo;
            pdf.Info.Author = "VV Distribution";
            pdf.Info.Subject = kind;

            var normal = F(8.4, false); const double normalLead = 11;
            var small = F(7.7, false); const double smallLead = 10;
            var smallBold = F(7.7, true);
            var heading = F(11, true); const double headingLead = 12.8;
            var title = F(10.4, true); const double titleLead = 12.1;
            var head = F(6.8, true); const double headLead = 8.2;
            var body = F(8.1, false); const double bodyLead = 10.3;

            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(PageW); page.Height = XUnit.FromPoint(PageH);
            var g = XGraphics.FromPdfPage(page);

            // header table (กว้าง 512 วางที่ x=50.5, เริ่มใต้ padding ของ frame)
            double frameTop = Top + FramePad;
            double y = frameTop;
            DrawLogo(g, Left, y, 24 * Mm, 7.1 * Mm, heading);
            Draw(g, "บริษัท วีวี ดิสทริบิวชั่น จำกัด สำนักงานใหญ่", heading, Left + 80, y, 262, headingLead);
            var (thai, english, refLabel) = Labels(kind);
            double th = 0;
            th += Draw(g, thai, title, Left + 80 + 262, y + th, 170, titleLead, XStringAlignment.Far);
            th += Draw(g, english, title, Left + 80 + 262, y + th, 170, titleLead, XStringAlignment.Far);
            th += Draw(g, "หน้า / Page  1 / 1", title, Left + 80 + 262, y + th, 170, titleLead, XStringAlignment.Far);
            y += Math.Max(th, headingLead) + 21.0;

            // customer / meta block: เส้นบน 1pt, padding บน 8, ล่าง 3; ตารางซ้อน padding ซ้าย 6
            g.DrawLine(new XPen(XColors.Black, 1), Left, y, Left + ContentW, y);
            var customerRows = new List<(string, string)>
            {
                ("รหัสลูกค้า/No.", doc.Customer.Number),
                ("ลูกค้า/Customer", doc.Customer.Name)
            };
            if (doc.Customer.Address.Length > 0) customerRows.Add(("", doc.Customer.Address));
            if (doc.Customer.Phone.Length > 0) customerRows.Add(("โทรศัพท์/Phone No.", doc.Customer.Phone));
            if (doc.Customer.TaxId.Length > 0) customerRows.Add(("ภาษี/Tax ID", doc.Customer.TaxId));
            var metaRows = new List<(string, string)>
            {
                ("เลขที่/Document No.", doc.DocumentNo),
                ("วันที่/Document Date", DocDate(doc.DocumentDate))
            };
            if (doc.SourceDocumentNo.Length > 0) metaRows.Add(($"เลขที่อ้างอิง/{refLabel}", doc.SourceDocumentNo));
            metaRows.Add(("พนักงานขาย/Salesperson", doc.Salesperson.Length > 0 ? doc.Salesperson : "-"));

            double infoTop = y + 8;
            double Block(List<(string, string)> rows, double x)
            {
                double cy = infoTop;
                foreach (var (label, value) in rows)
                {
                    var hl = label.Length > 0 ? Height(g, label, smallBold, 113, smallLead) : 0;
                    var hv = Height(g, value, small, 139, smallLead);
                    if (label.Length > 0) Draw(g, label, smallBold, x, cy + 3, 113, smallLead);
                    Draw(g, value, small, x + 115, cy + 3, 139, smallLead);
                    cy += Math.Max(Math.Max(hl, hv), smallLead) + 6;
                }
                return cy - infoTop;
            }
            var bh = Math.Max(Block(customerRows, Left + 6), Block(metaRows, Left + 256 + 6));
            y = infoTop + bh + 3 + (isInvoice ? 26.8 : isCn ? 8.2 : 15.6) * Mm;

            // items table
            double[] cw = { 25, 85, 160, 31, 32, 67, 55, 57 };
            double[] cx = new double[cw.Length + 1];
            cx[0] = Left; for (int i = 0; i < cw.Length; i++) cx[i + 1] = cx[i] + cw[i];

            bool parenQty = isInvoice || isCn;
            bool parenAmt = isInvoice;
            double tableBottom = PageH - BottomMargin - FramePad;

            void DrawHeader(XGraphics gg, ref double yy)
            {
                string[] heads =
                {
                    "ลำดับ\nNo.", "รหัสรายการ\nItem No.", "คำอธิบาย\nDescription", "จำนวน\nQty", "หน่วยนับ\nUOM",
                    "ราคาขาย\nUnit Price", isSo ? "ส่วนลด %\nDisc. %" : "ส่วนลด\nDiscount\nAmount", "จำนวนเงิน\nAmount"
                };
                double top = yy + 4, hmax = 0;
                for (int i = 0; i < heads.Length; i++)
                {
                    var h = Draw(gg, heads[i], head, cx[i] + 2, top, cw[i] - 4, headLead, XStringAlignment.Center);
                    hmax = Math.Max(hmax, h);
                }
                yy = top + hmax + 4;
                gg.DrawLine(new XPen(XColors.Black, 1), Left, yy, Left + ContentW, yy);
            }

            DrawHeader(g, ref y);
            int n = 0;
            foreach (var line in doc.Lines)
            {
                n++;
                var amount = line.Amount ?? (line.Quantity * line.UnitPrice) - line.Discount;
                var desc = line.Description;
                var rowH = Math.Max(Height(g, desc, body, cw[2] - 4, bodyLead), Height(g, line.ItemNo, body, cw[1] - 4, bodyLead));
                rowH = Math.Max(rowH, bodyLead);
                if (y + 4 + rowH + 4 > tableBottom)
                {
                    g.Dispose();
                    page = pdf.AddPage();
                    page.Width = XUnit.FromPoint(PageW); page.Height = XUnit.FromPoint(PageH);
                    g = XGraphics.FromPdfPage(page);
                    y = frameTop;
                    DrawHeader(g, ref y);
                }
                var ty = y + 4;
                Draw(g, n.ToString(CultureInfo.InvariantCulture), body, cx[0] + 2, ty, cw[0] - 4, bodyLead, XStringAlignment.Center);
                Draw(g, line.ItemNo, body, cx[1] + 2, ty, cw[1] - 4, bodyLead);
                Draw(g, desc, body, cx[2] + 2, ty, cw[2] - 4, bodyLead);
                Draw(g, Qty(line.Quantity, parenQty), body, cx[3] + 2, ty, cw[3] - 4, bodyLead, XStringAlignment.Far);
                Draw(g, line.Unit, body, cx[4] + 2, ty, cw[4] - 4, bodyLead, XStringAlignment.Far);
                Draw(g, Money(line.UnitPrice), body, cx[5] + 2, ty, cw[5] - 4, bodyLead, XStringAlignment.Far);
                Draw(g, DiscountValue(line, isSo), body, cx[6] + 2, ty, cw[6] - 4, bodyLead, XStringAlignment.Far);
                Draw(g, Money(amount, parenAmt), body, cx[7] + 2, ty, cw[7] - 4, bodyLead, XStringAlignment.Far);
                y += 4 + rowH + 4;
            }

            // footer (หน้าแรกเท่านั้น เหมือนระบบเดิม)
            if (pdf.PageCount == 1)
            {
                DrawFooter(g, doc, isInvoice, normal, normalLead, small, smallLead, smallBold);
                g.Dispose();
            }
            else
            {
                g.Dispose();
                using var first = XGraphics.FromPdfPage(pdf.Pages[0]);
                DrawFooter(first, doc, isInvoice, normal, normalLead, small, smallLead, smallBold);
            }

            using var ms = new MemoryStream();
            pdf.Save(ms, false);
            return ms.ToArray();
        }

        private static string DiscountValue(PdfLine line, bool isSo)
        {
            if (line.Discount == 0) return "";
            if (!isSo) return Money(line.Discount);
            if (line.Discount <= 100) return line.Discount.ToString("0.######", CultureInfo.InvariantCulture);
            var gross = Math.Abs(line.Quantity * line.UnitPrice);
            return gross == 0 ? "" : (line.Discount / gross * 100).ToString("0.00", CultureInfo.InvariantCulture);
        }

        // ท้ายกระดาษ: วางจากล่างขึ้นบนเหมือน _draw_footer ของ ReportLab (ความสูงแต่ละส่วนวัดจากของเดิม)
        private void DrawFooter(XGraphics g, PdfDoc doc, bool isInvoice, XFont normal, double normalLead,
                                XFont small, double smallLead, XFont smallBold)
        {
            double productTotal = doc.Lines.Where(l => l.Quantity > 0).Sum(l => l.Quantity * l.UnitPrice);
            double depositTotal = Math.Abs(doc.Lines.Where(l => l.Amount is < 0).Sum(l => l.Amount ?? 0));
            double voucherExtra = Math.Max(productTotal - depositTotal, 0);
            double qtyTotal = doc.Lines.Where(l => l.Quantity > 0 && l.Unit.Trim().ToUpperInvariant() == "PCS").Sum(l => Math.Abs(l.Quantity));

            double bottom = PageH - (isInvoice ? 10 : 9) * Mm;

            // ลายเซ็น/จัดทำโดย (สูง 61.35: 26 + 18 + 17.35)
            const double issuedH = 61.35;
            double issuedTop = bottom - issuedH;
            Draw(g, "จัดทำโดย\nIssued By", smallBold, Left, issuedTop + 6, 170, smallLead, XStringAlignment.Center);
            Draw(g, doc.IssuedBy, small, Left, issuedTop + 26 + 6, 170, smallLead, XStringAlignment.Center);
            g.DrawLine(new XPen(XColors.Black, 0.75), Left, issuedTop + 26 + 18, Left + 170, issuedTop + 26 + 18);
            var dateLabel = isInvoice ? "Date/วันที่" : "วันที่/Date";
            DrawLabelValue(g, dateLabel, "  " + DocDate(doc.DocumentDate), smallBold, small, Left, issuedTop + 26 + 18 + 5);

            // เส้นประ (ตารางว่างสูง 12)
            double dashTop = issuedTop - 12;
            var dash = new XPen(XColors.Black, 0.75) { DashPattern = new double[] { 2, 2 } };
            g.DrawLine(dash, Left, dashTop, Left + ContentW, dashTop);

            // ข้อความจำนวนเงิน หรือช่องว่าง 10mm (invoice) / 5mm
            double spacer = doc.AmountText.Length > 0
                ? Height(g, doc.AmountText, small, ContentW, smallLead)
                : (isInvoice ? 10 : 5) * Mm;
            double amountTop = dashTop - spacer;
            if (doc.AmountText.Length > 0) Draw(g, doc.AmountText, small, Left, amountTop, ContentW, smallLead);

            // หมายเหตุ + จำนวนชิ้นรวม
            double remarkH = 4 + Math.Max(20, doc.Remark.Length > 0 ? Height(g, doc.Remark, normal, 357, normalLead) : 0);
            double remarkTop = amountTop - remarkH;
            Draw(g, "หมายเหตุ\nRemark", smallBold, Left, remarkTop + 4, 70, smallLead);
            Draw(g, doc.Remark, normal, Left + 70, remarkTop + 4, 357, normalLead);
            Draw(g, $"{Qty(qtyTotal)} PCS", smallBold, Left + 70 + 357, remarkTop + 4, 85, smallLead, XStringAlignment.Far);

            if (isInvoice)
            {
                double sumTop = remarkTop - 16;
                g.DrawLine(new XPen(XColors.Black, 1), Left, sumTop, Left + ContentW, sumTop);
                DrawLabelValue(g, "มูลค่าสินค้า:", " " + Money(productTotal), smallBold, small, Left, sumTop + 6);
                DrawLabelValue(g, "หัก Deposit:", " " + Money(depositTotal), smallBold, small, Left + 125, sumTop + 6);
                DrawLabelValue(g, "หัก Voucher Extra:", " " + Money(voucherExtra), smallBold, small, Left + 245, sumTop + 6);
            }
            else
            {
                g.DrawLine(new XPen(XColors.Black, 1), Left, remarkTop - 12, Left + ContentW, remarkTop - 12);
            }
        }

        /// <summary>ข้อความตัวหนา (label) ตามด้วยข้อความปกติ (value) ในบรรทัดเดียว</summary>
        private static void DrawLabelValue(XGraphics g, string label, string value, XFont boldFont, XFont font, double x, double y)
        {
            g.DrawString(label, boldFont, XBrushes.Black, new XPoint(x, y), XStringFormats.TopLeft);
            var w = g.MeasureString(label, boldFont).Width;
            g.DrawString(value, font, XBrushes.Black, new XPoint(x + w, y), XStringFormats.TopLeft);
        }

        private void DrawLogo(XGraphics g, double x, double y, double w, double h, XFont fallback)
        {
            var path = Path.Combine(_env.ContentRootPath, "Assets", "vv-distribution-logo.jpg");
            if (File.Exists(path))
            {
                using var img = XImage.FromFile(path);
                g.DrawImage(img, x, y, w, h);
            }
            else
            {
                g.DrawString("VV DISTRIBUTION", fallback, XBrushes.Black, new XPoint(x, y), XStringFormats.TopLeft);
            }
        }
    }

    /// <summary>ใช้ Tahoma (หรือฟอนต์ที่ตั้งค่าไว้) เหมือนระบบเดิม; Windows มีให้อยู่แล้ว</summary>
    internal sealed class ThaiFontResolver : IFontResolver
    {
        private static ThaiFontResolver? _instance;
        private readonly string _regular;
        private readonly string _bold;

        private ThaiFontResolver(string regular, string bold) { _regular = regular; _bold = bold; }

        public static void Configure(PdfOptions opt)
        {
            if (_instance is not null) return;
            var regular = First(opt.FontPath, @"C:\Windows\Fonts\tahoma.ttf", @"C:\Windows\Fonts\THSarabunNew.ttf",
                "/usr/share/fonts/truetype/noto/NotoSansThai-Regular.ttf", "/usr/share/fonts/opentype/noto/NotoSansThai-Regular.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                ?? throw new InvalidOperationException("ไม่พบฟอนต์ภาษาไทยสำหรับสร้าง PDF (ตั้งค่า Pdf:FontPath)");
            var bold = First(opt.BoldFontPath, @"C:\Windows\Fonts\tahomabd.ttf", @"C:\Windows\Fonts\THSarabunNew Bold.ttf") ?? regular;
            _instance = new ThaiFontResolver(regular, bold);
            GlobalFontSettings.FontResolver = _instance;
        }

        private static string? First(params string?[] paths) =>
            paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            new(isBold ? "LensThai-Bold" : "LensThai-Regular");

        public byte[]? GetFont(string faceName) =>
            File.ReadAllBytes(faceName == "LensThai-Bold" ? _bold : _regular);
    }
}
