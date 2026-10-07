using CustomerDepositRemaining.Services;
using Microsoft.AspNetCore.Mvc;

namespace CustomerDepositRemaining.Controllers
{
    public class DocumentController : LensControllerBase
    {
        private readonly IDocumentPdfService _pdf;
        private readonly ILogger<DocumentController> _log;

        public DocumentController(IVoucherLookupService lookup, IDocumentPdfService pdf, ILogger<DocumentController> log) : base(lookup)
        {
            _pdf = pdf; _log = log;
        }

        /// <summary>PDF เอกสาร (ใบแจ้งหนี้ / ใบยืนยันสั่งซื้อ / ใบลดหนี้) ของร้านที่เลือกอยู่เท่านั้น</summary>
        [HttpGet]
        public async Task<IActionResult> Pdf(string no, string? kind, string? deposit, CancellationToken ct)
        {
            var customer = (await Lookup.FindCustomersAsync(Phone, ct)).First(c => c.CustomerNo == CustomerNo);
            byte[]? bytes;
            try { bytes = await _pdf.BuildAsync(customer, no, kind, deposit, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "PDF generation failed for document {Doc}", no);
                return StatusCode(503, "ไม่สามารถสร้างเอกสาร PDF ได้ในขณะนี้");
            }
            if (bytes is null) return NotFound("ไม่พบเอกสาร");

            var safe = new string((no ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.ContentDisposition = $"inline; filename=\"{(safe.Length > 0 ? safe : "document")}.pdf\"";
            return File(bytes, "application/pdf");
        }
    }
}
